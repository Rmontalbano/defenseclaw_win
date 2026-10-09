using System.Runtime.CompilerServices;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Shell;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>The palette overlay with recents (CUST-264), on the shared UI thread: the "Recent" chip on the rows that lead an empty search.</summary>
[Collection(UiCollection.Name)]
public sealed class PaletteRecentsViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    [Fact]
    public void The_rows_that_lead_an_empty_search_wear_a_Recent_chip_and_it_goes_when_something_is_typed()
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);
        var store = AppSettingsStore.OpenFresh(_temp.File("palette-settings.json"));
        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "cli.doctor", "nav.alerts", "app.shortcuts" } } });
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

        UiThread.Run(() =>
        {
            var catalog = new PanelCatalog(services);
            var actions = new ShellActions(services, catalog, tray, () => null);
            var curated = CuratedCommandCatalog.For(null).Commands.Where(c => c.TuiName is "doctor" or "skill list" or "keys list").ToArray();
            var rows = ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { }, curated: curated);
            var palette = new CommandPaletteViewModel { RecentsStore = store };
            palette.Load(rows);

            var control = new CommandPaletteControl { DataContext = palette };
            using var host = new OffscreenHost(control, 760, 700);
            host.Relayout();

            Assert.Equal(new[] { "cli.doctor", "nav.alerts", "app.shortcuts" }, palette.Results.Take(3).Select(r => r.Command.Id));
            List<TextBlock> Chips() => VisualTree.Descendants<TextBlock>(control).Where(t => t.Text == "Recent" && t.IsVisible).ToList();
            Assert.Equal(3, Chips().Count);
            RenderTo.Png(host, "palette-recents-empty-search");

            palette.Query = "go to";
            host.Relayout();
            Assert.Empty(Chips());
        });
    }
}
