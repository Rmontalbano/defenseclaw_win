using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The verbs a Govern row can offer. A flags value so a row can say, in one field, which buttons it
/// shows; the same enum (one flag at a time) is the parameter of <see cref="GovernRow.ActionCommand"/>.
/// <see cref="Info"/> is the read-only "show me the detail" verb of the panel: <c>skill info</c>,
/// <c>plugin info</c> or <c>tool status</c>.
/// </summary>
[Flags]
public enum GovernVerbs
{
    None = 0,
    Info = 1 << 0,
    Block = 1 << 1,
    Allow = 1 << 2,
    Unblock = 1 << 3,
    Disable = 1 << 4,
    Enable = 1 << 5,
    Quarantine = 1 << 6,
    Restore = 1 << 7,
    Remove = 1 << 8,
    Unset = 1 << 9,
    CopyName = 1 << 10,

    /// <summary><c>&lt;noun&gt; scan [--connector C] -- NAME</c>: runs the scanner and records the result (reviewed first).</summary>
    Scan = 1 << 11,

    /// <summary>
    /// "Open in Registries" (CUST-276): offered on a skill or MCP server a registry promoted. A navigation, not a command: it shows the Registries
    /// panel on the item's cached entry and runs nothing, so it is not reviewed and is not held back by a stale list or a read-only installation.
    /// </summary>
    OpenInRegistries = 1 << 12,
}

/// <summary>What a row calls back into when one of its buttons is pressed. Implemented by the panel view-model.</summary>
public interface IGovernRowHost
{
    void OnRowAction(GovernRow row, GovernVerbs verb);

    /// <summary>
    /// Whether the row menu's state-changing items are enabled now: false while the list is partial, failed, still being read or
    /// old (see <see cref="CatalogTrust"/>). Info and Copy name do not depend on it.
    /// </summary>
    bool AreChangesAllowed => true;

    /// <summary>Why <see cref="AreChangesAllowed"/> is false (the items' tooltip); null when it is true.</summary>
    string? ChangesBlockedReason => null;

    /// <summary>Whether the row menu's Scan item is enabled now (not while another command runs, not with the scanner missing).</summary>
    bool IsScanAvailable => true;
}

/// <summary>One label/value pair in a row's details expander.</summary>
public sealed record GovernField(string Label, string Value);

/// <summary>
/// One skill, MCP server, plugin or tool rule as listed by <c>defenseclaw &lt;noun&gt; list --json</c>.
/// <para>
/// One shape for all four panels so they share a single row view. Everything the view needs is
/// precomputed here (tones, labels, which buttons apply); the view-model that parsed the row decides
/// the values, the row only carries them. Rows are immutable and rebuilt on every refresh.
/// </para>
/// </summary>
public sealed partial class GovernRow : INotifyPropertyChanged
{
    private readonly IGovernRowHost _host;
    private string? _key;
    private string? _searchText;

    public GovernRow(IGovernRowHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ActionCommand = new RelayCommand<string>(Invoke);
    }

    /// <summary>The CLI group this row belongs to: skill, mcp, plugin or tool.</summary>
    public required string Noun { get; init; }

    /// <summary>What the CLI takes as the target argument (a plugin's id, a tool's bare name, …).</summary>
    public required string Name { get; init; }

    /// <summary>Human title; usually <see cref="Name"/>.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>The connector this row lives under; null when the CLI did not say (or for a global tool rule).</summary>
    public string? Connector { get; init; }

    /// <summary>Tool rules only: <c>connector</c>, <c>global</c> or <c>source</c> (audit-only).</summary>
    public string? RuleScope { get; init; }

    /// <summary>Tool rules only: the source a source-scoped rule belongs to (<c>--source</c>).</summary>
    public string? SourceScope { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// The registry source that promoted this skill or MCP server into the allow policy (the id in its rule's <c>registry:&lt;id&gt;</c> reason, see
    /// <see cref="RegistryAttribution"/>); null for an item no registry promoted, and always for plugins and tools.
    /// </summary>
    public string? RegistrySource { get; init; }

    /// <summary>One compact line of secondary facts ("origin: … · version: …").</summary>
    public string? MetaLine { get; init; }

    /// <summary>The state badge (Blocked, Quarantined, Disabled, Allowed, Active, …).</summary>
    public string StateLabel { get; init; } = string.Empty;

    /// <summary>DcBadge tone key for <see cref="StateLabel"/>: Bad, Warn, Ok or Neutral.</summary>
    public string StateTone { get; init; } = "Neutral";

    /// <summary>The scan badge text ("HIGH · 23 findings", "Scan clean", "Not scanned"); empty hides it.</summary>
    public string ScanLabel { get; init; } = string.Empty;

    /// <summary>DcBadge tone key for <see cref="ScanLabel"/>: Critical, High, Medium, Low, Ok or Neutral.</summary>
    public string ScanTone { get; init; } = "Neutral";

    /// <summary>The enforcement summary ("install: block · file: quarantine"); empty when none.</summary>
    public string? ActionsText { get; init; }

    public string? Reason { get; init; }

    public string? UpdatedText { get; init; }

    /// <summary>Shown on a de-emphasized row that is not really an item of this kind.</summary>
    public string? ArtifactNote { get; init; }

    public bool IsArtifact { get; init; }

    public bool IsBlocked { get; init; }

    public bool IsAllowed { get; init; }

    public bool IsQuarantined { get; init; }

    public bool IsDisabled { get; init; }

    public bool NeedsAttention { get; init; }

    /// <summary>The exact JSON object the row was parsed from, indented.</summary>
    public string RawJson { get; init; } = string.Empty;

    public IReadOnlyList<GovernField> Fields { get; init; } = Array.Empty<GovernField>();

    /// <summary>The verbs offered on this row; the view shows one button per flag.</summary>
    public GovernVerbs Verbs { get; init; } = GovernVerbs.Info;

    /// <summary>"Info" for skills/plugins, "Status" for tools.</summary>
    public string InfoLabel { get; init; } = "Info";

    public ICommand ActionCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the "Scan…" menu item can be used right now; the panel calls <see cref="RefreshScanEnabled"/> when that changes.</summary>
    public bool ScanEnabled => _host.IsScanAvailable && _host.AreChangesAllowed;

    public void RefreshScanEnabled() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScanEnabled)));

    /// <summary>
    /// Whether the menu's state-changing items (allow, block, enable, disable, quarantine, restore, remove, scan, ...) are usable. Read
    /// from the panel each time, so a row built before the list went partial or stale is not left offering what it offered.
    /// </summary>
    public bool ChangesEnabled => _host.AreChangesAllowed;

    /// <summary>Why <see cref="ChangesEnabled"/> is false; null when it is true. Shown as the disabled items' tooltip.</summary>
    public string? ChangesBlockedReason => _host.ChangesBlockedReason;

    /// <summary>The panel's trust in its list changed: the change items and the Scan item re-read it.</summary>
    public void RefreshChangesEnabled()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangesEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChangesBlockedReason)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScanEnabled)));
    }

    // ---- Table cells (the dense grid): values the columns bind to, read from the details list so no parser changes ----

    private string FieldText(string label) => Fields.FirstOrDefault(f => f.Label == label)?.Value ?? string.Empty;

    public string Version => FieldText("Version");

    public string Origin => FieldText("Origin");

    /// <summary>Where the item came from (a skill's or plugin's <c>source</c>); empty when the CLI did not say.</summary>
    public string SourceText => FieldText("Source");

    public string Transport => FieldText("Transport");

    /// <summary>A tool rule's scope in words ("connector claudecode", "every connector (fallback)").</summary>
    public string AppliesText => FieldText("Applies to");

    /// <summary>An MCP server's launch command, or its URL.</summary>
    public string LaunchText => FieldText("Command").Length > 0 ? FieldText("Command") : FieldText("URL");

    /// <summary>The scan column's words, as the Mac says them: "19 CRITICAL findings" (from "CRITICAL · 19 findings"), "Not scanned".</summary>
    public string ScanCellText
    {
        get
        {
            var match = ScanCountPattern().Match(ScanLabel);
            return match.Success ? $"{match.Groups[2].Value} {match.Groups[1].Value} {match.Groups[3].Value}" : ScanLabel;
        }
    }

    [GeneratedRegex(@"^(\w+) · (\d+) (findings?)$")]
    private static partial Regex ScanCountPattern();

    /// <summary>Identity across refreshes. Computed once: the sync pass asks for it many times per refresh.</summary>
    public string Key => _key ??= $"{RuleScope}|{Connector}|{SourceScope}|{Name}";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Name : Title;

    public string ConnectorLabel => string.IsNullOrWhiteSpace(Connector)
        ? (RuleScope == "global" ? "all connectors (fallback)" : string.Empty)
        : Connector!;

    public bool HasConnector => ConnectorLabel.Length > 0;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasMeta => !string.IsNullOrWhiteSpace(MetaLine);

    public bool HasState => StateLabel.Length > 0;

    public bool HasScan => ScanLabel.Length > 0;

    public bool HasActionsText => !string.IsNullOrWhiteSpace(ActionsText);

    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);

    public bool HasUpdated => !string.IsNullOrWhiteSpace(UpdatedText);

    public bool HasArtifactNote => IsArtifact && !string.IsNullOrWhiteSpace(ArtifactNote);

    public bool HasFields => Fields.Count > 0;

    /// <summary>True when a registry promoted this item: the row shows the <see cref="RegistryBadge"/> and offers Open in Registries.</summary>
    public bool HasRegistry => !string.IsNullOrWhiteSpace(RegistrySource);

    /// <summary>
    /// The badge text, <c>registry:corp-skills</c>: the TUI's <c>registry_badge</c>, so an id past 18 characters is cut and ends in <c>...</c> (the
    /// whole id is in <see cref="RegistryToolTip"/> and the details). The id came from config.yaml, so a control or format character in it is spelled
    /// out. Empty when no registry promoted the item.
    /// </summary>
    public string RegistryBadge => HasRegistry ? DisplayNames.Visible(RegistryAttribution.Badge(RegistrySource)) : string.Empty;

    /// <summary>The whole <c>registry:&lt;id&gt;</c>, uncut (the Registry line of the details); empty without a registry.</summary>
    public string RegistryFull => FullRegistryText(RegistrySource);

    /// <summary><see cref="RegistryFull"/> for a source id, for a parser that fills the details before the row exists.</summary>
    public static string FullRegistryText(string? registrySource) =>
        string.IsNullOrWhiteSpace(registrySource) ? string.Empty : RegistryAttribution.ReasonPrefix + DisplayNames.Visible(registrySource.Trim());

    /// <summary>What hovering the badge says: where the item came from, and that clicking it opens the entry.</summary>
    public string RegistryToolTip => HasRegistry
        ? $"Promoted into the allow policy by the registry source “{DisplayNames.Visible(RegistrySource!.Trim())}”. Click to open its entry in Registries."
        : string.Empty;

    /// <summary>What a screen reader announces for the badge.</summary>
    public string RegistryAutomationName => HasRegistry
        ? $"Promoted by registry {DisplayNames.Visible(RegistrySource!.Trim())}. Opens its entry in Registries."
        : string.Empty;

    public bool CanInfo => (Verbs & GovernVerbs.Info) != 0;

    public bool CanBlock => (Verbs & GovernVerbs.Block) != 0;

    public bool CanAllow => (Verbs & GovernVerbs.Allow) != 0;

    public bool CanUnblock => (Verbs & GovernVerbs.Unblock) != 0;

    public bool CanDisable => (Verbs & GovernVerbs.Disable) != 0;

    public bool CanEnable => (Verbs & GovernVerbs.Enable) != 0;

    public bool CanQuarantine => (Verbs & GovernVerbs.Quarantine) != 0;

    public bool CanRestore => (Verbs & GovernVerbs.Restore) != 0;

    public bool CanRemove => (Verbs & GovernVerbs.Remove) != 0;

    public bool CanUnset => (Verbs & GovernVerbs.Unset) != 0;

    public bool CanScan => (Verbs & GovernVerbs.Scan) != 0;

    public bool CanOpenInRegistries => (Verbs & GovernVerbs.OpenInRegistries) != 0;

    /// <summary>Lower-cased text the panel's filter box matches against.</summary>
    public string SearchText => _searchText ??= string.Join(
        ' ',
        new[] { Name, Title, Connector, Description, MetaLine, StateLabel, ScanLabel, Reason, ActionsText, RegistryFull }
            .Where(s => !string.IsNullOrWhiteSpace(s)))
        .ToLowerInvariant();

    /// <summary>What a screen reader announces for the row.</summary>
    public string AutomationName
    {
        get
        {
            var parts = new List<string> { $"{Noun} {DisplayTitle}" };
            if (HasConnector)
            {
                parts.Add($"connector {ConnectorLabel}");
            }

            if (HasState)
            {
                parts.Add(StateLabel);
            }

            if (HasScan)
            {
                parts.Add(ScanLabel);
            }

            if (HasRegistry)
            {
                parts.Add($"promoted by registry {DisplayNames.Visible(RegistrySource!.Trim())}");
            }

            if (IsArtifact)
            {
                parts.Add("not a real item, a listing artifact");
            }

            return string.Join(", ", parts);
        }
    }

    public override string ToString() => AutomationName;

    private void Invoke(string? verbName)
    {
        if (Enum.TryParse<GovernVerbs>(verbName, ignoreCase: true, out var verb) && verb != GovernVerbs.None)
        {
            _host.OnRowAction(this, verb);
        }
    }
}
