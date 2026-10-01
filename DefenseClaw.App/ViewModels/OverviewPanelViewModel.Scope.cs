using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Selecting a row of the Connectors table scopes the whole app to that connector (<see cref="ConnectorScope"/>, the Mac's shared
/// <c>connectorFilter</c>), and the Overview's cards say so in their titles: <c>Scanners · hermes</c>, <c>Enforcement · hermes</c>,
/// <c>Configuration · hermes</c>. The table is the scope's control here (the chip is a later issue), so the two are kept in step both ways: a
/// selection sets the scope, and a scope set or reset anywhere else moves the selection.
/// <para>
/// Only a connector on the roster can be scoped to, and only while there is more than one (<see cref="ConnectorScope.Set"/> refuses
/// otherwise): on a one-connector install a click still highlights the row and shows its detail, and the scope stays All.
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>True while the table's selection is being set from code, so that does not reach the scope.</summary>
    private bool _syncingSelection;

    /// <summary>The name the operator selected, kept across a rebuild of the rows (which replace the row objects).</summary>
    private string? _selectionName;

    /// <summary>The selected row of the Connectors table; its detail shows under the row. Bound two-way to the list.</summary>
    [ObservableProperty]
    private ConnectorRow? _selectedConnector;

    [ObservableProperty]
    private string _scannersTitle = "Scanners";

    [ObservableProperty]
    private string _enforcementTitle = "Enforcement";

    [ObservableProperty]
    private string _configurationTitle = "Configuration";

    /// <summary>"Select a row to scope the Overview to that connector." / "Overview scoped to Hermes."; empty on an install with one connector.</summary>
    [ObservableProperty]
    private string _connectorCaption = string.Empty;

    [ObservableProperty]
    private bool _hasConnectorCaption;

    [ObservableProperty]
    private bool _isScoped;

    /// <summary>"Open Hermes Alerts →", the link the Mac shows beside the caption while scoped.</summary>
    [ObservableProperty]
    private string _scopedAlertsText = string.Empty;

    partial void OnSelectedConnectorChanged(ConnectorRow? value)
    {
        if (_syncingSelection)
        {
            return;
        }

        _selectionName = value?.Name;

        // Refused (and so inert) on a one-connector install; the scope only ever follows a connector on the roster. When it does change,
        // ConnectorScope.Changed comes back through OnScopeChanged, which redraws the cards.
        _ = Services.ConnectorScope.Set(value?.Name);
    }

    /// <summary>Back to every connector: the caption's "Show all connectors".</summary>
    [RelayCommand]
    private void ClearScope()
    {
        _selectionName = null;
        _ = Services.ConnectorScope.Set(null);

        // A highlight with no scope behind it (a one-connector install, or a row the table no longer has) is cleared as well.
        SelectedConnector = null;
    }

    /// <summary>The scoped Alerts link. The Alerts panel shows what the scope allows once it follows the scope.</summary>
    [RelayCommand]
    private void OpenScopedAlerts() => RequestNavigation("alerts", new AlertsFilter());

    /// <summary>The scope changed or the roster did (from any screen): move the selection with it and redraw what depends on it.</summary>
    private void OnScopeChanged(object? sender, EventArgs e)
    {
        var scope = Services.ConnectorScope.Current;
        if (scope is not null)
        {
            _selectionName = scope;
        }
        else if (Services.ConnectorScope.Connectors.Count > 1)
        {
            _selectionName = null;
        }

        // (A one-connector install has no scope to follow: a highlighted row there is only a highlight, and stays.)
        _syncingSelection = true;
        try
        {
            SelectedConnector = _selectionName is null
                ? null
                : ConnectorRows.FirstOrDefault(r => string.Equals(r.Name, _selectionName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncingSelection = false;
        }

        ApplyScope();
    }

    /// <summary>A finding was acknowledged or arrived: the Findings tile and the table's Alerts column follow.</summary>
    private void OnAlertCountsChanged(object? sender, AlertCountsChangedEventArgs e)
    {
        var snapshot = _snapshot;
        BuildConnectors(snapshot, snapshot.Health);
        RenderEnforcementCards();
    }

    /// <summary>Titles, caption, tiles, scanners and configuration for the scope as it is now. No I/O.</summary>
    internal void ApplyScope()
    {
        var scope = Services.ConnectorScope.Current;
        var suffix = scope is null ? string.Empty : " · " + scope.ToLowerInvariant();
        ScannersTitle = "Scanners" + suffix;
        EnforcementTitle = "Enforcement" + suffix;
        ConfigurationTitle = "Configuration" + suffix;
        IsScoped = scope is not null;

        var row = scope is null ? null : ConnectorRows.FirstOrDefault(r => string.Equals(r.Name, scope, StringComparison.OrdinalIgnoreCase));
        var friendly = row is null ? scope : row.Friendly.Length > 0 ? row.Friendly : row.Name;
        ConnectorCaption = Services.ConnectorScope.CanScope
            ? scope is null ? "Select a row to scope the Overview to that connector." : $"Overview scoped to {friendly}."
            : string.Empty;
        HasConnectorCaption = ConnectorCaption.Length > 0;
        ScopedAlertsText = friendly is null ? string.Empty : $"Open {friendly} Alerts →";

        var snapshot = _snapshot;
        BuildScanners(snapshot, snapshot.Health);
        RenderEnforcementCards();
        BuildConfiguration();
    }
}
