namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The rule behind the Connector column of the Alerts and Audit tables (CUST-261): it is there only while more than one connector is active, as the TUI's
/// <c>show_connector_column</c> is set from the active connector count and the connector chip (<see cref="ConnectorScopeViewModel.IsVisible"/>) shows. The
/// rule is the shared scope's own - <c>Services.ConnectorScope.CanScope</c>, a roster of two or more - so every screen that tells connectors apart agrees on
/// when there is anyone to tell apart. A panel exposes it as <c>ShowConnectorColumn</c>, follows the scope's <c>Changed</c> while it is on screen (a roster
/// change is not a scope change, which the base class's hook ignores) and lets its view add or remove the column.
/// </summary>
internal static class ConnectorColumn
{
    /// <summary>
    /// Runs <paramref name="raise"/> on the UI thread. The scope raises <c>Changed</c> on the thread that made the change - the monitor's UI thread for a
    /// roster, a click's for a pick - so this is a direct call in practice and a hop when it is not.
    /// </summary>
    internal static void Notify(Action raise)
    {
        ArgumentNullException.ThrowIfNull(raise);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            raise();
        }
        else
        {
            _ = dispatcher.BeginInvoke(raise);
        }
    }
}
