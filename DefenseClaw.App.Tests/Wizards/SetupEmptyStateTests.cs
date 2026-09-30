using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The Setup hub's card grid can be empty for three different reasons, and the panel says which: the setup help could not be
/// read (the reason is in the banner), the CLI answered with no setup targets, or a filter hides every card. A failed read
/// used to show "No wizards match" and "0 of 0 setup target(s) shown" as if a search had emptied the list.
/// </summary>
[Collection(UiCollection.Name)]
public class SetupEmptyStateTests
{
    private const string LoadFailedTitle = "The setup catalog is unavailable";
    private const string NoTargetsTitle = "The CLI reported no setup targets";
    private const string NoMatchTitle = "No wizards match";

    [Theory]
    [InlineData(true, false, 0, 0, SetupEmptyState.None)] // still reading
    [InlineData(true, true, 0, 0, SetupEmptyState.None)]
    [InlineData(false, false, 30, 12, SetupEmptyState.None)] // cards are shown
    [InlineData(false, true, 30, 12, SetupEmptyState.None)]
    [InlineData(false, true, 0, 0, SetupEmptyState.LoadFailed)] // the read failed: there is nothing for a search to hide
    [InlineData(false, false, 0, 0, SetupEmptyState.NoTargets)] // the CLI answered with nothing
    [InlineData(false, false, 30, 0, SetupEmptyState.NoMatch)] // a filter hides all of them
    [InlineData(false, true, 30, 0, SetupEmptyState.NoMatch)]
    public void The_empty_state_follows_the_cause(bool isLoading, bool hasLoadError, int known, int shown, SetupEmptyState expected) =>
        Assert.Equal(expected, SetupPanelViewModel.ClassifyEmpty(isLoading, hasLoadError, known, shown));

    [Fact]
    public void A_failed_catalog_read_says_so_and_does_not_blame_the_search()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 940, 620);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);

            // The isolated services have no CLI, so the catalog read ends in "not found" - a real load failure.
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");
            UiThread.Run(() => shell.Host.Relayout());

            UiThread.Run(() =>
            {
                Assert.True(vm.HasLoadError);
                Assert.Equal(SetupEmptyState.LoadFailed, vm.EmptyState);
                Assert.Equal(new[] { LoadFailedTitle }, EmptyTitles(shell));
                Assert.DoesNotContain("0 of 0", vm.StatusNote, StringComparison.Ordinal);
                Assert.Equal("The setup catalog could not be read.", vm.StatusNote);

                // The way out is right there: a Re-read button in the empty state (the header has one too).
                var reread = VisualTree.Descendants<Wpf.Ui.Controls.Button>(shell.Page!)
                    .Where(b => b.IsVisible && b.Content as string == "Re-read catalog")
                    .ToArray();
                Assert.Equal(2, reread.Length);
                Assert.All(reread, b => Assert.Same(vm.ReloadCatalogCommand, b.Command));

                Render(shell, "setup-empty-load-failed-940x620");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public void Each_cause_shows_only_its_own_words()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 940, 620);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.EmptyState = SetupEmptyState.NoTargets;
                shell.Host.Relayout();
                Assert.Equal(new[] { NoTargetsTitle }, EmptyTitles(shell));
                Render(shell, "setup-empty-no-targets-940x620");

                vm.EmptyState = SetupEmptyState.NoMatch;
                shell.Host.Relayout();
                Assert.Equal(new[] { NoMatchTitle }, EmptyTitles(shell));
                Render(shell, "setup-empty-no-match-940x620");

                vm.EmptyState = SetupEmptyState.None;
                shell.Host.Relayout();
                Assert.Empty(EmptyTitles(shell));
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    /// <summary>Scrolls the page to its end, where the empty state sits under the filters, and renders it.</summary>
    private static void Render(PanelShell shell, string fileName)
    {
        VisualTree.Descendants<ScrollViewer>(shell.Page!).FirstOrDefault(sv => sv.ScrollableHeight > 0)?.ScrollToEnd();
        shell.Host.Relayout();
        RenderTo.Png(shell.Host, fileName);
    }

    /// <summary>The titles of whichever empty-state cards are on screen.</summary>
    private static string[] EmptyTitles(PanelShell shell) =>
        VisualTree.Descendants<TextBlock>(shell.Page!)
            .Where(t => t.IsVisible && t.Text is LoadFailedTitle or NoTargetsTitle or NoMatchTitle)
            .Select(t => t.Text)
            .ToArray();
}
