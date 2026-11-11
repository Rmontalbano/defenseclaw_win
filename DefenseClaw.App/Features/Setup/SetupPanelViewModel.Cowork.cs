using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Cowork card's state line and the readiness checklist follow the Cowork store (CUST-444): a <c>status --json</c> read the Overview or the
/// Cowork wizard made while the hub is on screen redraws them. Nothing is read here; the store's own reads are the only CLI calls.
/// </summary>
public sealed partial class SetupPanelViewModel
{
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

    /// <summary>Redraws the Cowork card's line (and the area tile that carries it) and the checklist from the store's current state.</summary>
    internal void FollowCoworkState()
    {
        var cowork = CoworkProtectionStore.For(Services).Current;
        foreach (var card in _all.Where(static c => string.Equals(c.Target, CoworkWizard.Target, StringComparison.OrdinalIgnoreCase)))
        {
            _ = card.ApplyState(WizardCardStates.For(card.Target, card.Definition.Group, Services.Config, cowork));
        }

        RefreshAreas();
        Readiness.Rebuild();
    }
}
