using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Tables;

/// <summary>
/// WCAG contrast of the table's severity badges, state pills and row states in every style and mode (CUST-214). The badge picks
/// black or white for its label from the tone colour itself (so there is no per-style token to drift), and this holds that choice
/// to 4.5:1 on the colours each style really defines. The pill and the quiet Info badge keep the ordinary text colour on a faint
/// tint, and the selected and hovered row keep both text colours readable.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SeverityBadgeContrastTests
{
    private static readonly string[] SolidTones = { "Critical", "High", "Medium", "Low", "Ok" };
    private static readonly string[] AllTones = { "Critical", "High", "Medium", "Low", "Ok", "Neutral" };

    private readonly ITestOutputHelper _output;

    public SeverityBadgeContrastTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Default", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Dark")]
    [InlineData("Cisco", "Light")]
    public void Badges_pills_and_row_states_read_in_every_style_and_mode(string styleName, string modeName)
    {
        var look = AppearanceContrastTests.Look.Load(styleName, modeName);
        var failures = new List<string>();

        var surface = look.Surface;
        var zebraRow = look.Composite(look.Zebra, surface);

        // The solid capsule: the label the control chooses, on the tone's own fill.
        foreach (var tone in SolidTones)
        {
            var fill = look.Composite(look.Tone(tone), surface);
            var label = DcSeverityBadge.ForegroundFor(fill);
            var ratio = AppearanceContrastTests.Ratio(label, fill);
            _output.WriteLine($"{styleName} {modeName} {tone} badge: {label} on {fill} = {ratio:0.0}");
            Check(failures, $"{tone} badge label", ratio, 4.5);
        }

        // The quiet Info badge and every state pill: the ordinary text on a faint tint, over a plain row and over a zebra row.
        foreach (var (rowName, row) in new[] { ("a plain row", surface), ("a zebra row", zebraRow) })
        {
            foreach (var tone in AllTones)
            {
                var tint = look.Composite(look.ToneSubtle(tone), row);
                Check(failures, $"text on the {tone} tint over {rowName}", AppearanceContrastTests.Ratio(look.Composite(look.TextPrimary, tint), tint), 4.5);
            }

            // The selected and the hovered row: both text colours (a quiet column is secondary).
            foreach (var (stateName, fillColor) in new[] { ("selected", look.Selected), ("hovered", look.Subtle) })
            {
                var background = look.Composite(fillColor, row);
                Check(failures, $"primary text on a {stateName} row over {rowName}", AppearanceContrastTests.Ratio(look.Composite(look.TextPrimary, background), background), 4.5);
                Check(failures, $"secondary text on a {stateName} row over {rowName}", AppearanceContrastTests.Ratio(look.Composite(look.TextSecondary, background), background), 4.5);
            }
        }

        Assert.True(failures.Count == 0, $"{styleName} {modeName}: " + string.Join("; ", failures));
    }

    [Fact]
    public void The_label_is_whichever_of_black_and_white_reads_better_and_never_below_4_58_to_1()
    {
        // Black and white are the two extremes, so the better of them is at least sqrt(21) = 4.58 on any opaque colour.
        var worst = double.MaxValue;
        for (var r = 0; r <= 255; r += 15)
        {
            for (var g = 0; g <= 255; g += 15)
            {
                for (var b = 0; b <= 255; b += 15)
                {
                    var fill = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    var ratio = DcSeverityBadge.Ratio(DcSeverityBadge.ForegroundFor(fill), fill);
                    worst = Math.Min(worst, ratio);
                }
            }
        }

        Assert.True(worst >= 4.5, $"the worst fill gets only {worst:0.00}:1");
        Assert.Equal(Colors.Black, DcSeverityBadge.ForegroundFor(Color.FromRgb(0xFF, 0xD6, 0x0A)));
        Assert.Equal(Colors.White, DcSeverityBadge.ForegroundFor(Color.FromRgb(0xD7, 0x00, 0x15)));
    }

    private static void Check(List<string> failures, string what, double ratio, double minimum)
    {
        if (ratio < minimum)
        {
            failures.Add($"{what} is {ratio:0.00}:1, needs {minimum:0.0}");
        }
    }
}

/// <summary>The three small controls: what they say and how they follow a live change of look.</summary>
[Collection(UiCollection.Name)]
public sealed class BadgeControlTests
{
    [Fact]
    public void The_severity_badge_spells_the_severity_out_and_chooses_its_label_from_the_fill_it_has()
    {
        UiThread.Run(() =>
        {
            const string key = "DcToneHighBrush";

            // The token lives on the badge's own parent here, so replacing it reaches the badge the way a style switch reaches a panel.
            var badge = new DcSeverityBadge { Text = "HIGH", Tone = "High" };
            var parent = new Grid { Children = { badge } };
            parent.Resources[key] = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A));
            using var host = new OffscreenHost(parent, 200, 80);
            host.Relayout();

            // The word is drawn, in the label colour that reads on a light yellow.
            var text = VisualTree.Descendants<TextBlock>(badge).Single();
            Assert.Equal("HIGH", text.Text);
            Assert.Equal(Colors.Black, ((SolidColorBrush)badge.Foreground).Color);

            // A style or mode switch replaces the token's brush: the fill and the label both follow, to white on a dark fill and back.
            parent.Resources[key] = new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x60));
            host.Relayout();
            Assert.Equal(Color.FromRgb(0x10, 0x20, 0x60), ((SolidColorBrush)badge.Fill!).Color);
            Assert.Equal(Colors.White, ((SolidColorBrush)badge.Foreground).Color);

            parent.Resources[key] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x80));
            host.Relayout();
            Assert.Equal(Colors.Black, ((SolidColorBrush)badge.Foreground).Color);

            // Info is the quiet one: the ordinary text colour on the neutral tint.
            badge.Tone = "Info";
            Assert.Same(Application.Current.FindResource("DcTextPrimaryBrush"), badge.Foreground);
            Assert.Same(Application.Current.FindResource("DcToneNeutralSubtleBrush"), badge.Fill);
        });
    }

    [Theory]
    [InlineData("Critical", "DcToneCriticalBrush")]
    [InlineData("Bad", "DcToneCriticalBrush")]
    [InlineData("High", "DcToneHighBrush")]
    [InlineData("Warn", "DcToneHighBrush")]
    [InlineData("Medium", "DcToneMediumBrush")]
    [InlineData("Low", "DcToneLowBrush")]
    [InlineData("Ok", "DcToneOkBrush")]
    [InlineData("Info", null)]
    [InlineData("Neutral", null)]
    [InlineData(null, null)]
    public void The_badge_takes_the_same_tone_keys_as_the_rest_of_the_design_system(string? tone, string? key)
    {
        Assert.Equal(key, DcSeverityBadge.FillKeyFor(tone));
    }

    [Fact]
    public void The_state_pill_is_a_lowercase_word_and_the_status_label_keeps_the_view_models_spelling()
    {
        UiThread.Run(() =>
        {
            var pill = new DcStatePill { Text = "Enabled", Tone = "Ok" };
            var label = new DcStatusLabel { Text = "19 CRITICAL findings", Tone = "Bad" };
            using var host = new OffscreenHost(new StackPanel { Children = { pill, label } }, 300, 100);
            host.Relayout();

            Assert.Equal("enabled", pill.Word);
            Assert.Contains(VisualTree.Descendants<TextBlock>(pill), t => t.Text == "enabled");
            Assert.Contains(VisualTree.Descendants<TextBlock>(label), t => t.Text == "19 CRITICAL findings");
            Assert.NotEmpty(VisualTree.Descendants<DcStatusGlyph>(label));

            pill.Text = "Disabled";
            Assert.Equal("disabled", pill.Word);
        });
    }
}

/// <summary>The Alerts table as a real view: density, sorting, selection and the row menu.</summary>
[Collection(UiCollection.Name)]
public sealed class AlertsTableTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static GatewayAlert Alert(string id, string severity, int minutesAgo, string action = "block", string target = "")
    {
        return new GatewayAlert
        {
            Id = id,
            Timestamp = Now.AddMinutes(-minutesAgo),
            Severity = severity,
            Action = action,
            Target = target,
            RunId = "run-" + id,
            Structured = new Dictionary<string, JsonElement>
            {
                [GatewayAlert.Keys.RuleId] = JsonSerializer.SerializeToElement("RULE-" + id),
                [GatewayAlert.Keys.Title] = JsonSerializer.SerializeToElement("Title of " + id),
            },
        };
    }

    [Fact]
    public void The_table_is_dense_zebra_striped_virtualized_and_follows_the_mac_columns()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var grid = scene.Grid;
            Assert.Equal(
                new[] { "Time", "Severity", "Kind", "Action", "Target", "Details", "Run", "Actions" },
                grid.Columns.Select(c => (string)c.Header).ToArray());

            // Rows are about 26 DIPs (the Mac's are 24-25 pt), the header 30; no gridlines; no row header.
            var rows = VisualTree.Descendants<DataGridRow>(grid).ToList();
            Assert.Equal(5, rows.Count);
            Assert.All(rows, r => Assert.InRange(r.ActualHeight, 24, 28));
            var header = VisualTree.Descendants<DataGridColumnHeader>(grid).First(h => h.Column is not null);
            Assert.InRange(header.ActualHeight, 29, 32);
            Assert.Equal(DataGridGridLinesVisibility.None, grid.GridLinesVisibility);
            Assert.Equal(DataGridHeadersVisibility.Column, grid.HeadersVisibility);

            // Alternate rows wear the zebra token, the others nothing.
            var zebra = Application.Current.FindResource("DcZebraBrush");
            Assert.Same(zebra, rows[1].Background);
            Assert.NotSame(zebra, rows[0].Background);

            // Virtualization is still on, by the row and by the pixel.
            Assert.True(ScrollViewer.GetCanContentScroll(grid));
            Assert.True(VirtualizingPanel.GetIsVirtualizing(grid));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(grid));

            // Text is never below 12. Icon glyphs (a symbol font: the "…" button, the sort arrow) are not text: their size follows the
            // icon control, which depends on which views the shared UI thread built earlier, and failed this check now and then.
            static bool IsGlyph(TextBlock t) =>
                t.FontFamily.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase) ||
                t.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase);
            Assert.All(
                VisualTree.Descendants<TextBlock>(grid).Where(t => t.IsVisible && t.Text.Length > 0 && !IsGlyph(t)),
                t => Assert.True(t.FontSize >= 12, $"'{t.Text}' is {t.FontSize} ({t.FontFamily.Source})"));

            // The severity word is on every badge.
            var badges = VisualTree.Descendants<DcSeverityBadge>(grid).ToList();
            Assert.Equal(new[] { "CRITICAL", "HIGH", "INFO", "LOW", "MEDIUM" }, badges.Select(b => b.Text).Order().ToArray());
        });
    }

    [Fact]
    public void A_row_is_named_for_a_screen_reader_by_its_item()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            // The grid's automation children are one peer per row; each is named by its item's text (AlertItem.ToString).
            var peer = UIElementAutomationPeer.CreatePeerForElement(scene.Grid);
            var names = peer.GetChildren().Select(c => c.GetName()).ToList();
            Assert.Contains(names, n => n.Contains("HIGH alert", StringComparison.Ordinal) && n.Contains("RULE-a-1", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_header_sorts_from_the_keyboard_and_says_which_way_in_words()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var grid = scene.Grid;
            var severity = Header(grid, "Severity");

            // Newest first until something is clicked: the view-model's own order.
            Assert.Equal("a-1", FirstKey(grid));
            Assert.Null(severity.SortDirection);

            // The header is a button that can be focused, so Space and Enter work; the "..." column's is not.
            Assert.True(severity.Focusable && severity.IsTabStop);
            Assert.False(Header(grid, "Actions").Focusable);

            Press(severity, Key.Space);
            scene.Host.Relayout();
            Assert.Equal(ListSortDirection.Ascending, severity.SortDirection);
            Assert.Equal("Sorted ascending", AutomationProperties.GetItemStatus(severity));
            Assert.Equal("INFO", ((AlertItem)grid.Items[0]).Severity);

            Press(severity, Key.Enter);
            scene.Host.Relayout();
            Assert.Equal(ListSortDirection.Descending, severity.SortDirection);
            Assert.Equal("Sorted descending", AutomationProperties.GetItemStatus(severity));
            Assert.Equal("CRITICAL", ((AlertItem)grid.Items[0]).Severity);

            // The sort is the table's, not the view-model's: its own list keeps its order.
            Assert.Equal("a-1", scene.ViewModel.Alerts[0].Key);
        });
    }

    [Fact]
    public void Several_rows_can_be_selected_and_the_first_one_drives_the_detail_pane()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var grid = scene.Grid;
            Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);

            var items = grid.Items.Cast<AlertItem>().ToList();
            grid.SelectedItems.Add(items[1]);
            grid.SelectedItems.Add(items[3]);
            scene.Host.Relayout();

            Assert.Equal(new[] { items[1], items[3] }, scene.ViewModel.SelectedAlerts);
            Assert.Same(items[1], scene.ViewModel.SelectedAlert);
            Assert.True(scene.ViewModel.HasSelection);

            // Copy details: one line per selected row, in table order.
            var text = AlertsPanelViewModel.CopyText(scene.ViewModel.ActionRows);
            var lines = text.Split(Environment.NewLine);
            Assert.Equal(2, lines.Length);
            Assert.Contains($"[{items[1].Severity}]", lines[0], StringComparison.Ordinal);
            Assert.Contains(items[1].Headline, lines[0], StringComparison.Ordinal);
            Assert.Contains($"[{items[3].Severity}]", lines[1], StringComparison.Ordinal);

            // The detail pane's Esc still closes it.
            scene.ViewModel.ClearSelectionCommand.Execute(null);
            Assert.Empty(grid.SelectedItems);
            Assert.Empty(scene.ViewModel.SelectedAlerts);
        });
    }

    [Fact]
    public void Right_clicking_a_row_selects_it_unless_it_is_already_part_of_the_selection()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var grid = scene.Grid;
            var items = grid.Items.Cast<AlertItem>().ToList();
            var rows = VisualTree.Descendants<DataGridRow>(grid).ToList();

            grid.SelectedItems.Add(items[0]);
            grid.SelectedItems.Add(items[1]);

            // A row outside the selection becomes the selection...
            RightClick(rows[3]);
            Assert.Equal(new[] { items[3] }, grid.SelectedItems.Cast<AlertItem>().ToArray());

            // ...one inside it leaves the selection as it is.
            grid.SelectedItems.Add(items[0]);
            RightClick(rows[0]);
            Assert.Equal(2, grid.SelectedItems.Count);
        });
    }

    [Fact]
    public void The_row_menu_has_icons_the_dismiss_item_in_the_danger_tone_and_opens_the_same_review_as_the_toolbar()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            // The CLI is a seam: a dry run that matches one alert, and nothing else ever runs.
            var calls = new List<string[]>();
            scene.ViewModel.RunCli = (argv, _) =>
            {
                calls.Add(argv.ToArray());
                var invocation = InvocationFactory.Create(false, "alerts");
                InvocationFactory.Append(invocation, "Preview: 1 alert(s) matched; digest=sha256:v1:0123456789abcdef");
                InvocationFactory.Append(invocation, "  alert-001 version=0");
                InvocationFactory.Finish(invocation, 0);
                return Task.FromResult(invocation);
            };
            scene.ViewModel.AfterApply = () => Task.CompletedTask;

            var row = VisualTree.Descendants<DataGridRow>(scene.Grid).First();
            var menu = ContextMenuService.GetContextMenu(row);
            Assert.NotNull(menu);
            var items = menu!.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Copy details", "Acknowledge…", "Dismiss…" }, items.Select(i => (string)i.Header).ToArray());
            Assert.All(items, i => Assert.NotNull(i.Icon));

            // Only the one that dismisses is in the critical tone.
            var critical = Application.Current.FindResource("DcToneCriticalBrush");
            Assert.Same(critical, items[2].Foreground);
            Assert.NotSame(critical, items[1].Foreground);

            // Two rows selected, a HIGH and a CRITICAL one: Acknowledge opens on the worse of them - and only previews.
            var all = scene.Grid.Items.Cast<AlertItem>().ToList();
            scene.Grid.SelectedItems.Add(all.First(a => a.Severity == "HIGH"));
            scene.Grid.SelectedItems.Add(all.First(a => a.Severity == "CRITICAL"));
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            Assert.True(scene.ViewModel.IsReviewOpen);
            UiThread.Settle();
            var call = Assert.Single(calls);
            Assert.Equal(new[] { "alerts", "acknowledge", "--severity", "CRITICAL" }, call.Take(4));
            Assert.Contains("--dry-run", call);
            Assert.Equal("Acknowledge alerts", scene.ViewModel.ReviewHeading);

            // Dismiss goes through the very same review (it is the destructive tier there).
            scene.ViewModel.CancelReviewCommand.Execute(null);
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(scene.ViewModel.IsReviewOpen);
            Assert.Equal("Dismiss alerts", scene.ViewModel.ReviewHeading);
            scene.ViewModel.CancelReviewCommand.Execute(null);
        });
    }

    [Fact]
    public void The_row_button_opens_the_rows_own_menu()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var row = VisualTree.Descendants<DataGridRow>(scene.Grid).First();
            var button = VisualTree.Descendants<DcRowMenuButton>(row).Single();
            Assert.Equal("Row actions", AutomationProperties.GetName(button));
            Assert.False(button.IsTabStop);

            // Opened by the button, the menu is the row's own and sits under the button; the row is selected first.
            var opened = button.OpenMenu();
            try
            {
                Assert.True(opened);
                var menu = ContextMenuService.GetContextMenu(row)!;
                Assert.True(menu.IsOpen);
                Assert.Same(button, menu.PlacementTarget);
                Assert.True(row.IsSelected);
            }
            finally
            {
                ContextMenuService.GetContextMenu(row)!.IsOpen = false;
            }
        });
    }

    [Theory]
    [InlineData("Default", "Dark", "default-dark", 940, 620)]
    [InlineData("Default", "Dark", "default-dark", 1400, 900)]
    [InlineData("Cisco", "Light", "cisco-light", 940, 620)]
    [InlineData("Cisco", "Light", "cisco-light", 1400, 900)]
    public void The_kind_column_holds_its_word_and_does_not_take_more_room_than_the_target(string styleName, string modeName, string look, int width, int height)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(styleName), Enum.Parse<AppearanceMode>(modeName)));
        using var scene = Scene.Open(width, height);

        UiThread.Run(() =>
        {
            var kind = scene.Grid.Columns.Single(c => (string)c.Header == "Kind");
            var target = scene.Grid.Columns.Single(c => (string)c.Header == "Target");
            // The column holds one short word (audit, scan, egress; the rule id moved to the inspector's title), so it needs little.
            Assert.True(kind.ActualWidth >= 64, $"Kind is {kind.ActualWidth:0} DIPs");
            Assert.True(kind.ActualWidth <= target.ActualWidth + 40, $"Kind {kind.ActualWidth:0} DIPs, Target {target.ActualWidth:0}");
            RenderTo.Png(scene.Host, $"alerts-table-{look}-{width}x{height}");
        });
    }

    private static DataGridColumnHeader Header(DataGrid grid, string name) =>
        VisualTree.Descendants<DataGridColumnHeader>(grid).First(h => h.Column is not null && (string)h.Column.Header == name);

    private static string FirstKey(DataGrid grid) => ((AlertItem)grid.Items[0]).Key;

    /// <summary>A key press on the focused header: what Space or Enter does for a keyboard or a screen-reader user.</summary>
    private static void Press(DataGridColumnHeader header, Key key)
    {
        var source = PresentationSource.FromVisual(header);
        header.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent, Source = header });
        header.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyUpEvent, Source = header });
    }

    private static void RightClick(DataGridRow row)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
            Source = row,
        };
        row.RaiseEvent(args);
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
                        Alert("a-1", "HIGH", 1, "block", "src/app.py"),
                        Alert("a-2", "CRITICAL", 2),
                        Alert("a-3", "MEDIUM", 3),
                        Alert("a-4", "LOW", 4),
                        Alert("a-5", "INFO", 5),
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

        public DataGrid Grid => (DataGrid)Panel.FindName("AlertList");

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}

/// <summary>The Audit table's menu actions and the Inventory table's sort, which work on the view-models.</summary>
public sealed class TableViewModelTests
{
    private static AuditRow Row(string id, string severity, string action, string target, string details, string runId = "")
    {
        return new AuditRow(new AuditEvent
        {
            Id = id,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
            RawTimestamp = "2026-09-30T10:00:00Z",
            Action = action,
            Target = target,
            Details = details,
            Severity = severity,
            RunId = runId,
            StructuredJsonRaw = "{\"decision\":\"allow\"}",
        });
    }

    [Fact]
    public void An_audit_row_carries_what_the_table_sorts_and_copies()
    {
        var critical = Row("e1", "CRITICAL", "scan-finding", "skill/pdf", "finding.observed", "run-1");
        var info = Row("e2", "INFO", "hook_decision", "", "synthetic");
        var none = Row("e3", "", "hook_decision", "", "synthetic");

        Assert.True(critical.SeverityRank > info.SeverityRank);
        Assert.True(info.SeverityRank > none.SeverityRank);

        var line = critical.CopyLine;
        Assert.Contains("scan-finding", line, StringComparison.Ordinal);
        Assert.Contains("skill/pdf", line, StringComparison.Ordinal);
        Assert.Contains("[CRITICAL]", line, StringComparison.Ordinal);
        Assert.Contains("finding.observed", line, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", info.CopyLine, StringComparison.Ordinal);

        Assert.Equal(critical.CopyLine + Environment.NewLine + info.CopyLine, AuditPanelViewModel.CopyDetailsText(new[] { critical, info }));
        var json = AuditPanelViewModel.CopyStructuredJsonText(new[] { critical });
        Assert.Contains("\"decision\": \"allow\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alert_sorts_by_severity_rank_and_copies_as_one_line()
    {
        var high = AlertItem.FromGateway(new GatewayAlert { Id = "x", Timestamp = DateTimeOffset.UtcNow, Severity = "HIGH", Action = "block", Target = "t", RunId = "r1" });
        var warn = AlertItem.FromGateway(new GatewayAlert { Id = "y", Timestamp = DateTimeOffset.UtcNow, Severity = "WARN", Action = "block" });

        Assert.Equal(3, high.SeverityRank);
        Assert.Equal(2, warn.SeverityRank);
        Assert.Equal("r1", high.RunId);
        Assert.Contains("[HIGH] block t", high.CopyLine, StringComparison.Ordinal);
    }
}
