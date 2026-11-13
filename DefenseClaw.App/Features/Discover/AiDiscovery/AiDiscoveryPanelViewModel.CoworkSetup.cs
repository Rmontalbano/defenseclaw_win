using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "Set up Cowork protection" on a Claude Desktop entry (runtime 1.0.8, CUST-444): offered while the runtime has Cowork protection and the
/// Cowork store does not report Cowork protected, on a per-user install. It opens the same Cowork wizard as the Overview's "Set up Cowork" button, and reads
/// "Rebuild the Cowork plugin" for a stale plugin (1.0.9), as the Overview row does.
/// </summary>
public sealed partial class AiDiscoveryPanelViewModel
{
    private bool _coworkSetupOffered;

    /// <summary>What opens the Cowork wizard: null is <see cref="CoworkWizard.ShowAsync"/> over the main window. A test puts its own here.</summary>
    internal Func<Task>? CoworkWizardOpener { get; set; }

    private string _coworkSetupLabel = ClaudeDesktopEntry.SetUpCoworkLabel;

    /// <summary>
    /// True on a Cowork-protection runtime while the Cowork store has no "protected" answer (not read yet, or not protected), on a per-user install: on a
    /// managed one the wizard refuses (<c>setup cowork</c> exits there) and the Overview draws no Cowork row, so no button is offered.
    /// </summary>
    internal bool OffersCoworkSetup =>
        Services.Runtime.Capabilities.Has(RuntimeCapability.CoworkProtection) &&
        !Services.Installation.Context.IsManaged &&
        CoworkProtectionStore.For(Services).Current is not { Protected: true };

    /// <summary>The button's text: "Rebuild the Cowork plugin" while <c>status --json</c> reports the plugin stale (1.0.9), else "Set up Cowork protection".</summary>
    internal string CoworkSetupLabel =>
        CoworkProtectionStore.For(Services).Current is { IsPluginStale: true } ? ClaudeDesktopEntry.RebuildCoworkLabel : ClaudeDesktopEntry.SetUpCoworkLabel;

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

    /// <summary>Redraws the cards only when the offer or its text changes, so a store read that changes nothing for this panel costs nothing.</summary>
    internal void FollowCoworkState()
    {
        if (OffersCoworkSetup != _coworkSetupOffered || !string.Equals(CoworkSetupLabel, _coworkSetupLabel, StringComparison.Ordinal))
        {
            RebuildLists(_signals);
        }
    }
}
