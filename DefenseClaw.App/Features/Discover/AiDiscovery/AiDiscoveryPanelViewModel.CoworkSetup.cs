using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "Set up Cowork protection" on a Claude Desktop entry (runtime 1.0.8, CUST-444): offered while the runtime has Cowork protection and the
/// Cowork store does not report Cowork protected. It opens the same Cowork wizard as the Overview's "Set up Cowork" button.
/// </summary>
public sealed partial class AiDiscoveryPanelViewModel
{
    private bool _coworkSetupOffered;

    /// <summary>What opens the Cowork wizard: null is <see cref="CoworkWizard.ShowAsync"/> over the main window. A test puts its own here.</summary>
    internal Func<Task>? CoworkWizardOpener { get; set; }

    /// <summary>True on a Cowork-protection runtime while the Cowork store has no "protected" answer (not read yet, or not protected).</summary>
    internal bool OffersCoworkSetup =>
        Services.Runtime.Capabilities.Has(RuntimeCapability.CoworkProtection) &&
        CoworkProtectionStore.For(Services).Current is not { Protected: true };

    [RelayCommand]
    private async Task OpenCoworkSetupAsync()
    {
        await (CoworkWizardOpener?.Invoke() ?? CoworkWizard.ShowAsync(Services, Application.Current?.MainWindow)).ConfigureAwait(true);
    }

    private void OnCoworkStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            FollowCoworkState();
        }
        else
        {
            _ = dispatcher.BeginInvoke(FollowCoworkState);
        }
    }

    /// <summary>Redraws the cards only when the offer flips, so a store read that changes nothing for this panel costs nothing.</summary>
    internal void FollowCoworkState()
    {
        if (OffersCoworkSetup != _coworkSetupOffered)
        {
            RebuildLists(_signals);
        }
    }
}
