using System.Diagnostics;
using System.IO;
using System.Windows;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services;

/// <summary>
/// Whether a scan this app started is running right now: what the tray's "scanning" shield and its tooltip read.
/// <para>
/// <b>What it sees.</b> Every command the app runs goes through <see cref="CliRunner"/>, which raises
/// <see cref="CliRunner.InvocationStarted"/> and, exactly once per invocation however it ends (an exit, a timeout, a cancel, a start that
/// failed), <see cref="CliRunner.InvocationCompleted"/>. A run is a scan when it is one of the commands the TUI files under "scan"
/// (<see cref="IsScan(string, IReadOnlyList{string})"/>): <c>skill scan</c>, <c>mcp scan</c>, <c>plugin scan</c>, <c>aibom scan</c>,
/// <c>agent discovery scan</c>, <c>agent usage --refresh</c> and the gateway's <c>scan code</c>, and the Runtime panel's <c>agent discovery
/// runtime scan</c>, from whichever surface started it (the Overview, the Skills, MCP and Plugins panels, Inventory, AI Discovery, the Runtime
/// panel, the command palette) because they all start it the same way. While one is in flight <see cref="IsScanning"/> is true; two at once
/// count as one scan until the last has finished.
/// </para>
/// <para>
/// <b>What it does not see.</b> A scan the gateway runs by itself (the AI discovery scan its sidecar repeats on a schedule), or one started
/// from a terminal outside the app: the app is told of neither, so neither shows. A hand-off (a command the app only copies to a console,
/// <see cref="CliRunner.RecordHandOff"/>) is born finished and never counts.
/// </para>
/// <para>
/// <b>Cost.</b> No timer and no polling: two event subscriptions that fire when a command starts or ends, and a set of the ids of the scans
/// in flight (empty almost always). <see cref="Changed"/> is raised, on the UI thread, only when <see cref="IsScanning"/> flips, so an idle
/// app, and a busy one running anything but a scan, does nothing here. A handler never throws into the runner: a fault is traced.
/// </para>
/// </summary>
internal sealed class ScanActivity : IDisposable
{
    /// <summary>One scan: the tool it runs under, the leading words of its command, and a flag it needs to be one (null: none).</summary>
    private sealed record ScanVerb(string Tool, string[] Words, string? RequiredFlag = null);

    /// <summary>
    /// The scans, as the leading words of the command: the ones that go through every skill, MCP server, plugin or AI component and print
    /// or record a verdict. They are the commands the TUI's registry files under "scan" (<c>tui/registry_data.py</c> of the 0.8.10 CLI:
    /// <c>skill scan</c>, <c>mcp scan</c>, <c>plugin scan</c>, <c>aibom scan</c>, <c>agent discovery scan</c>, <c>agent usage --refresh</c>, and
    /// CodeGuard's <c>scan code</c> under the gateway binary; <c>setup galileo test</c> shares the category but sends one canary trace, which is
    /// not a scan of anything), plus the Runtime panel's own command (<c>AiRuntimeCommands.ScanCommand</c>, which the pinned source has and 0.8.10
    /// lacks). <c>agent usage</c> without <c>--refresh</c> only renders what is already known.
    /// </summary>
    private static readonly ScanVerb[] ScanVerbs =
    {
        new("defenseclaw", new[] { "skill", "scan" }),
        new("defenseclaw", new[] { "mcp", "scan" }),
        new("defenseclaw", new[] { "plugin", "scan" }),
        new("defenseclaw", new[] { "aibom", "scan" }),
        new("defenseclaw", new[] { "agent", "discovery", "scan" }),
        new("defenseclaw", new[] { "agent", "discovery", "runtime", "scan" }),
        new("defenseclaw", new[] { "agent", "usage" }, RequiredFlag: "--refresh"),
        new("defenseclaw-gateway", new[] { "scan", "code" }),
    };

    private readonly CliRunner _cli;
    private readonly Func<CliInvocation, bool> _isScan;
    private readonly Action<Action> _post;

    /// <summary>Guards the set and the flags below; never held across a callback.</summary>
    private readonly object _lock = new();

    /// <summary>The ids of the scans in flight.</summary>
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>What <see cref="Changed"/> last announced, so a start and an end that cancel out before the UI thread looks raise nothing.</summary>
    private bool _announced;

    private bool _disposed;

    /// <param name="cli">The runner whose invocations are watched.</param>
    /// <param name="isScan">Decides which invocations are scans; <see cref="IsScan(CliInvocation)"/> when null. A test passes its own.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null. A test passes one that runs it in place.</param>
    public ScanActivity(CliRunner cli, Func<CliInvocation, bool>? isScan = null, Action<Action>? post = null)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _isScan = isScan ?? IsScan;
        _post = post ?? PostToDispatcher;

        // Subscribed first, then seeded: a scan that starts in between is in the set once (it is a set), and one that finishes in
        // between is skipped by the seed (FinishedAt is set before InvocationCompleted is raised) or removed by its completion.
        _cli.InvocationStarted += OnStarted;
        _cli.InvocationCompleted += OnCompleted;
        Seed();
    }

    /// <summary>Raised on the UI thread when <see cref="IsScanning"/> has flipped (a scan began while none ran, or the last one ended).</summary>
    public event EventHandler? Changed;

    /// <summary>True while at least one scan is in flight. Readable from any thread.</summary>
    public bool IsScanning
    {
        get
        {
            lock (_lock)
            {
                return _inFlight.Count > 0;
            }
        }
    }

    /// <summary>How many scans are in flight.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>True when <paramref name="invocation"/> is one of the CLI's scans; see <see cref="IsScan(string, IReadOnlyList{string})"/>.</summary>
    public static bool IsScan(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return IsScan(invocation.Executable, invocation.Argv);
    }

    /// <summary>
    /// True when <paramref name="executable"/> and <paramref name="argv"/> are one of the scans (<see cref="ScanVerbs"/>): <c>skill scan --all</c>,
    /// <c>skill scan -- NAME</c>, <c>aibom scan --json</c>, <c>agent discovery runtime scan</c>, <c>agent usage --refresh</c>, and the gateway's
    /// <c>scan code -- PATH</c>; and the command is not only a preview (<c>--help</c>, <c>--dry-run</c>: <see cref="CommandTiers.Classify"/>
    /// reads those as read-only). Only the leading words are looked at, up to the first option, so a target that spells <c>scan</c> is not a
    /// scan, and the executable must be exactly <c>defenseclaw</c> or <c>defenseclaw-gateway</c> (the extension is ignored).
    /// <para>
    /// In the developer runtime selector's container mode the command runs as <c>docker exec [-i] [-e NAME ...] CONTAINER defenseclaw ARGS</c>
    /// (<see cref="RuntimeLaunch.ContainerArguments"/>); that wrapper is looked through.
    /// </para>
    /// </summary>
    public static bool IsScan(string executable, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var tool = Path.GetFileNameWithoutExtension(executable ?? string.Empty);
        if (string.Equals(tool, "docker", StringComparison.OrdinalIgnoreCase) && UnwrapContainer(argv) is { } inner)
        {
            (tool, argv) = inner;
        }

        var words = argv.TakeWhile(word => word.Length > 0 && !word.StartsWith('-')).Take(CommandTiers.MaxPathTokens).ToArray();

        // Options end at "--": what follows is a target, so a path named "--refresh" is not the flag.
        var options = argv.TakeWhile(word => word != "--").Where(word => word.StartsWith('-')).ToArray();

        return ScanVerbs.Any(verb =>
                   string.Equals(verb.Tool, tool, StringComparison.OrdinalIgnoreCase) &&
                   words.Length >= verb.Words.Length &&
                   verb.Words.SequenceEqual(words.Take(verb.Words.Length), StringComparer.OrdinalIgnoreCase) &&
                   (verb.RequiredFlag is null || options.Contains(verb.RequiredFlag, StringComparer.OrdinalIgnoreCase))) &&
               CommandTiers.Classify(argv) != CommandTier.ReadOnly;
    }

    /// <summary>The tool and its arguments inside a <c>docker exec</c> the runner built for a container runtime, or null for any other docker command.</summary>
    private static (string Tool, IReadOnlyList<string> Args)? UnwrapContainer(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || !string.Equals(argv[0], "exec", StringComparison.Ordinal))
        {
            return null;
        }

        var at = 1;
        if (at < argv.Count && argv[at] == "-i")
        {
            at++;
        }

        while (at + 1 < argv.Count && argv[at] == "-e")
        {
            at += 2;
        }

        // The container's name, then the tool.
        at++;
        if (at >= argv.Count || !RuntimeLaunch.RunsInContainer(argv[at]))
        {
            return null;
        }

        return (argv[at], argv.Skip(at + 1).ToArray());
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inFlight.Clear();
        }

        _cli.InvocationStarted -= OnStarted;
        _cli.InvocationCompleted -= OnCompleted;
        Changed = null;
    }

    /// <summary>The runner's event, raised on the thread that started the command. Never throws into the runner.</summary>
    internal void OnStarted(object? sender, CliInvocation invocation)
    {
        try
        {
            // A hand-off is recorded already finished: nothing is running, and its completion would only undo this.
            if (!invocation.IsRunning || !_isScan(invocation))
            {
                return;
            }

            bool began;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                began = _inFlight.Add(invocation.Id) && _inFlight.Count == 1;
            }

            if (began)
            {
                _post(RaiseIfChanged);
            }
        }
#pragma warning disable CA1031 // A tray shield must never be the reason a command fails to start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"scan activity: ignoring a start: {ex.Message}");
        }
    }

    /// <summary>The runner's event, raised once per invocation on a pool thread. Never throws into the runner.</summary>
    internal void OnCompleted(object? sender, CliInvocation invocation)
    {
        try
        {
            // An id never seen starting (not a scan, or finished before the seed) is simply not here.
            bool ended;
            lock (_lock)
            {
                ended = _inFlight.Remove(invocation.Id) && _inFlight.Count == 0;
            }

            if (ended)
            {
                _post(RaiseIfChanged);
            }
        }
#pragma warning disable CA1031 // A tray shield must never be the reason a command fails to finish.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"scan activity: ignoring a completion: {ex.Message}");
        }
    }

    /// <summary>Picks up the scans already running when this was created.</summary>
    private void Seed()
    {
        foreach (var invocation in _cli.Activity)
        {
            if (invocation.IsRunning && _isScan(invocation))
            {
                lock (_lock)
                {
                    _ = _inFlight.Add(invocation.Id);
                }
            }
        }

        lock (_lock)
        {
            _announced = _inFlight.Count > 0;
        }
    }

    /// <summary>On the UI thread: tells the subscriber, unless it was already told what is true now.</summary>
    private void RaiseIfChanged()
    {
        EventHandler? handler;
        lock (_lock)
        {
            var scanning = _inFlight.Count > 0;
            if (_disposed || scanning == _announced)
            {
                return;
            }

            _announced = scanning;
            handler = Changed;
        }

        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // The subscriber is the tray; its fault is traced, not thrown into the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"scan activity: a Changed subscriber threw: {ex}");
        }
    }

    /// <summary>
    /// The application's dispatcher, the way <c>AlertCountsService</c> does it: in place on the UI thread (and when there is no WPF
    /// application, as in a headless run), queued otherwise, and nothing once shutdown has begun.
    /// </summary>
    private static void PostToDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Shutdown began between the check and the post: nothing is left to update.
        }
    }
}
