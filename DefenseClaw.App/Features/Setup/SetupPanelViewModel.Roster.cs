using System.Windows;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The hub follows a roster the catalog read again under it (CUST-444): a runtime probe that brings or takes away the decision-model card, or a
/// CLI replaced outside the app, changes which targets there are. Cards are only added or removed when the set of targets differs; a roster with
/// the same targets is left to <see cref="SyncCardsWithCatalog"/> and the catalog's <c>DefinitionChanged</c>.
/// </summary>
public sealed partial class SetupPanelViewModel
{
    private void OnRosterReloaded(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => SyncRosterWithCatalog());
    }

    /// <summary>
    /// Redraws the cards from the catalog's finished roster when its targets differ from the cards'. Returns true when it did (the filters are applied again then). Does nothing while a
    /// read is running, after a failed read, or while the hub is still loading its first roster.
    /// </summary>
    internal bool SyncRosterWithCatalog()
    {
        if (IsLoading || _catalog.LoadedRoster is not { } roster)
        {
            return false;
        }

        var wanted = roster.Select(static d => d.Target).ToHashSet(StringComparer.Ordinal);
        var shown = _all.Select(static c => c.Target).Where(static t => !string.Equals(t, SplunkDashboards.Target, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.SetEquals(shown))
        {
            return false;
        }

        FillCards(roster.Select(d => _catalog.Find(d.Target) ?? d).ToList());
        RefreshAreas();
        ApplyFilters();
        return true;
    }
}
