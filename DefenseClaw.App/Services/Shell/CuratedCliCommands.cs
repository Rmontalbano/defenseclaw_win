using System.Diagnostics;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// One CLI command the palette offers (the Mac's "230 commands" sheet): the noun path as an argv list, a category, and what
/// the CLI's own help says it does. There is no flag in it, so there is nothing a secret could ride on.
/// </summary>
/// <param name="Argv">The nouns, e.g. <c>agent</c>, <c>discovery</c>, <c>scan</c>. Never a shell line.</param>
/// <param name="Category">One of <see cref="CuratedCommandCatalog.Categories"/>.</param>
/// <param name="Summary">The first help sentence.</param>
/// <param name="Usage">The usage line, kept to say what an unrunnable command still needs.</param>
/// <param name="RequiredArguments">Names of the positional arguments it cannot run without; empty when it runs as it stands.</param>
internal sealed record CuratedCommand(
    IReadOnlyList<string> Argv,
    string Category,
    string Summary,
    string Usage,
    IReadOnlyList<string> RequiredArguments)
{
    public string Id => "cli." + string.Join('.', Argv);

    /// <summary>The command as it reads, e.g. <c>defenseclaw agent discovery scan</c>.</summary>
    public string Title => CommandReview.CommandLine(CommandReview.DefaultExecutable, Argv);

    /// <summary>The same command as text to paste into PowerShell (every argument one literal string).</summary>
    public string ClipboardText => CommandReview.ClipboardLine(CommandReview.DefaultExecutable, Argv);

    public bool NeedsArguments => RequiredArguments.Count > 0;

    /// <summary>What the review and the tier policy make of it: read-only ones run as they are, the rest are reviewed first.</summary>
    public CommandTier Tier => CommandReview.ResolveTier(Argv);
}

/// <summary>
/// The palette's curated CLI commands, read from the installed CLI's own <c>--help</c> screens rather than typed in by hand
/// (so a new CLI shows its new commands, and a removed one stops being offered). Walks <c>defenseclaw --help</c>, then each
/// group's help, to leaf commands, through <see cref="SetupHelpProbe"/> (so the screens are cached on disk per CLI build, like the
/// Setup hub's). Only <c>--help</c> is ever run, and no command is run to learn about it.
/// <para>
/// What is kept: a command the Windows policy does not hide (<see cref="WizardWindowsPolicy.HidesCommand"/>: sandbox, OpenClaw,
/// ZeptoClaw, Docker), that the CLI does not itself call unsupported here, and whose argv passes <see cref="Refuses"/>. Capability
/// gating is the probe's: no CLI on this machine, no commands.
/// </para>
/// </summary>
internal sealed class CuratedCommandCatalog
{
    /// <summary>The Mac sheet's categories (Sandbox is never populated here: the policy hides it).</summary>
    public static readonly IReadOnlyList<string> Categories = new[]
    {
        "Daemon", "Enforce", "Info", "Install", "Other", "Policy", "Sandbox", "Scan", "Setup",
    };

    /// <summary>Groups nest at most this deep (<c>setup observability add</c> is three nouns).</summary>
    private const int MaxDepth = 3;

    // Word-fragments of a flag that carry a credential. Matched case-insensitively against any "-"-prefixed token.
    private static readonly string[] SecretFlagFragments =
    {
        "--value", "token", "api-key", "apikey", "secret", "password", "passwd", "passphrase", "credential", "bearer", "--key",
    };

    private static readonly char[] ShellMetacharacters = { ';', '&', '|', '<', '>', '`', '$', '(', ')', '%', '"', '\'', '\\', '*', '?', '~', '^', '\n', '\r' };

    private readonly SetupHelpProbe _probe;
    private readonly object _gate = new();
    private Task? _load;
    private IReadOnlyList<CuratedCommand> _commands = Array.Empty<CuratedCommand>();

    public CuratedCommandCatalog(SetupHelpProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>The commands found so far (empty until <see cref="EnsureLoaded"/> finishes, and when the CLI cannot be read).</summary>
    public IReadOnlyList<CuratedCommand> Commands
    {
        get
        {
            lock (_gate)
            {
                return _commands;
            }
        }
    }

    /// <summary>Null until the walk ends; otherwise why it found nothing (the CLI could not be run).</summary>
    public string? LoadError { get; private set; }

    /// <summary>Raised (off the UI thread) when the walk finished and <see cref="Commands"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Starts the walk once, in the background, and returns it. Never faults; a second call joins the first.</summary>
    public Task EnsureLoaded()
    {
        lock (_gate)
        {
            return _load ??= Task.Run(LoadAsync);
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var root = await _probe.CliHelpAsync(Array.Empty<string>()).ConfigureAwait(false);
            if (!root.Succeeded)
            {
                LoadError = root.Error;
                return;
            }

            var found = new List<CuratedCommand>();
            await WalkAsync(Array.Empty<string>(), root.Text, found).ConfigureAwait(false);

            lock (_gate)
            {
                _commands = found
                    .OrderBy(c => Categories.ToList().IndexOf(c.Category))
                    .ThenBy(c => c.Title, StringComparer.Ordinal)
                    .ToList();
            }

            LoadError = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A palette extra must never fault the app; the entries just stay empty.
        catch (Exception ex)
        {
            LoadError = ex.Message;
            Trace.TraceWarning($"Curated CLI command discovery failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private async Task WalkAsync(IReadOnlyList<string> path, string helpText, List<CuratedCommand> found)
    {
        var parsed = SetupHelpParser.Parse(helpText, Math.Max(path.Count - 1, 0));

        var tasks = new List<Task>();
        foreach (var child in parsed.Commands)
        {
            var childPath = path.Append(child.Name).ToArray();
            if (!IsPlausibleNoun(child.Name) || WizardWindowsPolicy.HidesCommand(childPath, child.Summary))
            {
                continue;
            }

            tasks.Add(VisitAsync(childPath, child.Summary, found));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task VisitAsync(string[] path, string summary, List<CuratedCommand> found)
    {
        var help = await _probe.CliHelpAsync(path).ConfigureAwait(false);
        if (!help.Succeeded)
        {
            return;
        }

        var parsed = SetupHelpParser.Parse(help.Text, path.Length - 1);
        if (parsed.Commands.Count > 0 && path.Length < MaxDepth)
        {
            await WalkAsync(path, help.Text, found).ConfigureAwait(false);
            return;
        }

        // A group at the depth limit is not a command: it would only print its own help.
        if (parsed.Commands.Count > 0 || parsed.PlatformStatus == PlatformStatus.Unsupported)
        {
            return;
        }

        var entry = new CuratedCommand(
            path,
            CategoryFor(path),
            parsed.Summary.Length > 0 ? parsed.Summary : summary,
            parsed.Usage,
            parsed.Positionals.Where(p => p.IsRequired).Select(p => p.Name).ToArray());

        if (!Refuses(entry.Argv))
        {
            lock (found)
            {
                found.Add(entry);
            }
        }
    }

    /// <summary>A command word is lower-case letters, digits and dashes: anything else in a help line is not a noun to put on an argv.</summary>
    internal static bool IsPlausibleNoun(string name) =>
        name.Length > 0 && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>
    /// True when <paramref name="argv"/> must not be offered or run: it is empty, any token has a shell metacharacter, or it
    /// carries a flag that names a credential (<c>--value</c>, <c>--token</c>, <c>--api-key</c> and the like). A palette entry is a
    /// noun path, so none of these is expected - this is the line that makes sure it stays so.
    /// </summary>
    public static bool Refuses(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0)
        {
            return true;
        }

        foreach (var token in argv)
        {
            if (token.Length == 0 || token.IndexOfAny(ShellMetacharacters) >= 0 || token.Any(char.IsWhiteSpace))
            {
                return true;
            }

            if (token.StartsWith('-') && SecretFlagFragments.Any(f => token.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a command line into an argv list with no shell: whitespace separates, nothing else is interpreted. Null when the
    /// text is empty, has a quote or shell metacharacter (it is not a plain noun path), or fails <see cref="Refuses"/>.
    /// </summary>
    public static IReadOnlyList<string>? Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var tokens = commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && string.Equals(tokens[0], CommandReview.DefaultExecutable, StringComparison.OrdinalIgnoreCase))
        {
            tokens = tokens[1..];
        }

        return Refuses(tokens) ? null : tokens;
    }

    /// <summary>The Mac sheet's category for a command, from its nouns.</summary>
    internal static string CategoryFor(IReadOnlyList<string> path)
    {
        if (path.Any(n => n.Contains("scan", StringComparison.OrdinalIgnoreCase)) && !string.Equals(path[0], "setup", StringComparison.Ordinal))
        {
            return "Scan";
        }

        return path[0] switch
        {
            "setup" or "quickstart" or "init" => "Setup",
            "scan" or "scanner" or "aibom" or "inventory" => "Scan",
            "policy" or "guardrail" or "rules" => "Policy",
            "alerts" or "block" or "allow" or "enforce" or "quarantine" or "skill" or "mcp" or "plugin" => "Enforce",
            "install" or "upgrade" or "uninstall" or "update" => "Install",
            "gateway" or "daemon" or "sidecar" or "start" or "stop" or "restart" => "Daemon",
            "status" or "doctor" or "version" or "list" or "show" or "logs" or "audit" or "keys" or "config" or "agent" => "Info",
            "sandbox" => "Sandbox",
            _ => "Other",
        };
    }
}
