using System.Diagnostics;
using System.Globalization;
using System.Windows;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>What one run of <c>defenseclaw alerts --json --limit 500</c> came to.</summary>
/// <param name="Succeeded">The command exited 0.</param>
/// <param name="Stdout">What it printed on standard output (lines joined; any line ending).</param>
/// <param name="Failure">Why it did not succeed, in a phrase for the banner; null when it did.</param>
/// <param name="NotSupported">The runtime has no such command or flag ("No such command"): not a failure, nothing to warn about.</param>
internal sealed record RuntimeAlertFetch(bool Succeeded, string Stdout, string? Failure, bool NotSupported = false);

/// <summary>What <see cref="RuntimeAlertsService"/> knows now.</summary>
/// <param name="Rows">The last list the runtime gave that could be read; empty until one has.</param>
/// <param name="FetchedAt">When that list was taken; null until one has.</param>
/// <param name="StaleReason">Why the list could not be refreshed ("the runtime did not answer within 30 s"), or null when it is current. The banner says "Alerts may be stale: " and this.</param>
/// <param name="DetectionOnly24h">Detection-only findings of the last 24 hours, which the runtime's list does not carry; null until counted.</param>
internal sealed record RuntimeAlertState(
    IReadOnlyList<RuntimeAlertRow> Rows,
    DateTimeOffset? FetchedAt,
    string? StaleReason,
    int? DetectionOnly24h)
{
    public static RuntimeAlertState Empty { get; } = new(Array.Empty<RuntimeAlertRow>(), null, null, null);
}

/// <summary>
/// The runtime's own alert list (<c>defenseclaw alerts --json --limit 500</c>, 1.0.0 or later), kept for the Alerts panel to merge into the audit
/// database's queue, plus the count of detection-only findings that list leaves out.
/// <para>
/// <b>Why it exists.</b> The runtime's list is not a subset of the database queue: it also lists enforcement actions, connector hook blocks and health
/// failures, leaves out detection-only findings and names each alert's decision, rule, route and agent (see <see cref="RuntimeAlertSnapshotParser"/>).
/// The queue's definition is evaluated in SQL with functions only the Python runtime registers, so the list cannot be reproduced here; it is asked for.
/// </para>
/// <para>
/// <b>Cadence, and never in the way.</b> <see cref="Request"/> returns at once. A run starts only when the capability is present, none is running, and the
/// last attempt (success or failure) is older than <see cref="MinInterval"/>, or the caller forces one (Refresh, an acknowledge); the panel asks on its
/// 30 s tick, so the process runs about once a minute while the panel is open, and never while it is closed. A run that ends raises
/// <see cref="Changed"/> on the UI thread. A failed run keeps the last good list and sets <see cref="RuntimeAlertState.StaleReason"/>; "No such command"
/// is "not supported" and says nothing.
/// </para>
/// </summary>
internal sealed class RuntimeAlertsService
{
    /// <summary>The least time between two attempts unless one is forced.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long the CLI may take; a Python start plus a 500-row query is a few seconds.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The arguments: the Mac's (<c>AppState</c>), the newest 500, the same window as the queue.</summary>
    public static readonly IReadOnlyList<string> Argv = ["alerts", "--json", "--limit", "500"];

    private readonly Func<bool> _enabled;
    private readonly Func<CancellationToken, Task<RuntimeAlertFetch>> _fetch;
    private readonly Func<DateTimeOffset, CancellationToken, Task<int>> _countDetectionOnly;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    private RuntimeAlertState _state = RuntimeAlertState.Empty;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;
    private bool _running;
    private bool _forcedWhileRunning;
    private bool _unsupported;
    private TaskCompletionSource _settled = CompletedSettled();

    /// <param name="enabled">True while the runtime has <see cref="DefenseClaw.Core.Runtime.RuntimeCapability.RuntimeAlerts"/>; asked at every request.</param>
    /// <param name="fetch">Runs the command; <see cref="FromCli"/> in the app.</param>
    /// <param name="countDetectionOnly">Counts detection-only findings since the given time; null counts nothing.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null.</param>
    /// <param name="time">The clock; the system's when null.</param>
    public RuntimeAlertsService(
        Func<bool> enabled,
        Func<CancellationToken, Task<RuntimeAlertFetch>> fetch,
        Func<DateTimeOffset, CancellationToken, Task<int>>? countDetectionOnly = null,
        Action<Action>? post = null,
        TimeProvider? time = null)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _countDetectionOnly = countDetectionOnly ?? ((_, _) => Task.FromResult(0));
        _post = post ?? PostToDispatcher;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on the UI thread when a run ended and the state differs from before.</summary>
    public event EventHandler? Changed;

    /// <summary>True while the runtime has the capability: the panel merges nothing and shows nothing otherwise.</summary>
    public bool IsEnabled => _enabled();

    /// <summary>What is known now (<see cref="RuntimeAlertState.Empty"/> before the first run, and whenever the capability is absent).</summary>
    public RuntimeAlertState Current
    {
        get
        {
            if (!_enabled())
            {
                return RuntimeAlertState.Empty;
            }

            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Starts a run unless one is due later or already going. <paramref name="force"/> (Refresh, after an acknowledge) ignores the interval; asked
    /// while a run is going, it makes one more run follow, so the list a caller sees is from after its call.
    /// </summary>
    public void Request(bool force)
    {
        if (!_enabled())
        {
            return;
        }

        lock (_lock)
        {
            if (_running)
            {
                _forcedWhileRunning |= force;
                return;
            }

            if (_unsupported && !force)
            {
                return;
            }

            if (!force && _time.GetUtcNow() - _lastAttempt < MinInterval)
            {
                return;
            }

            _running = true;
            _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>One run, then another if a forced request arrived during it. Never throws.</summary>
    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await RunOnceAsync().ConfigureAwait(false);
                lock (_lock)
                {
                    if (!_forcedWhileRunning)
                    {
                        _running = false;
                        _settled.TrySetResult();
                        return;
                    }

                    _forcedWhileRunning = false;
                }
            }
        }
#pragma warning disable CA1031 // A background run that throws has nobody to tell; the list stays as it was.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"runtime alerts: refresh failed: {ex}");
            lock (_lock)
            {
                _running = false;
                _settled.TrySetResult();
            }
        }
    }

    /// <summary>Completes when no run is going (already complete while idle): what a test awaits after <see cref="Request"/>.</summary>
    internal Task Settled
    {
        get
        {
            lock (_lock)
            {
                return _settled.Task;
            }
        }
    }

    private static TaskCompletionSource CompletedSettled()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    private async Task RunOnceAsync()
    {
        var now = _time.GetUtcNow();
        RuntimeAlertFetch fetch;
        using var cts = new CancellationTokenSource(CommandTimeout + TimeSpan.FromSeconds(5));
        try
        {
            fetch = await _fetch(cts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A runner that throws (a missing executable) is a stale list with a reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            fetch = new RuntimeAlertFetch(false, string.Empty, OneLine($"{ex.GetType().Name}: {ex.Message}"));
        }

        // The count of what the list leaves out is independent of the list: a runtime that could not answer still has a database.
        int? detectionOnly = null;
        try
        {
            detectionOnly = await _countDetectionOnly(now - TimeSpan.FromHours(24), cts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A locked or older database only loses the note.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"runtime alerts: detection-only findings could not be counted: {ex.GetType().Name}: {ex.Message}");
        }

        RuntimeAlertState previous;
        RuntimeAlertState next;
        lock (_lock)
        {
            previous = _state;
            _lastAttempt = now;
            _unsupported = fetch.NotSupported;

            var rows = previous.Rows;
            var fetchedAt = previous.FetchedAt;
            string? stale = null;
            if (fetch.NotSupported)
            {
                rows = Array.Empty<RuntimeAlertRow>();
                fetchedAt = null;
            }
            else if (fetch.Succeeded)
            {
                var parsed = RuntimeAlertSnapshotParser.Parse(fetch.Stdout);
                if (parsed.Status == RuntimeAlertParseStatus.Ok)
                {
                    rows = parsed.Rows;
                    fetchedAt = now;
                }
                else
                {
                    stale = "the runtime's alert list was not in the expected form (" + parsed.Message + ")";
                }
            }
            else
            {
                stale = fetch.Failure ?? "the runtime's alert list could not be read";
            }

            next = new RuntimeAlertState(rows, fetchedAt, stale, detectionOnly ?? previous.DetectionOnly24h);
            _state = next;
        }

        if (!Same(previous, next))
        {
            _post(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }

    private static bool Same(RuntimeAlertState a, RuntimeAlertState b) =>
        a.StaleReason == b.StaleReason &&
        a.DetectionOnly24h == b.DetectionOnly24h &&
        a.Rows.Count == b.Rows.Count &&
        a.Rows.Zip(b.Rows).All(pair => ReferenceEquals(pair.First, pair.Second) || pair.First.Id == pair.Second.Id && pair.First.Facts.Count == pair.Second.Facts.Count && pair.First.Severity == pair.Second.Severity);

    /// <summary>The command through the app's read-only runner (<c>alerts --json</c> is classified read-only); the whole output is kept, since a list cut short is not parsed.</summary>
    public static Func<CancellationToken, Task<RuntimeAlertFetch>> FromCli(CliRunner cli)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var options = CliRunOptions.WithTimeout(CommandTimeout) with { RetainFullOutput = true, Background = true };
        return async token => Interpret(await cli.RunAsync(Argv, cancellationToken: token, options: options).ConfigureAwait(false));
    }

    /// <summary>What a finished run means for the list. Public for the tests.</summary>
    internal static RuntimeAlertFetch Interpret(CliInvocation run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var stdout = string.Join('\n', run.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));
        var stderr = run.OutputLines.Where(l => l.Stream == CliStream.StandardError).Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();

        if (run.FailureReason is { Length: > 0 } stopped)
        {
            return new RuntimeAlertFetch(false, stdout, OneLine($"the runtime's alert command {stopped}"));
        }

        if (run.ExitCode is not 0)
        {
            // Click's usage errors: the command or the flag is not in this runtime.
            if (stderr.Any(IsNotSupported))
            {
                return new RuntimeAlertFetch(false, stdout, null, NotSupported: true);
            }

            var reason = stderr.Count > 0 ? stderr[^1] : "exit code " + (run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
            return new RuntimeAlertFetch(false, stdout, OneLine("the runtime's alert command failed: " + reason));
        }

        if (run.IsOutputTruncated)
        {
            return new RuntimeAlertFetch(false, stdout, "the runtime's alert list was longer than this app keeps of one command's output");
        }

        return new RuntimeAlertFetch(true, stdout, null);
    }

    /// <summary>Click's own wording for an unknown command or option.</summary>
    internal static bool IsNotSupported(string stderrLine) =>
        stderrLine.Contains("No such command", StringComparison.OrdinalIgnoreCase) ||
        stderrLine.Contains("No such option", StringComparison.OrdinalIgnoreCase);

    private static string OneLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

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
            // Shutdown began between the check and the post.
        }
    }
}
