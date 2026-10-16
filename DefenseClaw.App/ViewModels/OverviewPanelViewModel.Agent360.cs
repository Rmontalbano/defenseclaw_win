using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Observability;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "Open Agent360" (CUST-354): the card's and the connector rows' link into the per-agent Grafana dashboard of the local observability stack.
/// <para>
/// <b>Offered only when the stack answers.</b> <see cref="CanOpenAgent360"/> follows the app's one look at the local Grafana
/// (<see cref="AppServices.Agent360"/>: a single GET of its health endpoint, made when the panel is shown or refreshed, never on a timer). While Grafana
/// does not answer, the button is not drawn and <see cref="Agent360Hint"/> says why and points to the Local OTel wizard. When it does answer and the
/// compiled routing plan narrows what the local stack receives, <see cref="Agent360Note"/> says the dashboard will be partly empty.
/// </para>
/// <para>
/// The address is built by <see cref="Agent360Link"/> from the address <c>setup local-observability url</c> prints (the default when that cannot be read)
/// and the connector the row or the Overview scope names; opening it is a request to the default browser and changes nothing.
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>Test seam: where an Agent360 address goes (default: the default browser).</summary>
    internal Action<string>? Agent360Opener { get; set; }

    /// <summary>True while the local Grafana answers: the button is drawn.</summary>
    [ObservableProperty]
    private bool _canOpenAgent360;

    /// <summary>Why Agent360 is not offered, pointing to the Local OTel wizard; empty while it is offered or the first look is still running.</summary>
    [ObservableProperty]
    private string _agent360Hint = string.Empty;

    [ObservableProperty]
    private bool _hasAgent360Hint;

    /// <summary>When the compiled plan narrows the local route: one sentence saying Agent360 will be partly empty. Empty otherwise.</summary>
    [ObservableProperty]
    private string _agent360Note = string.Empty;

    [ObservableProperty]
    private bool _hasAgent360Note;

    /// <summary>
    /// Looks at the local Grafana when the answer is due (or <paramref name="force"/>d: Refresh) and redraws the button, hint and note. A look that
    /// cannot run is data (not reachable), never an exception; a cancelled wait (the panel left the screen) changes nothing.
    /// </summary>
    internal async Task RefreshAgent360Async(bool force, CancellationToken cancellationToken)
    {
        try
        {
            await (force ? Services.Agent360.RefreshAsync(cancellationToken) : Services.Agent360.EnsureFreshAsync(cancellationToken)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyAgent360();
    }

    /// <summary>Redraws the button, hint and note from the look and the plan held now. No I/O.</summary>
    internal void ApplyAgent360()
    {
        var availability = Services.Agent360;
        CanOpenAgent360 = availability.Reachable == true;

        // Nothing is said before the first look has landed: "Checking…" under every connector would be noise.
        Agent360Hint = availability.Reachable == false ? availability.Decision.Reason ?? string.Empty : string.Empty;
        HasAgent360Hint = Agent360Hint.Length > 0;

        Agent360Note = CanOpenAgent360 ? Agent360Coverage.NoteFor(_observabilityPlan) : string.Empty;
        HasAgent360Note = Agent360Note.Length > 0;
    }

    /// <summary>The Overview's own button: the dashboard for the connector the Overview is scoped to, or for all of them.</summary>
    [RelayCommand]
    private void OpenAgent360() => LaunchAgent360(Services.ConnectorScope.Current);

    /// <summary>A connector row's button: the dashboard narrowed to that connector.</summary>
    [RelayCommand]
    private void OpenConnectorAgent360(ConnectorRow? row)
    {
        if (row is { IsUnconfigured: false })
        {
            LaunchAgent360(row.Name);
        }
    }

    private void LaunchAgent360(string? connector)
    {
        if (Services.Agent360.UrlFor(connector, agent: null) is not { } url)
        {
            // The stack stopped answering since the button was drawn: say so instead of opening a dead page.
            ApplyAgent360();
            return;
        }

        if (!Agent360Launcher.TryOpen(url, Agent360Opener))
        {
            Agent360Hint = "No browser opened. The dashboard is at " + url;
            HasAgent360Hint = true;
        }
    }
}
