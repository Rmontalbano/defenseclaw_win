using System.ComponentModel;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The "Run gateway as administrator" switch under the Quick Actions buttons (the Mac's Overview has it in the same place). The switch and its
/// words are <see cref="GatewayAdminToggleViewModel"/>, shared with Settings; this part only follows it, so the start/restart button's name
/// ("Start Gateway as Administrator") changes when the switch does.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private GatewayAdminToggleViewModel? _adminGateway;

    /// <summary>The switch, its caption and status, and the Repair ownership / Prepare buttons.</summary>
    public GatewayAdminToggleViewModel AdminGateway => _adminGateway ??= new GatewayAdminToggleViewModel(Services);

    private void AttachAdminGateway()
    {
        AdminGateway.PropertyChanged += OnAdminGatewayChanged;
        AdminGateway.Attach();
    }

    private void DetachAdminGateway()
    {
        if (_adminGateway is null)
        {
            return;
        }

        _adminGateway.PropertyChanged -= OnAdminGatewayChanged;
        _adminGateway.Detach();
    }

    private void OnAdminGatewayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayAdminToggleViewModel.IsOn) or nameof(GatewayAdminToggleViewModel.StatusText))
        {
            ApplyGatewayActions(_snapshot);
        }
    }
}
