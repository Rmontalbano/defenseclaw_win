using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The detail pane of a row that no scan printed (CUST-376): an IDE plugin or an agent identity, both read from the gateway.
/// </summary>
/// <param name="Key">A stable key for the row (the plugin's fingerprint, the identity's agent id), so a re-read keeps the selection.</param>
/// <param name="Title">The pane's heading.</param>
/// <param name="Fields">The labelled values, in order.</param>
/// <param name="AgentId">The agent id of an identity (empty for a plugin): what the Agents panel lists it under.</param>
public sealed record InventoryBomExtraDetail(string Key, string Title, IReadOnlyList<InventoryBomField> Fields, string AgentId = "");

/// <summary>What one of the two gateway-fed tabs has to show.</summary>
public enum InventoryBomExtraState
{
    /// <summary>Not read yet.</summary>
    NotLoaded,

    /// <summary>The first read is on its way.</summary>
    Loading,

    /// <summary>Rows are on screen (there may be none).</summary>
    Ready,

    /// <summary>The gateway has no such route (HTTP 404 or 405): an older gateway, or a managed host that does not register it.</summary>
    Unsupported,

    /// <summary>The gateway answered that it keeps nothing (AI discovery off, or a managed host that records no identities).</summary>
    Disabled,

    /// <summary>The IDE inventory is switched off in the gateway's configuration.</summary>
    Off,

    /// <summary>The read failed; nothing can be said about what exists.</summary>
    Unavailable,
}

/// <summary>
/// The AI BOM's two gateway-fed tabs, the IDE plugins and the agent identities the Mac folds into its BOM on a 1.x runtime
/// (<c>RuntimeV1Inventory</c>). Neither comes from <c>aibom scan</c>: both are read-only gateway routes (<c>GET /api/v1/ai-usage/ide-plugins</c>,
/// <c>GET /api/v1/agents/identities</c>), so they need no review and no scan, and show whether or not a BOM was generated.
/// <para>
/// <b>Scope.</b> The shared connector scope narrows the identities (each belongs to a connector) and never the IDE plugins, which belong to no
/// connector (the Mac exempts them in the same place). <b>Honesty.</b> A tab says why it is empty: an older gateway or a managed host that does not
/// serve the route is "not available on this host", a switched-off inventory says so, a failed read is not an empty list.
/// </para>
/// </summary>
public sealed partial class InventoryBomBrowser
{
    /// <summary>The IDE Plugins tab's value.</summary>
    public const string IdeTab = "ide-plugins";

    /// <summary>The Agent identities tab's value.</summary>
    public const string IdentitiesTab = "identities";

    /// <summary>The most identities listed (the gateway's page bound, and the Mac's).</summary>
    public const int MaxIdentities = 1000;

    private ExtraSection<IdePluginRow> _ide = ExtraSection<IdePluginRow>.None;
    private ExtraSection<AgentIdentityRow> _identities = ExtraSection<AgentIdentityRow>.None;

    private sealed record ExtraSection<T>(InventoryBomExtraState State, string Title, string Detail, IReadOnlyList<T> Rows, string Note, long Total, bool Partial)
    {
        public static ExtraSection<T> None { get; } = new(InventoryBomExtraState.NotLoaded, string.Empty, string.Empty, [], string.Empty, 0, false);

        public bool IsReady => State == InventoryBomExtraState.Ready;
    }

    /// <summary>The runtime has the two gateway routes (<c>RuntimeCapability.IdeInventory</c>): the tabs are offered. False hides them, as on 0.8.10.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIdePluginsTab), nameof(ShowIdentitiesTab))]
    private bool _extrasAvailable;

    /// <summary>The Agents panel exists on this runtime, so an identity can be opened there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenAgents))]
    private bool _agentsLinkAvailable;

    /// <summary>Raised when the operator asks to see the selected identity in the Agents panel.</summary>
    public event EventHandler? OpenAgentsRequested;

    public bool ShowIdePluginsTab => ExtrasAvailable;

    public bool ShowIdentitiesTab => ExtrasAvailable;

    public bool IsIdeTab => ActiveTab == IdeTab;

    public bool IsIdentitiesTab => ActiveTab == IdentitiesTab;

    /// <summary>The active tab is one of the two gateway-fed ones.</summary>
    public bool IsExtrasTab => IsIdeTab || IsIdentitiesTab;

    /// <summary>The selected row is an identity and the Agents panel can show it.</summary>
    public bool CanOpenAgents => AgentsLinkAvailable && SelectedRow?.Extra is { AgentId.Length: > 0 };

    /// <summary>The IDE Plugins tab's count; null until plugins have been read (and for a gateway that has none to give).</summary>
    public int? IdePluginsCount => ExtrasAvailable && _ide.IsReady ? _ide.Rows.Count : null;

    /// <summary>The Agent identities tab's count under the connector scope.</summary>
    public int? IdentitiesCount => ExtrasAvailable && _identities.IsReady ? _identities.Rows.Count(InIdentityScope) : null;

    /// <summary>What the IDE Plugins tab is showing.</summary>
    public InventoryBomExtraState IdeState => _ide.State;

    /// <summary>What the Agent identities tab is showing.</summary>
    public InventoryBomExtraState IdentitiesState => _identities.State;

    [RelayCommand]
    private void OpenInAgents()
    {
        if (CanOpenAgents)
        {
            OpenAgentsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows or hides the two tabs. Hiding them while one is open goes back to the Summary.</summary>
    public void SetExtrasAvailable(bool available)
    {
        if (ExtrasAvailable == available)
        {
            return;
        }

        ExtrasAvailable = available;
        if (!available && IsExtrasTab)
        {
            ActiveTab = SummaryTab;
        }
        else
        {
            Rebuild();
        }
    }

    /// <summary>A read is starting: a tab with nothing yet says so instead of "no rows".</summary>
    public void BeginExtrasLoad()
    {
        if (_ide.State == InventoryBomExtraState.NotLoaded)
        {
            _ide = _ide with { State = InventoryBomExtraState.Loading };
        }

        if (_identities.State == InventoryBomExtraState.NotLoaded)
        {
            _identities = _identities with { State = InventoryBomExtraState.Loading };
        }

        if (IsExtrasTab)
        {
            Rebuild();
        }
    }

    /// <summary>Takes the IDE plugin inventory the gateway answered with, or the reason there is none.</summary>
    public void ShowIdePlugins(CorrelationRead<IdePluginPage> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        _ide = read switch
        {
            { IsOk: true, Value: { Enabled: false } off } => Empty(InventoryBomExtraState.Disabled, "AI discovery is off", off.Reason.Length > 0 ? off.Reason : "The gateway collects IDE plugins during AI discovery, which is switched off. Turn it on under AI Discovery."),
            { IsOk: true, Value: { IsOff: true } } => Empty(InventoryBomExtraState.Off, "The IDE plugin inventory is turned off", "ai_discovery.ide_inventory is off in the gateway's configuration, so no IDE is read."),
            { IsOk: true, Value: { } page } => Ready(page),
            _ => Failed(_ide, read, "IDE plugins could not be read", "IDE plugins"),
        };

        AfterExtrasChanged(IdeTab);

        static ExtraSection<IdePluginRow> Empty(InventoryBomExtraState state, string title, string detail) => new(state, title, detail, [], string.Empty, 0, false);

        static ExtraSection<IdePluginRow> Ready(IdePluginPage page)
        {
            var note = page.Skipped > 0 ? $"{page.Skipped:N0} rows the gateway sent could not be read." : string.Empty;
            return new ExtraSection<IdePluginRow>(
                InventoryBomExtraState.Ready,
                string.Empty,
                string.Empty,
                page.Rows.OrderBy(r => r.User, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.IdeLabel, StringComparer.Ordinal).ThenBy(r => r.PluginLabel, StringComparer.OrdinalIgnoreCase).ToArray(),
                note,
                page.Total,
                page.Partial);
        }
    }

    /// <summary>Takes the agent directory the gateway answered with, or the reason there is none.</summary>
    public void ShowIdentities(CorrelationRead<AgentDirectoryPage> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        _identities = read switch
        {
            { IsOk: true, Value: { Enabled: false } } => new ExtraSection<AgentIdentityRow>(
                InventoryBomExtraState.Disabled,
                "Agent identities are not recorded on this host",
                "The gateway reported that it keeps no agent identities here (a managed host does not).",
                [],
                string.Empty,
                0,
                false),
            { IsOk: true, Value: { } page } => new ExtraSection<AgentIdentityRow>(
                InventoryBomExtraState.Ready,
                string.Empty,
                string.Empty,
                page.Rows.Take(MaxIdentities).ToArray(),
                page.PersistError.Length > 0 ? page.PersistError : string.Empty,
                page.Total,
                page.NextCursor.Length > 0 || page.Total > page.Rows.Count),
            _ => Failed(_identities, read, "Agent identities could not be read", "Agent identities"),
        };

        AfterExtrasChanged(IdentitiesTab);
    }

    private static ExtraSection<T> Failed<T, TPage>(ExtraSection<T> previous, CorrelationRead<TPage> read, string title, string what)
        where TPage : class
    {
        if (read.Status == CorrelationReadStatus.Unsupported)
        {
            return new ExtraSection<T>(
                InventoryBomExtraState.Unsupported,
                "Not available on this host",
                $"{what} come from the gateway, and this gateway does not serve them: an older runtime does not, and a managed (Secure Client) host does not offer them. This is a limit of the host, not a problem with this PC.",
                [],
                string.Empty,
                0,
                false);
        }

        // What was on screen stays, marked stale: an empty list would read as "none" when the truth is "the gateway did not answer".
        if (previous.IsReady && read.KeepsLastGood)
        {
            return previous with { Note = $"Showing the last read; the latest failed: {read.Message}" };
        }

        // The shared reader's wording for "not ready" is about the correlation ledger; these two routes have no ledger.
        var message = read.Status == CorrelationReadStatus.NotReady ? "The gateway is running but cannot answer yet." : read.Message;
        return new ExtraSection<T>(InventoryBomExtraState.Unavailable, title, message + " This is not an empty list.", [], string.Empty, 0, false);
    }

    private void AfterExtrasChanged(string tab)
    {
        if (ActiveTab == tab || ActiveTab == SummaryTab)
        {
            Rebuild();
        }
        else
        {
            RaiseExtraCounts();
        }
    }

    private void RaiseExtraCounts()
    {
        OnPropertyChanged(nameof(IdePluginsCount));
        OnPropertyChanged(nameof(IdentitiesCount));
        OnPropertyChanged(nameof(IdeState));
        OnPropertyChanged(nameof(IdentitiesState));
        OnPropertyChanged(nameof(IsExtrasTab));
        OnPropertyChanged(nameof(ShowGenerate));
        OnPropertyChanged(nameof(CanOpenAgents));
    }

    private bool InIdentityScope(AgentIdentityRow row) => _scope is null || string.Equals(row.Connector.Trim(), _scope, StringComparison.OrdinalIgnoreCase);

    /// <summary>The two tabs' lines for the Summary tab, once they have been read.</summary>
    private IEnumerable<(string Label, string Value)> ExtraSummaryRows()
    {
        if (!ExtrasAvailable)
        {
            yield break;
        }

        if (_ide.IsReady)
        {
            var ai = _ide.Rows.Count(r => r.IsAi);
            var off = _ide.Rows.Count(r => r.Enabled == "disabled");
            yield return ("IDE plugins", string.Create(CultureInfo.InvariantCulture, $"{_ide.Rows.Count} ({ai} AI, {off} disabled)"));
        }

        if (_identities.IsReady)
        {
            yield return ("Agent identities", string.Create(CultureInfo.InvariantCulture, $"{_identities.Rows.Count(InIdentityScope)}"));
        }
    }

    /// <summary>Draws the active tab when it is one of the two gateway-fed ones; false for every other tab.</summary>
    private bool RebuildExtras(InventoryBomRowItem? selected)
    {
        if (!IsExtrasTab)
        {
            return false;
        }

        var ide = IsIdeTab;
        _ideByKey.Clear();
        string emptyTitle = string.Empty;
        string emptyDetail = string.Empty;
        string resultCaption = string.Empty;
        var shown = new List<InventoryBomRowItem>();

        if (ide)
        {
            SetColumns(IdeColumns);
            Section(_ide, "No IDE plugins found yet", "The gateway lists the extensions and plugins of the IDEs it finds during AI discovery scans. Nothing was found, or no scan has finished.", ref emptyTitle, ref emptyDetail);
            if (_ide.IsReady)
            {
                foreach (var row in _ide.Rows)
                {
                    _all.Add(IdeRowItem(row));
                }

                shown = FilteredExtras();
                resultCaption = ResultFor(shown.Count, _ide);
            }
        }
        else
        {
            SetColumns(IdentityColumns);
            Section(_identities, "No agent identities seen yet", "An identity appears after an agent's first hook reaches the gateway.", ref emptyTitle, ref emptyDetail);
            if (_identities.IsReady)
            {
                foreach (var row in _identities.Rows.Where(InIdentityScope))
                {
                    _all.Add(IdentityRowItem(row));
                }

                shown = FilteredExtras();
                resultCaption = ResultFor(shown.Count, _identities);
            }
        }

        Rows.ReplaceAll(shown);

        if (emptyTitle.Length == 0 && shown.Count == 0)
        {
            if (_all.Count > 0)
            {
                (emptyTitle, emptyDetail) = SearchText.Trim().Length > 0
                    ? ($"Nothing matches “{SearchText.Trim()}”", "Clear or change the search text.")
                    : ("No items match the current filter", "Choose All to list every row.");
            }
            else if (!ide && _scope is not null && _identities.Rows.Count > 0)
            {
                emptyTitle = $"No agent identities for {_scope}";
                emptyDetail = "Choose All connectors to see every identity the gateway has.";
            }
            else
            {
                (emptyTitle, emptyDetail) = ide
                    ? ("No IDE plugins found yet", "The gateway lists the extensions and plugins of the IDEs it finds during AI discovery scans.")
                    : ("No agent identities seen yet", "An identity appears after an agent's first hook reaches the gateway.");
            }
        }

        EmptyTitle = emptyTitle;
        EmptyDetail = emptyDetail;
        ShowEmpty = emptyTitle.Length > 0;
        ResultCaption = resultCaption;
        Caption = ide ? "IDE plugins · read from the gateway" : "Agent identities · read from the gateway";

        var kept = selected?.Extra is { } behind ? shown.FirstOrDefault(r => r.Extra?.Key == behind.Key) : null;
        if (!ReferenceEquals(SelectedRow, kept))
        {
            SelectedRow = kept;
        }

        RaiseCounts();
        ShowDetail();
        return true;

        static void Section<T>(ExtraSection<T> section, string emptyTitleText, string emptyDetailText, ref string title, ref string detail)
        {
            switch (section.State)
            {
                case InventoryBomExtraState.NotLoaded:
                case InventoryBomExtraState.Loading:
                    title = "Reading from the gateway";
                    detail = "This list is read-only and needs no scan.";
                    break;
                case InventoryBomExtraState.Ready:
                    if (section.Rows.Count == 0)
                    {
                        title = emptyTitleText;
                        detail = emptyDetailText;
                    }

                    break;
                default:
                    title = section.Title;
                    detail = section.Detail;
                    break;
            }
        }
    }

    private List<InventoryBomRowItem> FilteredExtras()
    {
        IEnumerable<InventoryBomRowItem> rows = _all;
        if (IsIdeTab && StatusFilter != "all")
        {
            rows = rows.Where(r => r.Extra is { } e && _ideByKey.TryGetValue(e.Key, out var plugin) && PassesIdeStatus(plugin));
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            rows = rows.Where(r => r.Matches(search));
        }

        return rows.ToList();
    }

    // The status chip keys on the plugin behind the row; rows are rebuilt on every change, so the map is too.
    private readonly Dictionary<string, IdePluginRow> _ideByKey = new(StringComparer.Ordinal);

    private bool PassesIdeStatus(IdePluginRow plugin) => StatusFilter switch
    {
        "enabled" => plugin.Enabled == "enabled",
        "disabled" => plugin.Enabled == "disabled",
        "unknown" => plugin.IsUnknown,
        "ai" => plugin.IsAi,
        _ => true,
    };

    private string ResultFor<T>(int shown, ExtraSection<T> section)
    {
        var text = section.Total > section.Rows.Count
            ? string.Create(CultureInfo.CurrentCulture, $"{shown:N0} shown · {section.Total - section.Rows.Count:N0} more not kept")
            : string.Create(CultureInfo.CurrentCulture, $"{shown:N0} shown");
        if (section.Partial && section.Total <= section.Rows.Count)
        {
            text += " · partial inventory (the scan reached its limits)";
        }

        return section.Note.Length > 0 ? text + " · " + section.Note : text;
    }

    // ------------------------------------------------------------------ columns and rows

    private static readonly IReadOnlyList<InventoryBomColumn> IdeColumns =
    [
        new("Plugin", InventoryBomColumnSize.Wide),
        new("IDE", InventoryBomColumnSize.Medium),
        new("Version", InventoryBomColumnSize.Medium),
        new("Enabled", InventoryBomColumnSize.Narrow),
        new("AI", InventoryBomColumnSize.Narrow),
        new("User", InventoryBomColumnSize.Medium),
    ];

    private static readonly IReadOnlyList<InventoryBomColumn> IdentityColumns =
    [
        new("Agent ID", InventoryBomColumnSize.Wide),
        new("Connector", InventoryBomColumnSize.Medium),
        new("User", InventoryBomColumnSize.Medium),
        new("Sessions", InventoryBomColumnSize.Narrow),
        new("Last seen", InventoryBomColumnSize.Medium),
        new("State", InventoryBomColumnSize.Medium),
    ];

    private InventoryBomRowItem IdeRowItem(IdePluginRow p)
    {
        var key = p.Fingerprint.Length > 0 ? p.Fingerprint : $"{p.InstallId}/{p.PluginId}/{p.User}";
        _ideByKey[key] = p;
        var fields = new List<InventoryBomField>
        {
            new("Plugin ID", p.PluginId),
            new("Name", p.DisplayName),
            new("Publisher", p.Publisher),
            new("Version", p.Version),
            new("IDE", p.IdeLabel),
            new("User", p.User),
            new("Enabled", p.Enabled.Length == 0 ? "unknown" : p.Enabled),
            new("Enabled source", p.EnabledSource),
            new("Scope", p.Scope),
            new("AI extension", p.IsAi ? "yes" : "no"),
            new("AI signature", p.AiSignatureId),
            new("Installed", At(p.InstalledAt)),
            new("Last seen", At(p.LastSeen)),
            new("Fingerprint", p.Fingerprint),
        };
        return new InventoryBomRowItem(
            [p.PluginLabel, p.IdeLabel, p.Version, p.EnabledLabel, p.IsAi ? "yes" : string.Empty, p.User],
            null,
            string.Empty,
            new InventoryBomExtraDetail(key, "IDE PLUGIN: " + (p.DisplayName.Length > 0 ? p.DisplayName : p.PluginId), fields));
    }

    private static InventoryBomRowItem IdentityRowItem(AgentIdentityRow a)
    {
        var state = a.Retired ? "retired account" : "observed";
        var fields = new List<InventoryBomField>
        {
            new("Agent ID", a.AgentId),
            new("Connector", a.Connector),
            new("User", a.UserName),
            new("State", state),
            new("Sessions seen", a.SessionsSeen.ToString(CultureInfo.InvariantCulture)),
            new("First seen", At(a.FirstSeen)),
            new("Last seen", At(a.LastSeen)),
            new("Last session", a.LastSessionId),
        };
        return new InventoryBomRowItem(
            [a.AgentId, a.Connector, a.UserName, a.SessionsSeen.ToString(CultureInfo.InvariantCulture), At(a.LastSeen), state],
            null,
            string.Empty,
            new InventoryBomExtraDetail(a.AgentId, "AGENT IDENTITY: " + a.AgentId, fields, a.AgentId));
    }

    private static string At(DateTimeOffset? time) =>
        time is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;
}
