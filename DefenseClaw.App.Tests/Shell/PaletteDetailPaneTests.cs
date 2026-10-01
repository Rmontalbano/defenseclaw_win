using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Shell;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>The palette's detail pane for a curated CLI row (CUST-224): shown for those rows only, with the argv and both buttons.</summary>
[Collection(UiCollection.Name)]
public class PaletteDetailPaneTests
{
    private static ShellCommand Row(string id, string title, string category, CuratedCommand? cli) =>
        new(id, title, category, "Description of " + title, null, string.Empty, true, null, () => { }, cli, cli is null ? null : () => { });

    [Fact]
    public void The_detail_pane_shows_for_a_CLI_row_with_its_argv_and_hides_for_an_app_row()
    {
        UiThread.Run(() =>
        {
            var cli = new CuratedCommand(new[] { "agent", "discovery", "scan" }, "Scan", "Trigger one immediate AI discovery scan.", string.Empty, Array.Empty<string>());
            var palette = new CommandPaletteViewModel();
            palette.Load(new[]
            {
                Row("nav.overview", "Go to Overview", "Panel", null),
                Row(cli.Id, cli.Title, cli.Category, cli),
            });

            var control = new CommandPaletteControl { DataContext = palette };
            using var host = new OffscreenHost(control, 760, 640);

            host.Relayout();
            var detail = VisualTree.Find<System.Windows.Controls.Border>(control, b => b.GetValue(System.Windows.Automation.AutomationProperties.NameProperty) as string == "Command detail");
            Assert.NotNull(detail);
            Assert.Equal(Visibility.Collapsed, detail!.Visibility);

            palette.MoveSelection(1);
            host.Relayout();
            Assert.Equal(Visibility.Visible, detail.Visibility);
            Assert.NotNull(VisualTree.Find<System.Windows.Controls.TextBox>(detail, t => t.Text == "defenseclaw agent discovery scan"));
            Assert.Equal(2, VisualTree.Descendants<Wpf.Ui.Controls.Button>(detail).Count());

            RenderTo.Png(host, "palette-cli-detail");
        });
    }
}
