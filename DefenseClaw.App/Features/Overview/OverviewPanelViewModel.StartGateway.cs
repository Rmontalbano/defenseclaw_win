using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The "Start gateway" button on the "The gateway is not answering" attention row. It is the shared path (<see cref="GatewayLifecycle"/>: the same
/// gate, review and run as the tray, the palette, the degraded banner and the flyout), not a second implementation: the panel only decides whether the
/// button is pressable and what its tooltip says. The Quick Actions button of the same name keeps its in-panel review.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private bool _startGatewayAllowed;
    private string _startGatewayTip = string.Empty;

    /// <summary>The row's button tooltip: what the start does, or why it is off (the installation's sentence first).</summary>
    public string StartGatewayTip
    {
        get => _startGatewayTip;
        private set => SetProperty(ref _startGatewayTip, value);
    }

    /// <summary>Opens the shared review of <c>defenseclaw-gateway start</c>; nothing starts until it is confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanStartGatewayFromRow))]
    private Task StartGatewayFromRow() => Services.GatewayLifecycle.RunAsync(GatewayAction.Start);

    private bool CanStartGatewayFromRow() => _startGatewayAllowed;

    /// <summary>Re-reads the offer for the snapshot on screen. Cheap: it runs with every rebuild of the attention list.</summary>
    private void RefreshStartGatewayAttention()
    {
        var offer = GatewayLifecycle.StartOffer(_snapshot, Services.Installation);
        StartGatewayTip = offer.Tip;
        if (offer.Enabled != _startGatewayAllowed)
        {
            _startGatewayAllowed = offer.Enabled;
            StartGatewayFromRowCommand.NotifyCanExecuteChanged();
        }
    }
}
