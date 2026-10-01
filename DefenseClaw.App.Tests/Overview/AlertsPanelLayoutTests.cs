using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Alerts panel as a real view in the shell stand-in. Like the Audit panel it had a detail pane beside the list that was
/// an empty "Select an alert..." card until something was selected (at the 940 DIP minimum: about two and a half alerts
/// visible), so the pane now exists only while an alert is selected and replaces the list on a narrow panel. And the
/// "Attributes" list in the detail gave each attribute its own key column, so the values started at a different x on every
/// line.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class AlertsPanelLayoutTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static GatewayAlert Alert(string id, string severity, params (string Key, string Value)[] attributes)
    {
        var structured = new Dictionary<string, JsonElement>
        {
            [GatewayAlert.Keys.RuleId] = JsonSerializer.SerializeToElement("CMD-ENV-DUMP"),
            [GatewayAlert.Keys.Title] = JsonSerializer.SerializeToElement("Environment variable dump"),
        };
        foreach (var (key, value) in attributes)
        {
            structured[key] = JsonSerializer.SerializeToElement(value);
        }

        return new GatewayAlert
        {
            Id = id,
            Timestamp = Now.AddMinutes(-2),
            Severity = severity,
            Action = "block",
            Structured = structured,
        };
    }

    [Fact]
    public void At_the_minimum_window_with_nothing_selected_the_list_takes_the_whole_width_and_there_is_no_empty_pane()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            Assert.True(scene.ListCard.ActualWidth >= 600, $"the list card is only {scene.ListCard.ActualWidth} DIPs wide");
            Assert.False(scene.DetailCard.IsVisible);
            Assert.False(scene.ViewModel.HasSelection);

            scene.Render("alerts-940x620-nothing-selected");
        });
    }

    [Fact]
    public void Selecting_an_alert_at_the_minimum_window_shows_the_detail_in_place_of_the_list_and_closing_it_brings_the_list_back()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            Assert.True(scene.Panel.IsCompact);

            scene.ViewModel.SelectedAlert = scene.ViewModel.Alerts[0];
            scene.Host.Relayout();

            Assert.True(scene.DetailCard.IsVisible);
            Assert.True(scene.DetailCard.ActualWidth >= 600, $"the detail is only {scene.DetailCard.ActualWidth} DIPs wide");
            Assert.False(scene.ListCard.IsVisible);

            scene.Render("alerts-940x620-alert-selected");

            scene.ViewModel.ClearSelectionCommand.Execute(null);
            scene.Host.Relayout();
            Assert.True(scene.ListCard.IsVisible);
            Assert.False(scene.DetailCard.IsVisible);
        });
    }

    [Fact]
    public void On_a_wide_window_the_detail_sits_beside_the_list()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.False(scene.Panel.IsCompact);
            Assert.False(scene.DetailCard.IsVisible);

            scene.ViewModel.SelectedAlert = scene.ViewModel.Alerts[0];
            scene.Host.Relayout();

            Assert.True(scene.ListCard.IsVisible && scene.DetailCard.IsVisible);
            Assert.Equal(1, Grid.GetColumn(scene.DetailCard));
            Assert.True(scene.ListCard.ActualWidth >= 600);
            Assert.True(scene.DetailCard.ActualWidth >= 320);

            scene.Render("alerts-1400x900-alert-selected");
        });
    }

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void Every_attribute_value_starts_at_the_same_x_whatever_its_key_length(int width, int height)
    {
        using var scene = Scene.Open(width, height);

        UiThread.Run(() =>
        {
            scene.ViewModel.SelectedAlert = scene.ViewModel.Alerts.First(a => a.Key == "a-1");
            scene.Host.Relayout();

            var attributes =VisualTree.Find<ItemsControl>(scene.DetailCard, c => System.Windows.Automation.AutomationProperties.GetName(c) == "Alert attributes")!;
            Assert.True(Grid.GetIsSharedSizeScope(attributes));

            var rows = VisualTree.Descendants<Grid>(attributes).Where(g => g.ColumnDefinitions.Count == 2).ToList();
            Assert.True(rows.Count >= 4, $"only {rows.Count} attribute rows were built");

            // The keys really are different lengths (a shared column makes their cells the same width, so it is the text that differs).
            var keyLengths = rows.Select(row => VisualTree.Descendants<TextBlock>(row).First().Text.Length).Distinct().Count();
            Assert.True(keyLengths > 1, "the keys are all the same length, so the test proves nothing");

            var starts = rows
                .Select(row => VisualTree.Descendants<TextBlock>(row).Last().TransformToAncestor(attributes).Transform(new Point(0, 0)).X)
                .ToList();
            Assert.All(starts, x => Assert.Equal(starts[0], x, 0.5));

            scene.Render($"alerts-{width}x{height}-attributes");
        });
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(int width, int height)
        {
            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                Panel = shell.Show<AlertsPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (AlertsPanelViewModel)Shell.ViewModel);

            UiThread.Run(() =>
            {
                ViewModel.Apply(new GatewaySnapshot
                {
                    State = AppGatewayState.Running,
                    PolledAt = DateTimeOffset.UtcNow,
                    AlertsFetchedAt = DateTimeOffset.UtcNow,
                    RecentAlerts = new[]
                    {
                        Alert("a-1", "HIGH", ("id", "x"), ("scanner", "codeguard"), ("a_much_longer_attribute_name", "y"), ("mid_key", "z")),
                        Alert("a-2", "CRITICAL", ("k", "1")),
                        Alert("a-3", "MEDIUM"),
                        Alert("a-4", "LOW"),
                        Alert("a-5", "INFO"),
                    },
                });
                Host.Relayout();
            });
        }

        public static Scene Open(int width, int height) => new(width, height);

        public PanelShell Shell { get; }

        public AlertsPanel Panel { get; private set; } = null!;

        public AlertsPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public Border ListCard => (Border)Panel.FindName("ListCard");

        public DefenseClaw.App.Views.Controls.DcInspector DetailCard => (DefenseClaw.App.Views.Controls.DcInspector)Panel.FindName("Inspector");

        public void Render(string name) => RenderTo.Png(Host, name);

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
