using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Services card as a real view in the shell stand-in (CUST-313): nine rows, the Sandbox card's sentence-long state word wraps instead of
/// being cut off at the narrowest window, and a PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder
/// (and never otherwise). Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewServicesLayoutTests
{
    private sealed class Scene : IDisposable
    {
        private readonly OverviewScene _data;
        private readonly PanelShell _shell;

        private Scene(OverviewScene data, PanelShell shell, OverviewPanel panel)
        {
            _data = data;
            _shell = shell;
            Panel = panel;
        }

        public OverviewPanel Panel { get; }

        public OverviewPanelViewModel ViewModel => (OverviewPanelViewModel)_shell.ViewModel;

        public static Scene Open(int width, int height, string healthJson, bool twoConnectors = false)
        {
            var data = OverviewScene.Create(twoConnectors: twoConnectors, seedAudit: false, seedAgents: false);
            PanelShell? shell = null;
            try
            {
                var panel = UiThread.Run(() =>
                {
                    var snapshot = OverviewScene.Snapshot(
                        twoConnectors: twoConnectors,
                        health: JsonSerializer.Deserialize<GatewayHealth>(healthJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
                    data.Publish(snapshot);
                    shell = new PanelShell(data.Services, width, height);
                    var view = shell.Show<OverviewPanel>();
                    var vm = (OverviewPanelViewModel)shell.ViewModel;
                    vm.Apply(snapshot);
                    shell.Host.Relayout();
                    return view;
                });

                return new Scene(data, shell!, panel);
            }
            catch
            {
                UiThread.Run(() => shell?.Dispose());
                data.Dispose();
                throw;
            }
        }

        public ItemsControl ServicesList =>
            VisualTree.Find<ItemsControl>(Panel, list => AutomationProperties.GetName(list) == "Services")
            ?? throw new InvalidOperationException("The Services list is not in the view.");

        public void Render(string name) => RenderTo.Png(_shell.Host, name);

        public void Dispose()
        {
            UiThread.Run(() => _shell.Dispose());
            _data.Dispose();
        }
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The 0.8.10 payload with a second connector that has failed, so the roll-up has something to say and the card shows more than green.</summary>
    private static string WithFailedSecondConnector()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Read("runtime-0.8.10/rest/health.sinks.synthetic.json"))!.AsObject();
        node["connectors"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
            "{\"name\":\"hermes\",\"state\":\"error\",\"source\":\"auto\",\"requests\":52,\"errors\":9,\"tool_inspection_mode\":\"both\",\"tool_blocks\":0,\"subprocess_blocks\":0}"));
        _ = node.Remove("sinks");
        return node.ToJsonString();
    }

    [Fact]
    public void The_services_list_shows_the_nine_cards_with_the_sandbox_fact_last()
    {
        using var scene = Scene.Open(1400, 900, Read("runtime-0.8.10/rest/health.sinks.synthetic.json"));

        UiThread.Run(() =>
        {
            var list = scene.ServicesList;

            Assert.Equal(9, list.Items.Count);
            Assert.All(Enumerable.Range(0, 9), i => Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(i)));

            var texts = VisualTree.Descendants<TextBlock>(list).Select(t => t.Text).ToList();
            Assert.Contains("Gateway", texts);
            Assert.Contains("AI Discovery", texts);
            Assert.Contains(OverviewPanelViewModel.SandboxStateText, texts);
            Assert.Contains(OverviewPanelViewModel.SandboxDetailText, texts);

            // Top to bottom in the TUI's order: each row starts below the one before it.
            var tops = Enumerable.Range(0, 9)
                .Select(i => ((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(i)).TranslatePoint(new Point(0, 0), list).Y)
                .ToList();
            Assert.Equal(tops.OrderBy(y => y), tops);
            Assert.Equal(tops.Count, tops.Distinct().Count());
            Assert.Equal("sandbox", ((ServiceRow)list.Items[8]).Key);

            scene.Render("overview-services-1400x900");
        });
    }

    [Fact]
    public void A_degraded_agent_and_an_unreported_sinks_card_render_in_their_tones()
    {
        using var scene = Scene.Open(1400, 900, WithFailedSecondConnector(), twoConnectors: true);

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            Assert.Equal("degraded", vm.ServiceRows.Single(r => r.Key == "agent").StateText);
            Assert.Equal("unknown", vm.ServiceRows.Single(r => r.Key == "sinks").StateText);

            scene.Render("overview-services-degraded-1400x900");
        });
    }

    [Fact]
    public void At_the_narrowest_window_the_sandbox_sentence_wraps_inside_the_card_instead_of_being_cut_off()
    {
        using var scene = Scene.Open(940, 620, Read("runtime-0.8.10/rest/health.sinks.synthetic.json"));

        UiThread.Run(() =>
        {
            var list = scene.ServicesList;
            var state = VisualTree.Descendants<TextBlock>(list).Single(t => t.Text == OverviewPanelViewModel.SandboxStateText);

            Assert.Equal(TextWrapping.Wrap, state.TextWrapping);
            var right = state.TranslatePoint(new Point(state.ActualWidth, 0), list).X;
            Assert.True(right <= list.ActualWidth + 0.5, $"the state word ends at {right} in a list {list.ActualWidth} wide");

            scene.Render("overview-services-940x620");
        });
    }
}
