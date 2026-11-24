using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.IO;
using DefenseClaw.Core.Setup;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.SetupResources;

/// <summary>One connector the last discovery scan skipped because its program is in a folder that is not trusted, and the folder to trust.</summary>
/// <param name="Connector">The connector's name.</param>
/// <param name="Directory">The folder its program is in.</param>
public sealed record UntrustedConnectorItem(string Connector, string Directory)
{
    public string AutomationName => $"Trust {Directory} for {Connector}";
}

/// <summary>
/// The trusted binary locations editor: <c>setup trusted-paths list --json</c> as a list with the TUI's columns (Source, Status, Owned, Path), and
/// Remove on the operator's own entries; Add opens the wizard on <c>add</c> (the TUI's inline "directory to trust" box). Three things are particular to it:
/// <list type="bullet">
///   <item><b>Built-ins are protected.</b> A default entry, and one that only the running program's environment names, has no Remove (the CLI refuses
///     it, and the button says why before a review is offered).</item>
///   <item><b>Could not read the allow-list is not an empty allow-list</b> (the TUI's error row): the failed state says the list is unknown, and
///     nothing can be changed until it has been read.</item>
///   <item><b>Connectors in untrusted folders</b> (the TUI's proactive highlight) come from the last discovery scan (<c>agent_discovery.json</c>), since the
///     app has no read-only command that scans; the folders already in the allow-list are left out. Each has a Trust button that opens the Add wizard
///     with the folder filled in. The same is what a failed connector setup hands over: <see cref="Prefill"/> and <see cref="ContextMessage"/>.</item>
/// </list>
/// </summary>
public sealed partial class TrustedPathsViewModel : SetupResourceViewModel
{
    /// <summary>The discovery file is a few kilobytes; a much larger one is not read.</summary>
    private const long MaxDiscoveryBytes = 1 << 20;

    private static readonly SetupColumn[] ColumnList =
    [
        new("Source", "Source", 0.9, 104, SetupColumnKind.Quiet),
        new("Status", "Status", 1.3, 176, SetupColumnKind.State),
        new("Owned", "Owned", 0.5, 70, SetupColumnKind.Quiet),
        new("Path", "Path", 3.2, 220, SetupColumnKind.Mono),
    ];

    /// <param name="services">The composition.</param>
    /// <param name="prefill">A folder to offer to trust (a failed connector setup named it); the Trust button of the banner hands it to the wizard.</param>
    /// <param name="context">Why the operator is here, in a sentence ("Setup of Claude Code stopped: its program is in ..."); empty when they browsed.</param>
    public TrustedPathsViewModel(AppServices services, string prefill = "", string context = "")
        : base(services)
    {
        _prefill = (prefill ?? string.Empty).Trim();
        _contextMessage = (context ?? string.Empty).Trim();
    }

    public override SetupResource Resource => SetupResource.TrustedPaths;

    public override string Title => "Trusted binary locations";

    public override string Subtitle =>
        "Folders DefenseClaw may run connector programs from. Anything in a trusted folder can be run.";

    public override string Singular => "trusted folder";

    public override string Plural => "trusted folders";

    public override IReadOnlyList<SetupColumn> Columns => ColumnList;

    public override bool Offers(SetupVerb verb) => verb is SetupVerb.Add or SetupVerb.Remove;

    public override object? Extras => this;

    public override string EmptyTitle => "The allow-list is empty";

    public override string EmptyDetail =>
        "defenseclaw setup trusted-paths list returned an empty list. No connector programs are trusted. Use Add to trust a folder.";

    public override string FailedTitle => "Could not read the trusted-path allow-list";

    public override string FailedDetail =>
        "The allow-list could not be read and may hold folders not shown here. Nothing can be changed until it is read.";

    protected override string TipFor(SetupVerb verb) => verb switch
    {
        SetupVerb.Add => "Opens the setup wizard to type the folder to trust. Nothing runs until you confirm.",
        SetupVerb.Remove => "Stop trusting this folder. Nothing runs until you confirm.",
        _ => string.Empty,
    };

    protected override bool TryParse(string stdout, out IReadOnlyList<SetupResourceRow> rows, out string error)
    {
        rows = Array.Empty<SetupResourceRow>();
        if (!TrustedPathParser.TryParse(stdout, out var parsed, out error))
        {
            return false;
        }

        rows = parsed.Select(ToRow).ToArray();
        return true;
    }

    internal static SetupResourceRow ToRow(TrustedPath p)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Source"] = p.Source,
            ["Status"] = p.Status,
            ["Owned"] = p.Owned,
            ["Path"] = p.Resolved,
        };

        var facts = new List<SetupFact>
        {
            new("Folder", p.Resolved, Mono: true),
            new("As written", p.Path, Mono: true),
            new("Source", DescribeSource(p)),
            new("Status", DescribeStatus(p)),
            new("Owned", p.Removable ? "yes: you added it, so it can be removed" : "no: it cannot be removed here"),
        };

        var blocked = new Dictionary<SetupVerb, string>();
        if (p.IsBuiltIn)
        {
            blocked[SetupVerb.Remove] = "Built-in defaults are protected. The CLI does not remove them.";
        }
        else if (p.IsEnvironmentOnly)
        {
            blocked[SetupVerb.Remove] = "Only the environment variable DEFENSECLAW_TRUSTED_BIN_PREFIXES names this folder. Remove it from the environment.";
        }
        else if (!p.Removable)
        {
            blocked[SetupVerb.Remove] = "Only a folder you added to config.yaml or .env can be removed here.";
        }

        return new SetupResourceRow(p.Resolved, cells, p.Status, ToneOf(p), facts, p, blocked);
    }

    private static string ToneOf(TrustedPath p) => p.Status.ToLowerInvariant() switch
    {
        "ok" => "Ok",
        "unsafe-permissions" => "Bad",
        "missing" or "not-a-dir" or "error" => "Warn",
        _ => "Neutral",
    };

    private static string DescribeSource(TrustedPath p) => p.Source.ToLowerInvariant() switch
    {
        "default" => "default: built in to DefenseClaw (protected)",
        "config" => "config: ai_discovery.trusted_binary_prefixes in config.yaml",
        "legacy .env" => "legacy .env: DEFENSECLAW_TRUSTED_BIN_PREFIXES in .env (still honored)",
        "env" => "env: only the running program's environment",
        _ => p.Source,
    };

    private static string DescribeStatus(TrustedPath p) => p.Status.ToLowerInvariant() switch
    {
        "ok" => "ok: it exists, is a folder, and no one else can write to it",
        "missing" => "missing: the folder does not exist (yet)",
        "not-a-dir" => "not-a-dir: this path is a file",
        "unsafe-permissions" => "unsafe-permissions: other accounts can write to this folder. The CLI ignores it.",
        "error" => "error: the CLI could not check this folder",
        _ => p.Status,
    };

    internal override SetupChangePlan Plan(SetupVerb verb, SetupResourceRow row)
    {
        if (verb != SetupVerb.Remove)
        {
            throw new ArgumentOutOfRangeException(nameof(verb), verb, "The trusted folders editor has no such change.");
        }

        var p = (TrustedPath)row.Source;
        var argv = SetupResourceArgv.Remove(Resource, p.Resolved);
        return new SetupChangePlan(
            verb,
            row,
            argv,
            $"Stop trusting “{row.Title}”?",
            $"Takes this folder out of the allow-list ({p.Source}). Connector programs in it are no longer found, and action-mode setup for them stops until you trust it again.",
            "Remove folder",
            CommandTier.Destructive,
            ChangeTimeout,

            // Only a config.yaml entry changes config.yaml, which is what makes the setup command restart the gateway; an entry only in .env does not.
            string.Equals(p.Source, "config", StringComparison.OrdinalIgnoreCase),
            Array.Empty<CommandReviewWarning>(),
            $"“{row.Title}” is no longer trusted.");
    }

    // ------------------------------------------------------------------ where the operator came from

    /// <summary>A folder a failed connector setup named; empty when the operator browsed here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrefill))]
    private string _prefill = string.Empty;

    /// <summary>Why the operator is here ("Setup of Claude Code stopped: ..."); empty when they browsed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContext))]
    private string _contextMessage = string.Empty;

    public bool HasContext => ContextMessage.Length > 0;

    public bool HasPrefill => Prefill.Length > 0;

    /// <summary>The window was opened again by a setup that stopped on a folder: it keeps its list and takes the new reason.</summary>
    public void SetContext(string prefill, string context)
    {
        Prefill = (prefill ?? string.Empty).Trim();
        ContextMessage = (context ?? string.Empty).Trim();
        NotifyActions();
    }

    /// <summary>Opens the Add wizard with the folder the failed setup named filled in.</summary>
    [RelayCommand]
    private Task TrustPrefillAsync() => HasPrefill && !RefuseChange(SetupVerb.Add) ? OpenAddWizardAsync(Prefill) : Task.CompletedTask;

    // ------------------------------------------------------------------ connectors in untrusted folders

    /// <summary>The connectors the last discovery scan skipped for the folder their program is in, minus the folders trusted since.</summary>
    public ObservableCollection<UntrustedConnectorItem> UntrustedConnectors { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUntrustedConnectors), nameof(UntrustedHeadline), nameof(HasUntrustedHeadline), nameof(UntrustedTone))]
    private string _untrustedNote = string.Empty;

    public bool HasUntrustedConnectors => UntrustedConnectors.Count > 0;

    /// <summary>The one line over the connectors: how many, or that the scan found none or could not be read. Empty until the first read has finished.</summary>
    public string UntrustedHeadline => UntrustedConnectors.Count switch
    {
        0 => UntrustedNote,
        1 => "1 connector's program is in a folder that is not trusted" + ScanTime,
        _ => $"{UntrustedConnectors.Count.ToString(CultureInfo.CurrentCulture)} connectors' programs are in folders that are not trusted" + ScanTime,
    };

    public bool HasUntrustedHeadline => UntrustedHeadline.Length > 0;

    /// <summary>Warn while there are connectors to trust, Neutral for a scan that found none or could not be read.</summary>
    public string UntrustedTone => UntrustedConnectors.Count > 0 ? "Warn" : "Neutral";

    private string _scanTime = string.Empty;

    private string ScanTime => _scanTime.Length > 0 ? $" (last discovery scan, {_scanTime})" : string.Empty;

    /// <summary>
    /// Reads <c>agent_discovery.json</c> (a file, off the UI thread, and not a large one) once the allow-list has been read, so the folders that are
    /// already trusted can be left out. A file that is missing or unreadable is said so in the note: it is not "no connectors".
    /// </summary>
    protected override async Task AfterReadAsync()
    {
        var path = Services.Paths.AgentDiscoveryStatePath;
        var report = await Task.Run(() => ReadDiscovery(path)).ConfigureAwait(true);
        if (IsDisposed)
        {
            return;
        }

        var trusted = Rows
            .Select(static r => r.Source)
            .OfType<TrustedPath>()
            .Where(static p => p.IsOk)
            .Select(static p => NormalizeFolder(p.Resolved))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = report.Connectors
            .Where(c => !trusted.Contains(NormalizeFolder(c.Directory)))
            .Select(static c => new UntrustedConnectorItem(c.Connector, c.Directory))
            .ToArray();

        UntrustedConnectors.Clear();
        foreach (var item in items)
        {
            UntrustedConnectors.Add(item);
        }

        _scanTime = report.ScannedAt is { } at ? at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty;
        UntrustedNote = !report.Read
            ? report.Note
            : items.Length == 0
                ? "No connector program was found outside a trusted folder in the last discovery scan" + ScanTime + "."
                : string.Empty;
        OnPropertyChanged(nameof(UntrustedHeadline));
        OnPropertyChanged(nameof(HasUntrustedHeadline));
        OnPropertyChanged(nameof(HasUntrustedConnectors));
        OnPropertyChanged(nameof(UntrustedTone));
    }

    private static UntrustedConnectorReport ReadDiscovery(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return UntrustedConnectorReport.Unavailable("No discovery scan has been recorded yet. Run AI Discovery to list connectors outside trusted folders.");
            }

            if (info.Length > MaxDiscoveryBytes)
            {
                return UntrustedConnectorReport.Unavailable("The discovery file is larger than expected, so it was not read.");
            }

            return UntrustedConnectorScan.Read(SharedFile.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UntrustedConnectorReport.Unavailable("The discovery file could not be read, so connectors outside a trusted folder cannot be listed: " + DisplayNames.Visible(ex.Message));
        }
    }

    private static string NormalizeFolder(string folder) => folder.Trim().TrimEnd('\\', '/');

    /// <summary>Trust: opens the Add wizard with this connector's folder filled in.</summary>
    [RelayCommand]
    private async Task TrustAsync(UntrustedConnectorItem? item)
    {
        if (item is null || RefuseChange(SetupVerb.Add))
        {
            return;
        }

        await OpenAddWizardAsync(item.Directory).ConfigureAwait(true);
    }
}
