using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview as a real view in the shell stand-in (CUST-209, CUST-205): the Mac's order, an equal-height hero row that is three across on a wide
/// panel and two over one under 1000 DIPs, the four tiles as real buttons, and the connectors table whose rows select the scope. A PNG of each
/// state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewLayoutTests
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

        public OffscreenHost Host => _shell.Host;

        public AppServices Services => _data.Services;

        public static Scene Open(int width, int height)
        {
            var data = OverviewScene.Create(seedAudit: true);
            PanelShell? shell = null;
            try
            {
                var panel = UiThread.Run(() =>
                {
                    data.Publish(OverviewScene.Snapshot());
                    shell = new PanelShell(data.Services, width, height);
                    var view = shell.Show<OverviewPanel>();
                    var vm = (OverviewPanelViewModel)shell.ViewModel;
                    vm.Apply(OverviewScene.Snapshot());
                    vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));
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

        /// <summary>The card (the direct child of the two-row grid, <paramref name="gridColumns"/> wide, that lays cards out) whose header reads <paramref name="header"/>.</summary>
        public FrameworkElement Card(string header, int gridColumns)
        {
            var title = VisualTree.Descendants<DcCardHeader>(Panel).First(h => Equals(h.Content, header));
            DependencyObject? current = title;
            while (current is not null)
            {
                var parent = VisualTreeHelper.GetParent(current);
                if (parent is Grid grid && grid.ColumnDefinitions.Count == gridColumns && grid.RowDefinitions.Count == 2)
                {
                    return (FrameworkElement)current;
                }

                current = parent;
            }

            throw new InvalidOperationException($"'{header}' is not in a {gridColumns}-column grid.");
        }

        /// <summary>The header itself, for a card that is a stack child rather than a grid cell.</summary>
        public DcCardHeader Header(string header) => VisualTree.Descendants<DcCardHeader>(Panel).First(h => Equals(h.Content, header));

        public Point Origin(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), Panel);

        public void Render(string name) => RenderTo.Png(Host, name);

        public void Dispose()
        {
            UiThread.Run(() => _shell.Dispose());
            _data.Dispose();
        }
    }

    // ---- The Mac's order ----

    [Fact]
    public void The_cards_are_in_the_macs_order_top_to_bottom()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            double y(string header) => scene.Origin(scene.Header(header)).Y;

            var attention = y("What needs attention");
            var hero = y("Services");
            var actions = y("Quick Actions");
            var configuration = y("Configuration");
            var connectors = y("Connectors");
            var observability = y("Observability destinations");
            var activity = y("Activity — last 24 h");
            var doctor = y("Doctor");

            Assert.True(attention < hero, "Attention comes before the hero row");
            Assert.True(hero < actions, "the hero row comes before Quick Actions");
            Assert.True(actions < configuration);
            Assert.True(configuration < connectors);
            Assert.True(connectors < observability);
            Assert.True(observability < activity);
            Assert.True(activity < doctor);

            scene.Render("overview-1400x900-top");
        });
    }

    [Fact]
    public void On_a_wide_panel_the_hero_row_is_services_scanners_enforcement_three_across_and_equally_tall()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.False(CompactLayout.GetIsCompact((DependencyObject)scene.Panel.Content));

            var services = scene.Card("Services", 6);
            var scanners = scene.Card("Scanners", 6);
            var enforcement = scene.Card("Enforcement", 6);

            Assert.Equal(scene.Origin(services).Y, scene.Origin(scanners).Y, 0.5);
            Assert.Equal(scene.Origin(services).Y, scene.Origin(enforcement).Y, 0.5);
            Assert.True(scene.Origin(services).X < scene.Origin(scanners).X);
            Assert.True(scene.Origin(scanners).X < scene.Origin(enforcement).X);

            Assert.Equal(services.ActualHeight, scanners.ActualHeight, 0.5);
            Assert.Equal(services.ActualHeight, enforcement.ActualHeight, 0.5);

            // Three equal columns (the gaps are margins).
            Assert.Equal(services.ActualWidth, scanners.ActualWidth, 13.0);
            Assert.Equal(services.ActualWidth, enforcement.ActualWidth, 13.0);
        });
    }

    [Fact]
    public void Under_a_thousand_dips_enforcement_takes_the_row_under_services_and_scanners_which_share_one()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            Assert.True(scene.Panel.ActualWidth < 1000, $"the panel is {scene.Panel.ActualWidth} DIPs wide");

            var services = scene.Card("Services", 6);
            var scanners = scene.Card("Scanners", 6);
            var enforcement = scene.Card("Enforcement", 6);

            Assert.Equal(scene.Origin(services).Y, scene.Origin(scanners).Y, 0.5);
            Assert.Equal(services.ActualHeight, scanners.ActualHeight, 0.5);
            Assert.Equal(services.ActualWidth, scanners.ActualWidth, 13.0);

            Assert.True(
                scene.Origin(enforcement).Y >= scene.Origin(services).Y + services.ActualHeight,
                "Enforcement is under the other two");
            Assert.Equal(scene.Origin(services).X, scene.Origin(enforcement).X, 0.5);
            Assert.True(enforcement.ActualWidth > services.ActualWidth + scanners.ActualWidth, "and as wide as both");

            scene.Render("overview-940x620-top");
        });
    }

    [Fact]
    public void Doctor_and_the_agents_sit_side_by_side_when_wide_and_stack_when_narrow()
    {
        using (var wide = Scene.Open(1400, 900))
        {
            UiThread.Run(() =>
            {
                var doctor = wide.Card("Doctor", 2);
                var agents = wide.Card("Discovered AI agents", 2);

                Assert.Equal(wide.Origin(doctor).Y, wide.Origin(agents).Y, 0.5);
                Assert.True(wide.Origin(doctor).X < wide.Origin(agents).X);
            });
        }

        using var narrow = Scene.Open(940, 620);
        UiThread.Run(() =>
        {
            var doctor = narrow.Card("Doctor", 2);
            var agents = narrow.Card("Discovered AI agents", 2);

            Assert.True(narrow.Origin(agents).Y >= narrow.Origin(doctor).Y + doctor.ActualHeight);
            Assert.Equal(narrow.Origin(doctor).X, narrow.Origin(agents).X, 0.5);
        });
    }

    // ---- The tiles ----

    [Fact]
    public void The_four_tiles_are_real_focusable_buttons_with_a_spoken_name_a_hint_and_a_working_click()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var tiles = VisualTree.Descendants<Button>(scene.Panel).Where(b => b.DataContext is EnforcementTile).ToList();
            Assert.Equal(4, tiles.Count);

            var expected = new (string Panel, Func<object?, bool> Payload)[]
            {
                ("logs", p => p is LogsPreset { Name: "hooks" }),
                ("audit", p => p is AuditPreset { Name: "blocks" }),
                ("alerts", p => p is AlertsFilter { Kind: AlertsFilter.KindAll }),
                ("setup", p => p is null),
            };

            for (var i = 0; i < tiles.Count; i++)
            {
                var button = tiles[i];
                var tile = (EnforcementTile)button.DataContext;

                Assert.True(button.Focusable && button.IsTabStop, $"{tile.Title} is reachable from the keyboard");
                Assert.NotNull(button.FocusVisualStyle);
                Assert.Equal(tile.AutomationName, AutomationProperties.GetName(button));
                Assert.Equal(tile.AutomationHint, AutomationProperties.GetHelpText(button));
                Assert.False(string.IsNullOrWhiteSpace(button.ToolTip as string));

                // Through UI Automation, the way a screen reader or a script presses it.
                var peer = new ButtonAutomationPeer(button);
                Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();

                // UI Automation's Invoke posts the click; let it land.
                UiThread.Settle();

                var pending = scene.Services.Navigation.Pending!;
                Assert.Equal(expected[i].Panel, pending.PanelId);
                Assert.True(expected[i].Payload(pending.Payload), $"{expected[i].Panel} payload was {pending.Payload}");
            }
        });
    }

    [Fact]
    public void The_tiles_lay_out_two_by_two()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var tiles = VisualTree.Descendants<Button>(scene.Panel).Where(b => b.DataContext is EnforcementTile).ToList();
            var points = tiles.Select(scene.Origin).ToList();

            Assert.Equal(points[0].Y, points[1].Y, 0.5);
            Assert.Equal(points[2].Y, points[3].Y, 0.5);
            Assert.True(points[2].Y > points[0].Y);
            Assert.Equal(points[0].X, points[2].X, 0.5);
            Assert.Equal(points[1].X, points[3].X, 0.5);
            Assert.Equal(tiles[0].ActualWidth, tiles[1].ActualWidth, 0.5);
            Assert.Equal(tiles[0].ActualHeight, tiles[2].ActualHeight, 0.5);
        });
    }

    // ---- What needs attention ----

    [Fact]
    public void Attention_rows_carry_the_bracket_tag_beside_the_severity_word()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            vm.Apply(OverviewScene.Snapshot(health: OverviewScene.Health(eventHistoryFailure: "sqlite_write_failed")) with { CriticalAlertCount = 1 });
            scene.Host.Relayout();

            var tags = VisualTree.Descendants<TextBlock>(scene.Panel).Where(t => t.DataContext is AttentionRow row && t.Text == row.Tag).ToList();
            Assert.Equal(vm.VisibleAttention.Count, tags.Count);
            Assert.All(tags, tag => Assert.Equal(((AttentionRow)tag.DataContext).SeverityKey, tag.Tag));

            // The word is still there for a reader who cannot see the colour.
            var badges = VisualTree.Descendants<Border>(scene.Panel).Where(b => b.DataContext is AttentionRow && b.Child is TextBlock).ToList();
            Assert.Equal(vm.VisibleAttention.Count, badges.Count);
        });
    }

    // ---- The connectors table ----

    [Fact]
    public void The_connectors_table_has_a_header_a_row_per_connector_and_selecting_a_row_scopes_the_overview()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var table = VisualTree.Descendants<ListBox>(scene.Panel).Single(l => AutomationProperties.GetName(l) == "Connectors");
            // The two configured connectors, then the scene's detected-but-not-configured ones (CUST-210): see OverviewAddConnectorTests.
            Assert.Equal(2, table.Items.Cast<ConnectorRow>().Count(r => !r.IsUnconfigured));
            Assert.All(table.Items.Cast<ConnectorRow>().Skip(2), r => Assert.True(r.IsUnconfigured));
            Assert.Equal("Select a row to scope the Overview to that connector.", AutomationProperties.GetHelpText(table));

            var headers = VisualTree.Descendants<TextBlock>(scene.Panel).Select(t => t.Text).ToList();
            foreach (var column in new[] { "Connector", "Mode", "Rule Pack", "Last Activity", "Calls", "Blocks", "Alerts", "Status" })
            {
                Assert.Contains(column, headers);
            }

            scene.ViewModel.SetActive(true);
            table.SelectedIndex = 1;
            scene.Host.Relayout();

            Assert.Equal("hermes", scene.Services.ConnectorScope.Current);
            Assert.Equal("Scanners · hermes", scene.ViewModel.ScannersTitle);
            Assert.Contains(
                VisualTree.Descendants<TextBlock>(scene.Panel),
                t => t.Text == "Overview scoped to Hermes.");
            Assert.Contains(VisualTree.Descendants<DcCardHeader>(scene.Panel), h => Equals(h.Content, "Enforcement · hermes"));

            scene.Render("overview-1400x900-scoped");
            scene.ViewModel.SetActive(false);
        });
    }

    // ---- Quick Actions ----

    [Fact]
    public void Quick_actions_are_named_buttons_and_the_diagnostics_menu_lists_the_four_checks_then_the_gateway_and_the_palette()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var labels = VisualTree.Descendants<Wpf.Ui.Controls.Button>(scene.Panel).Select(b => b.Content as string).ToList();
            foreach (var label in new[] { "Scan Skills", "Open Inventory", "Run Doctor" })
            {
                Assert.Contains(label, labels);
            }

            var diagnostics = VisualTree.Descendants<Wpf.Ui.Controls.DropDownButton>(scene.Panel).Single();
            Assert.Equal("Diagnostics", AutomationProperties.GetName(diagnostics));
            var menu = Assert.IsType<ContextMenu>(diagnostics.Flyout);
            var headers = menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
            Assert.Equal(
                new[] { "Validate configuration", "Check credentials", "Gateway status", "Show provenance", "List policies", "Restart Gateway…", "Stop Gateway…", "Open Command Palette" },
                headers);
        });
    }

    [Fact]
    public void Opened_the_diagnostics_menu_is_bound_to_the_panels_commands_and_parameters()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            vm.Apply(OverviewScene.Snapshot(running: true));
            var diagnostics = VisualTree.Descendants<Wpf.Ui.Controls.DropDownButton>(scene.Panel).Single();
            var menu = (ContextMenu)diagnostics.Flyout!;

            // The way the button opens it: aimed at itself, so the items reach the view-model through the placement target.
            menu.PlacementTarget = diagnostics;
            menu.IsOpen = true;
            try
            {
                UiThread.Settle();
                var items = menu.Items.OfType<MenuItem>().ToList();

                var checks = OverviewPanelViewModel.DiagnosticCommands.Count;
                for (var i = 0; i < checks; i++)
                {
                    Assert.Same(vm.RunDiagnosticCommand, items[i].Command);
                    Assert.Same(OverviewPanelViewModel.DiagnosticCommands[i], items[i].CommandParameter);
                    Assert.True(items[i].IsEnabled);
                }

                Assert.Same(vm.RestartGatewayCommand, items[checks].Command);
                Assert.Same(vm.StopGatewayCommand, items[checks + 1].Command);
                Assert.Same(vm.OpenCommandPaletteCommand, items[checks + 2].Command);
                Assert.True(items[checks].IsEnabled && items[checks + 1].IsEnabled && items[checks + 2].IsEnabled);

                // Gateway down: stop has nothing to stop, and the menu says so by being off.
                vm.Apply(OverviewScene.Snapshot(running: false));
                UiThread.Settle();
                Assert.False(items[checks + 1].IsEnabled);
            }
            finally
            {
                menu.IsOpen = false;
            }
        });
    }

    // ---- The hourly chart ----

    [Fact]
    public async Task The_chart_is_one_named_picture_with_its_totals_as_help_text()
    {
        using var scene = Scene.Open(1400, 900);

        // The read runs on the UI thread as it does in the app (its bound collections belong to it) without blocking it.
        await UiThread.Dispatcher.InvokeAsync(() => scene.ViewModel.RefreshHourlyAsync(CancellationToken.None)).Task.Unwrap();

        UiThread.Run(() =>
        {
            scene.Host.Relayout();

            var chart = VisualTree.Descendants<HourlyBarChart>(scene.Panel).Single();
            Assert.True(chart.IsVisible);
            Assert.Equal(24, chart.Buckets!.Count);

            var peer = UIElementAutomationPeer.CreatePeerForElement(chart);
            Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
            Assert.Equal("Hook decisions per hour, last 24 hours", peer.GetName());
            Assert.StartsWith("Hook decisions per hour over 24 hours", peer.GetHelpText(), StringComparison.Ordinal);
            Assert.True(peer.IsContentElement());
        });
    }

    [Fact]
    public void The_chart_draws_allowed_below_blocked_in_the_tone_brushes_and_nothing_when_there_is_nothing()
    {
        UiThread.Run(() =>
        {
            var start = new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
            var buckets = Enumerable.Range(0, 24).Select(i => new HourlyBucket(start.AddHours(i), i == 10 ? 100 : 0, i == 10 ? 50 : 0)).ToList();
            var chart = new HourlyBarChart { Buckets = buckets, Width = 600, Height = 150 };
            chart.Resources["DcToneOkBrush"] = Brushes.Lime;
            chart.Resources["DcToneCriticalBrush"] = Brushes.Red;
            chart.Resources["DcBorderBrush"] = Brushes.Gray;
            chart.Resources["DcTextSecondaryBrush"] = Brushes.Black;

            using var host = new OffscreenHost(chart, 600, 150);
            RenderTo.Png(host, "overview-chart");

            var bitmap = new RenderTargetBitmap(600, 150, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);

            // The busiest bar (hour 10) fills the plot: its lower part is the allowed colour, the part above it the blocked colour.
            var slot = (600 - 34) / 24.0;
            var x = (int)((10 * slot) + (slot / 2));
            var bottom = Pixel(bitmap, x, 150 - 22 - 4);
            var above = Pixel(bitmap, x, 8 + 4);
            Assert.True(bottom.G > 200 && bottom.R < 80 && bottom.B < 80, $"allowed should be lime, was {bottom}");
            Assert.True(above.R > 200 && above.G < 80 && above.B < 80, $"blocked should be red, was {above}");

            // An hour with nothing draws no bar.
            var empty = Pixel(bitmap, (int)((3 * slot) + (slot / 2)), 150 - 22 - 4);
            Assert.Equal(0, empty.A);

            chart.Buckets = Array.Empty<HourlyBucket>();
            chart.UpdateLayout();
            var blank = new RenderTargetBitmap(600, 150, 96, 96, PixelFormats.Pbgra32);
            blank.Render(chart);
            Assert.All(new[] { Pixel(blank, 50, 50), Pixel(blank, 300, 100) }, p => Assert.Equal(0, p.A));
        });
    }

    [Theory]
    [InlineData(0, 4, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(4, 4, 1)]
    [InlineData(5, 6, 2)]
    [InlineData(8, 8, 2)]
    [InlineData(9, 10, 5)]
    [InlineData(11, 15, 5)]
    [InlineData(21, 30, 10)]
    [InlineData(40, 40, 10)]
    [InlineData(100, 100, 50)]
    [InlineData(101, 150, 50)]
    [InlineData(3388, 4000, 1000)]
    public void The_scale_tops_at_a_round_number_that_holds_the_peak_in_at_most_four_steps(int peak, int top, int step)
    {
        var scale = HourlyBarChart.Scale(peak);

        Assert.True(scale.Top >= peak);
        Assert.True(scale.Top / scale.Step <= 4);
        Assert.Equal((top, step), scale);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    [InlineData(24, 0)]
    [InlineData(25, 1)]
    [InlineData(599, 23)]
    [InlineData(600, -1)]
    public void A_pointer_is_over_the_bar_of_the_slot_it_is_in_and_over_none_outside_the_plot(double x, int slot) =>
        Assert.Equal(slot, HourlyBarChart.SlotAt(x, 0, 600, 24));

    [Fact]
    public void A_bars_tooltip_says_the_hour_and_both_counts()
    {
        var bucket = new HourlyBucket(new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), 3388, 4);

        var text = HourlyBarChart.Describe(bucket);

        Assert.Contains("3,388 allowed", text.Replace(' ', ','), StringComparison.Ordinal);
        Assert.EndsWith("4 blocked", text, StringComparison.Ordinal);
    }

    // ---- The source: tokens, not colours ----

    [Fact]
    public void The_view_hard_codes_no_colour_and_no_text_smaller_than_twelve()
    {
        var path = SourcePath();
        var xaml = File.ReadAllText(path);

        Assert.DoesNotMatch(new Regex(@"#[0-9A-Fa-f]{3,8}\b"), xaml);
        Assert.DoesNotMatch(new Regex(@"(Foreground|Background|Fill|Stroke|BorderBrush)=""(?!\{)(?!Transparent"")[^""]+"""), xaml);

        foreach (Match match in Regex.Matches(xaml, @"FontSize=""(\d+(\.\d+)?)"""))
        {
            Assert.True(double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 12, match.Value);
        }
    }

    private static string SourcePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "Views", "Panels", "OverviewPanel.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Views\\Panels\\OverviewPanel.xaml was not found above the test output directory.");
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixels = new byte[4];
        bitmap.CopyPixels(new Int32Rect(Math.Clamp(x, 0, bitmap.PixelWidth - 1), Math.Clamp(y, 0, bitmap.PixelHeight - 1), 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }
}
