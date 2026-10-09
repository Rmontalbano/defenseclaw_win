using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Observability card as a real view in the shell stand-in (CUST-272): the Local SQLite line above the table, a Policy column, a line of
/// facts under each destination that wraps rather than runs off the card at the narrowest window, and - when the plan could not be read - the table
/// the gateway alone gives, with no plan columns and a note. A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a
/// folder (and never otherwise). Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewObservabilityLayoutTests
{
    private const string Title = "Observability destinations";

    private const string ObservabilityYaml = """
        observability:
          local:
            retention_days: 30
          destinations:
            - name: example-otlp
              kind: otlp
              endpoint: https://synthuser:synthpass@collector.example.test:4318/v1/logs?api_key=synthkey
            - name: example-splunk
              kind: splunk_hec
              endpoint: https://splunk.example.test:8088/services/collector/event
            - name: example-archive
              kind: http_jsonl
              enabled: false
              endpoint: https://archive.example.test/defenseclaw
            - name: example-metrics
              kind: prometheus
              listen: 127.0.0.1:9464
        """;

    private const string Destinations = """
        [{"name":"local-sqlite","kind":"sqlite","enabled":true,"state":"healthy","reason":"activated","signals":["logs"],"counters":{"accepted":0,"delivered":0,"dropped":0}},
         {"name":"example-otlp","kind":"otlp","enabled":true,"state":"healthy","reason":"activated","signals":["logs","traces","metrics"],
          "queue":{"items":3,"bytes":1024,"max_items":2048,"max_bytes":67108864,"dropped":0},"last_success_at":"2030-01-15T09:17:58Z"},
         {"name":"example-splunk","kind":"splunk_hec","enabled":true,"state":"degraded","reason":"retrying","signals":["logs"],
          "last_failure_class":"timeout","last_failure_at":"2030-01-15T09:30:00Z","last_success_at":"2030-01-15T09:00:00Z"}]
        """;

    private sealed class Scene : IDisposable
    {
        private readonly OverviewScene _data;
        private readonly PanelShell _shell;

        private Scene(OverviewScene data, PanelShell shell, OverviewPanel panel, OverviewPanelViewModel viewModel)
        {
            _data = data;
            _shell = shell;
            Panel = panel;
            ViewModel = viewModel;
        }

        public OverviewPanel Panel { get; }

        /// <summary>The panel's view-model, taken on the UI thread when the panel was shown (reading it later is a cross-thread read of the page's DataContext).</summary>
        public OverviewPanelViewModel ViewModel { get; }

        /// <param name="withPlan">True: the plan is read through a fake command. False: the real reader finds no CLI, and the card is the gateway's with a note.</param>
        public static Scene Open(int width, int height, bool withPlan)
        {
            var data = OverviewScene.Create(twoConnectors: false, seedAudit: false, seedAgents: false);
            File.WriteAllText(data.Services.Paths.ConfigFilePath, OverviewScene.ConfigYaml(false) + ObservabilityYaml);
            data.Services.ReloadConfig();

            PanelShell? shell = null;
            try
            {
                var (panel, viewModel) = UiThread.Run(() =>
                {
                    var snapshot = OverviewScene.Snapshot(twoConnectors: false, health: Health());
                    data.Publish(snapshot);
                    shell = new PanelShell(data.Services, width, height);
                    var view = shell.Show<OverviewPanel>();
                    var vm = (OverviewPanelViewModel)shell.ViewModel;
                    vm.Apply(snapshot);
                    shell.Host.Relayout();
                    return (view, vm);
                });

                var scene = new Scene(data, shell!, panel, viewModel);
                scene.Settle(withPlan, data);
                return scene;
            }
            catch
            {
                UiThread.Run(() => shell?.Dispose());
                data.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Showing the panel started the real reader (no CLI in a test: it ends as a failure). Let that finish first, so it cannot land after the plan
        /// this scene puts in its place and replace it.
        /// </summary>
        private void Settle(bool withPlan, OverviewScene data)
        {
            var vm = ViewModel;
            UiThread.WaitFor(() => vm.ObservabilityNote.StartsWith("Observability plan unavailable", StringComparison.Ordinal), "the real reader to give up");

            if (withPlan)
            {
                var plan = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "cli", "observability-plan.destinations.synthetic.json"));
                UiThread.Run(() =>
                {
                    vm.PlanReader = new ObservabilityPlanReader((_, _, _) => Task.FromResult(new PlanCommandOutput(0, null, plan)), () => data.Services.Config);
                    _ = vm.RefreshObservabilityPlanAsync(force: true, CancellationToken.None);
                });
                UiThread.WaitFor(() => vm.HasObservabilityPlan, "the plan to be applied");
            }

            UiThread.Run(() => _shell.Host.Relayout());
        }

        private static GatewayHealth Health()
        {
            var node = JsonNode.Parse(OverviewScene.HealthJson(twoConnectors: false))!.AsObject();
            var details = node["telemetry"]!["details"]!.AsObject();
            details["destinations"] = JsonNode.Parse(Destinations);
            details["destination_count"] = 3;
            details["retention_state"] = "healthy";
            _ = details.Remove("retention_days");
            details["retention_days"] = 30;
            return JsonSerializer.Deserialize<GatewayHealth>(node.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }

        public FrameworkElement Card
        {
            get
            {
                var header = VisualTree.Descendants<DcCardHeader>(Panel).First(h => Equals(h.Content, Title));
                return (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(System.Windows.Media.VisualTreeHelper.GetParent(header));
            }
        }

        public ItemsControl Rows =>
            VisualTree.Find<ItemsControl>(Panel, list => AutomationProperties.GetName(list) == Title)
            ?? throw new InvalidOperationException("The destinations list is not in the view.");

        /// <summary>Scrolls the page so the card is at the top (it is far down the dashboard), then draws it.</summary>
        public void Render(string name)
        {
            UiThread.Run(() =>
            {
                var scroll = VisualTree.Find<ScrollViewer>(Panel);
                if (scroll is { Content: UIElement content })
                {
                    scroll.ScrollToVerticalOffset(Math.Max(0, Card.TranslatePoint(new Point(0, 0), content).Y - 8));
                }

                _shell.Host.Relayout();
                RenderTo.Png(_shell.Host, name);
            });
        }

        public void Dispose()
        {
            UiThread.Run(() => _shell.Dispose());
            _data.Dispose();
        }
    }

    private static IReadOnlyList<string> VisibleTexts(DependencyObject root) =>
        VisualTree.Descendants<TextBlock>(root).Where(t => t.IsVisible && t.ActualWidth > 0).Select(t => t.Text).ToList();

    [Fact]
    public void With_the_plan_the_card_has_the_local_sqlite_line_the_plan_columns_and_a_line_of_facts_under_each_destination()
    {
        using var scene = Scene.Open(1400, 900, withPlan: true);

        UiThread.Run(() =>
        {
            var card = scene.Card;
            var texts = VisibleTexts(card);

            // The Local SQLite line, its files, and the column the plan adds.
            Assert.Contains("Local SQLite · retention=30 days · controller=healthy · judge capture=enabled", texts);
            Assert.Contains(texts, t => t.StartsWith("Event history: ", StringComparison.Ordinal) && t.Contains("Judge bodies: ", StringComparison.Ordinal));
            Assert.Contains("Policy", texts);

            // One row per destination the plan lists (five), the gateway's entries it does not (none here), and the sink.
            var rows = scene.Rows;
            Assert.Equal(6, rows.Items.Count);
            Assert.Contains("13/14", texts);
            Assert.Contains("0/14", texts);

            // A destination's facts: labelled, with the endpoint as a host and port.
            Assert.Contains("Buckets", texts);
            Assert.Contains("Redaction", texts);
            Assert.Contains("redacted: sensitive", texts);
            Assert.Contains("Queue", texts);
            Assert.Contains("Last result", texts);
            Assert.Contains("Endpoint", texts);
            Assert.Contains("collector.example.test:4318", texts);
            Assert.Contains("archive.example.test", texts);

            // The two states that are not the gateway's word.
            Assert.Contains("disabled", texts);
            Assert.Contains("unavailable", texts);

            // Nothing a credential rode in on is on the screen.
            Assert.DoesNotContain(texts, t => t.Contains("synth", StringComparison.OrdinalIgnoreCase) || t.Contains("api_key", StringComparison.OrdinalIgnoreCase));

            // Top to bottom in the plan's order, each row below the one before it.
            var tops = Enumerable.Range(0, rows.Items.Count)
                .Select(i => ((FrameworkElement)rows.ItemContainerGenerator.ContainerFromIndex(i)).TranslatePoint(new Point(0, 0), rows).Y)
                .ToList();
            Assert.Equal(tops.OrderBy(y => y), tops);
            Assert.Equal(tops.Count, tops.Distinct().Count());
            Assert.Equal("local-sqlite", ((ObservabilityRow)rows.Items[0]).Name);
        });

        scene.Render("cust272-observability-1400x900");
    }

    [Fact]
    public void At_the_narrowest_window_the_facts_wrap_inside_the_card_and_nothing_runs_past_it()
    {
        using var scene = Scene.Open(940, 620, withPlan: true);

        UiThread.Run(() =>
        {
            var card = scene.Card;
            Assert.True(card.ActualWidth > 300, $"the card is {card.ActualWidth} wide");

            // The column headings are the page's shared 11 px heading style (every table on the Overview has them); everything else is 12 or more.
            var headings = new[] { "Name", "Target", "Kind", "Policy", "State", "Signals" };
            foreach (var text in VisualTree.Descendants<TextBlock>(card).Where(t => t.IsVisible && t.ActualWidth > 0))
            {
                var right = text.TranslatePoint(new Point(text.ActualWidth, 0), card).X;
                Assert.True(right <= card.ActualWidth + 0.5, $"'{text.Text}' ends at {right} in a card {card.ActualWidth} wide");
                if (!headings.Contains(text.Text))
                {
                    Assert.True(text.FontSize >= 12, $"'{text.Text}' is {text.FontSize} px");
                }
            }

            // The OTLP destination has the most to say: its facts take more than one line there.
            var rows = scene.Rows;
            var otlp = (FrameworkElement)rows.ItemContainerGenerator.ContainerFromIndex(1);
            var local = (FrameworkElement)rows.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.True(otlp.ActualHeight > local.ActualHeight, $"{otlp.ActualHeight} against {local.ActualHeight}");
        });

        scene.Render("cust272-observability-940x620");
    }

    [Fact]
    public void Without_the_plan_the_card_is_the_table_the_gateway_gives_with_a_note_and_nothing_of_the_plan_is_drawn()
    {
        using var scene = Scene.Open(1400, 900, withPlan: false);

        UiThread.Run(() =>
        {
            var card = scene.Card;
            var texts = VisibleTexts(card);

            Assert.DoesNotContain(texts, t => t.StartsWith("Local SQLite", StringComparison.Ordinal));
            Assert.DoesNotContain("Policy", texts);
            Assert.DoesNotContain("Buckets", texts);
            Assert.DoesNotContain("Redaction", texts);
            Assert.DoesNotContain("Endpoint", texts);

            // The gateway's three destinations and its sink are there as they always were, and the note says why the rest is not.
            Assert.Equal(4, scene.Rows.Items.Count);
            Assert.Contains("Name", texts);
            Assert.Contains("Signals", texts);
            Assert.Contains("Observability plan unavailable: 'defenseclaw' was not found. The card shows what the gateway reports.", texts);
        });

        scene.Render("cust272-observability-fallback-1400x900");
    }
}
