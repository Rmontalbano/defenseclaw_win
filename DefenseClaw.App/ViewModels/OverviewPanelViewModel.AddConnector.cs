using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Connectors table's "Add" (CUST-210): the connectors the AI-discovery scan has seen that DefenseClaw is not configured for are listed
/// after the configured ones with the Mac's orange "not configured" and an Add button, which opens the shared review of
/// <c>defenseclaw setup &lt;alias&gt; --yes --mode observe</c> (observe never blocks; with other connectors configured <c>--yes</c> adds
/// alongside them). Nothing runs until the review is confirmed.
/// <para>
/// The detected set is read from <c>ai_discovery_state.json</c> together with the Agents card's read (same file, same cadence), so this adds
/// no I/O of its own. <see cref="UnconfiguredConnectors"/> and <see cref="UnconfiguredConnectorsChanged"/> are what the attention rule
/// "Detected but not configured: Codex, Claude Code" needs.
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private IReadOnlyList<string> _detectedConnectors = Array.Empty<string>();

    private IReadOnlyList<OfferedConnector> _unconfigured = Array.Empty<OfferedConnector>();

    /// <summary>True while the table has a "not configured" row: the Action column's header shows then.</summary>
    [ObservableProperty]
    private bool _hasUnconfiguredRows;

    /// <summary>Detected connectors that are offered on this machine and not configured, in the first-run window's order. Set on the UI thread.</summary>
    internal IReadOnlyList<OfferedConnector> UnconfiguredConnectors => _unconfigured;

    /// <summary>Raised (UI thread) when <see cref="UnconfiguredConnectors"/> changed.</summary>
    internal event EventHandler? UnconfiguredConnectorsChanged;

    /// <summary>The Agents card read a scan: remember which connectors it maps to and redraw the table when that changed.</summary>
    private void SetDetectedConnectors(IReadOnlyList<string> detected)
    {
        if (_detectedConnectors.SequenceEqual(detected, StringComparer.Ordinal))
        {
            return;
        }

        _detectedConnectors = detected;
        var snapshot = _snapshot;
        BuildConnectors(snapshot, snapshot.Health);
    }

    /// <summary>
    /// The not-configured rows for the table, after <paramref name="configured"/> (every name the table already has). Updates
    /// <see cref="UnconfiguredConnectors"/> as a side effect so the two never disagree.
    /// </summary>
    private List<ConnectorRow> BuildUnconfiguredRows(IEnumerable<string> configured)
    {
        var unconfigured = ConnectorOnboarding.Unconfigured(_detectedConnectors, configured, OfferableNow());
        var changed = !unconfigured.Select(static c => c.Id).SequenceEqual(_unconfigured.Select(static c => c.Id), StringComparer.Ordinal);
        _unconfigured = unconfigured;
        HasUnconfiguredRows = unconfigured.Count > 0;
        if (changed)
        {
            UnconfiguredConnectorsChanged?.Invoke(this, EventArgs.Empty);
        }

        return unconfigured
            .Select(static c => new ConnectorRow
            {
                Name = c.Id,
                Friendly = c.Label,
                StateText = "not configured",
                StateKey = "High",
                RulePack = "—",
                LastActivityShort = "—",
                IsUnconfigured = true,
                AddCaution = c.Caution,
            })
            .ToList();
    }

    /// <summary>
    /// What the catalog says can be offered here, when the Setup catalog has been read (it is not read for this: no CLI call from the Overview); the
    /// built-in list otherwise.
    /// </summary>
    private IReadOnlyList<OfferedConnector> OfferableNow()
    {
        var catalog = WizardCatalog.Shared(Services);
        var known = ConnectorOnboarding.Offerable(null)
            .Select(o => catalog.Find(o.Alias))
            .Where(static d => d is not null)
            .Cast<WizardDefinition>()
            .ToList();
        return ConnectorOnboarding.Offerable(known);
    }

    /// <summary>The row's Add: the review of the one command. Refused for a row that is configured (it has no button).</summary>
    [RelayCommand]
    private void AddConnector(ConnectorRow? row)
    {
        if (row is not { IsUnconfigured: true })
        {
            return;
        }

        var argv = ConnectorOnboarding.AddArgv(row.Name);
        var label = row.Friendly.Length > 0 ? row.Friendly : row.Name;
        Review.Open(
            $"Add {label}?",
            $"Configures DefenseClaw for {label} in observe mode, alongside any connector already configured. Observe records what the agent does and never blocks it.",
            new[]
            {
                new DiscoverStep(
                    argv,
                    $"Add {label} in observe mode.",
                    CommandTier.StateChanging,
                    CliRunner.LongRunningTimeout),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            restartsGateway: CommandReview.RestartsGatewayFor(argv),
            warning: row.AddCaution.Length > 0 ? row.AddCaution : null,
            primaryText: $"Add {label}");
    }
}

public sealed partial class ConnectorRow
{
    /// <summary>A detected connector DefenseClaw is not configured for: the table's "not configured" row, with an Add button.</summary>
    public bool IsUnconfigured { get; init; }

    /// <summary>Not certified on Windows, or empty: shown in the Add review.</summary>
    public string AddCaution { get; init; } = string.Empty;

    /// <summary>The screen-reader name of the Add button.</summary>
    public string AddAutomationName => $"Add {(Friendly.Length > 0 ? Friendly : Name)}";
}
