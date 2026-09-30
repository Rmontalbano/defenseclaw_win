using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>How the one "unacknowledged findings" number is worded on the badge, for a screen reader and in the tray tooltip.</summary>
public sealed class AlertCountPresentationTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static AlertCounts Counts(int total, bool hasMore = false) =>
        new(
            Enumerable.Range(0, total).Select(i => new AlertQueueItem("id-" + i, AuditSeverity.High, "scan-finding", null, "claudecode", At.AddSeconds(-i))).ToArray(),
            hasMore);

    [Theory]
    [InlineData(0, false, "")]
    [InlineData(1, false, "1")]
    [InlineData(441, false, "441")]
    [InlineData(500, true, "500+")]
    public void The_badge_is_the_total_with_a_plus_when_the_window_was_full_and_nothing_for_zero(int total, bool hasMore, string expected)
    {
        Assert.Equal(expected, AlertCountPresentation.Badge(Counts(total, hasMore)));
    }

    [Theory]
    [InlineData(0, false, "no unacknowledged findings")]
    [InlineData(1, false, "1 unacknowledged finding")]
    [InlineData(2, false, "2 unacknowledged findings")]
    [InlineData(441, false, "441 unacknowledged findings")]
    [InlineData(500, true, "500+ unacknowledged findings")]
    public void The_sentence_counts_in_words(int total, bool hasMore, string expected)
    {
        Assert.Equal(expected, AlertCountPresentation.Sentence(Counts(total, hasMore)));
    }

    [Fact]
    public void The_alerts_entrys_name_carries_the_count_and_is_plain_when_nothing_waits()
    {
        Assert.Equal("Alerts, 441 unacknowledged findings", AlertCountPresentation.NavigationName("Alerts", Counts(441)));
        Assert.Equal("Alerts, 500+ unacknowledged findings", AlertCountPresentation.NavigationName("Alerts", Counts(500, hasMore: true)));
        Assert.Equal("Alerts", AlertCountPresentation.NavigationName("Alerts", Counts(0)));
    }

    [Theory]
    [InlineData(AppGatewayState.Running, null)]
    [InlineData(AppGatewayState.Unknown, null)]
    [InlineData(AppGatewayState.NotInitialized, null)]
    [InlineData(AppGatewayState.WslGatewayDetected, null)]
    [InlineData(AppGatewayState.GatewayStopped, "gateway stopped")]
    [InlineData(AppGatewayState.Degraded, "gateway degraded")]
    [InlineData(AppGatewayState.NotInstalled, "DefenseClaw not installed")]
    public void The_overview_entry_is_cautioned_only_while_the_gateway_is_degraded(AppGatewayState state, string? expected)
    {
        Assert.Equal(expected, AlertCountPresentation.DegradedReason(new GatewaySnapshot { State = state }));
    }

    [Fact]
    public void The_tray_tooltip_leads_with_the_count_then_the_state_and_falls_back_to_the_state_alone()
    {
        var running = new GatewaySnapshot { State = AppGatewayState.Running };

        Assert.Equal("DefenseClaw — 441 unacknowledged findings\nRunning", AlertCountPresentation.TrayTooltip(running, Counts(441)));
        Assert.Equal("DefenseClaw — 500+ unacknowledged findings\nRunning", AlertCountPresentation.TrayTooltip(running, Counts(500, hasMore: true)));
        Assert.Equal("DefenseClaw — no unacknowledged findings\nRunning", AlertCountPresentation.TrayTooltip(running, Counts(0)));
        Assert.Equal("DefenseClaw — Running", AlertCountPresentation.TrayTooltip(running, counts: null));
        Assert.Equal("DefenseClaw — 3 unacknowledged findings\nGateway stopped", AlertCountPresentation.TrayTooltip(new GatewaySnapshot { State = AppGatewayState.GatewayStopped }, Counts(3)));
    }

    [Fact]
    public void The_tray_tooltip_is_well_inside_the_127_characters_the_shell_keeps()
    {
        var longest = AlertCountPresentation.TrayTooltip(new GatewaySnapshot { State = AppGatewayState.WslGatewayDetected }, Counts(500, hasMore: true));

        Assert.True(longest.Length < 80, longest);
    }
}

/// <summary>
/// The shell view-model's badge state: fed from the counts service and the gateway snapshot, exactly one subscription to give back,
/// and right for a window built after the first read (which never hears the first <c>Changed</c>).
/// </summary>
public sealed class SidebarBadgeViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-2);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly AlertQueueDatabase _database;

    public SidebarBadgeViewModelTests()
    {
        _services = TestServices.Create(_temp);
        _database = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private void Finding(string id, int minute, string severity = "HIGH") => _database.AddFinding(id, Base.AddMinutes(minute), severity);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task The_badge_follows_the_unacknowledged_count_down_to_nothing()
    {
        Finding("a", 1);
        Finding("b", 2, "CRITICAL");
        Finding("c", 3, "LOW");
        using var vm = new MainWindowViewModel(_services);

        await WaitUntilAsync(() => vm.AlertBadgeText == "3", "the first read");
        Assert.True(vm.HasAlertBadge);
        Assert.Equal("3 unacknowledged findings", vm.AlertBadgeDescription);

        _database.Acknowledge("a");
        await _services.AlertCounts.RefreshAsync();
        await WaitUntilAsync(() => vm.AlertBadgeText == "2", "the badge to drop after an acknowledge");

        _database.Acknowledge("b");
        _database.Acknowledge("c");
        await _services.AlertCounts.RefreshAsync();
        await WaitUntilAsync(() => vm.AlertBadgeText.Length == 0, "the badge to clear");
        Assert.False(vm.HasAlertBadge);
        Assert.Equal(string.Empty, vm.AlertBadgeDescription);
    }

    [Fact]
    public async Task A_full_window_reads_500_plus()
    {
        // One connection and one transaction: the database helper opens a connection per row, which is seconds for 501.
        using (var connection = new SqliteConnection($"Data Source={_services.Paths.AuditDatabasePath};Pooling=False"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            for (var i = 0; i < AlertQueueReader.DefaultWindowLimit + 1; i++)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO audit_events (id, timestamp, action, severity, bucket, connector, event_name)
                    VALUES ($id, $timestamp, 'scan-finding', 'HIGH', 'security.finding', 'claudecode', 'finding.observed')
                    """;
                command.Parameters.AddWithValue("$id", "f" + i.ToString("D4"));
                command.Parameters.AddWithValue("$timestamp", AlertQueueDatabase.Format(Base.AddSeconds(i)));
                _ = command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        using var vm = new MainWindowViewModel(_services);

        await WaitUntilAsync(() => vm.AlertBadgeText == "500+", "the first read");
        Assert.Equal("500+ unacknowledged findings", vm.AlertBadgeDescription);
    }

    [Fact]
    public void Nothing_read_yet_shows_no_badge_not_a_zero_and_no_database_shows_none_either()
    {
        using var vm = new MainWindowViewModel(_services);

        Assert.Equal(string.Empty, vm.AlertBadgeText);
        Assert.False(vm.HasAlertBadge);
        Assert.False(vm.HasOverviewBadge);
    }

    [Fact]
    public async Task A_window_built_after_the_first_read_starts_with_the_count_it_missed()
    {
        Finding("a", 1);
        Finding("b", 2);
        var first = new TaskCompletionSource();
        void OnChanged(object? sender, AlertCountsChangedEventArgs e) => first.TrySetResult();
        _services.AlertCounts.Changed += OnChanged;
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(30));

            using var late = new MainWindowViewModel(_services);

            Assert.Equal("2", late.AlertBadgeText);
        }
        finally
        {
            _services.AlertCounts.Changed -= OnChanged;
        }
    }

    [Fact]
    public async Task Disposing_the_view_model_gives_its_subscription_back_so_a_rebuilt_window_leaves_nothing_behind()
    {
        Finding("a", 1);
        Assert.False(_services.AlertCounts.IsRunning);

        var vm = new MainWindowViewModel(_services);
        Assert.True(_services.AlertCounts.IsRunning);
        await WaitUntilAsync(() => vm.AlertBadgeText == "1", "the first read");

        vm.Dispose();

        // The counts service is idle again: nothing is listening, so nothing is read on the monitor's tick.
        Assert.False(_services.AlertCounts.IsRunning);

        // Several windows in a row (the dashboard can be abandoned and rebuilt) leave no trace either.
        for (var i = 0; i < 5; i++)
        {
            new MainWindowViewModel(_services).Dispose();
        }

        Assert.False(_services.AlertCounts.IsRunning);
    }

    [Fact]
    public void The_overview_badge_follows_the_gateway_state()
    {
        using var vm = new MainWindowViewModel(_services);

        vm.Apply(new GatewaySnapshot { State = AppGatewayState.GatewayStopped, PolledAt = DateTimeOffset.UtcNow });
        Assert.True(vm.HasOverviewBadge);
        Assert.Equal("gateway stopped", vm.OverviewBadgeDescription);

        vm.Apply(new GatewaySnapshot { State = AppGatewayState.Degraded, PolledAt = DateTimeOffset.UtcNow });
        Assert.Equal("gateway degraded", vm.OverviewBadgeDescription);

        vm.Apply(new GatewaySnapshot { State = AppGatewayState.Running, PolledAt = DateTimeOffset.UtcNow });
        Assert.False(vm.HasOverviewBadge);
        Assert.Equal(string.Empty, vm.OverviewBadgeDescription);
    }
}

/// <summary>
/// The badge on a real sidebar entry in a real <see cref="NavigationView"/> (hosted offscreen, like the window's sidebar): what it
/// shows, what a screen reader is told, and that its colour follows the look without being rebuilt. A PNG of each state is written
/// when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SidebarBadgeViewTests
{
    private sealed class Sidebar : IDisposable
    {
        public Sidebar()
        {
            Navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 220, IsPaneOpen = true };
            Overview = Item("Overview", SymbolRegular.AppsListDetail24);
            Alerts = Item("Alerts", SymbolRegular.AlertUrgent24);
            var audit = Item("Audit", SymbolRegular.DocumentSearch24);
            _ = Navigation.MenuItems.Add(Overview);
            _ = Navigation.MenuItems.Add(Alerts);
            _ = Navigation.MenuItems.Add(audit);

            OverviewBadge = new SidebarBadge(Overview, "Overview", "Overview (Ctrl+1)", InfoBadgeSeverity.Caution);
            AlertsBadge = new SidebarBadge(Alerts, "Alerts", "Alerts (Ctrl+2)", InfoBadgeSeverity.Critical);

            Host = new WindowHost(Navigation, 300, 260);
        }

        public NavigationView Navigation { get; }

        public DcNavigationItem Overview { get; }

        public DcNavigationItem Alerts { get; }

        public SidebarBadge OverviewBadge { get; }

        public SidebarBadge AlertsBadge { get; }

        public WindowHost Host { get; }

        private static DcNavigationItem Item(string title, SymbolRegular icon) => new()
        {
            Content = title,
            Icon = new SymbolIcon { Symbol = icon },
            Height = 34,
            ToolTip = title + " (Ctrl+n)",
        };

        public void Dispose() => Host.Dispose();
    }

    /// <summary>
    /// The sidebar in a real window, shown far off screen without activation: unlike an <see cref="OffscreenHost"/> (whose root is not
    /// a window), a window is told when an application resource changes, which is what a live style switch is. Closed on dispose.
    /// </summary>
    private sealed class WindowHost : IDisposable
    {
        private readonly Window _window;

        public WindowHost(FrameworkElement content, double width, double height)
        {
            _window = new Window
            {
                Left = -32000,
                Top = -32000,
                Width = width,
                Height = height,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Content = content,
            };
            _window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
            _window.Show();
            Relayout();
        }

        public void Relayout()
        {
            _window.UpdateLayout();
            UiThread.Settle();
            _window.UpdateLayout();
        }

        /// <summary>Writes a PNG of the window's content when <c>DC_RENDER_DIR</c> names a folder, and does nothing otherwise.</summary>
        public void Png(string name)
        {
            var directory = Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var element = (FrameworkElement)_window.Content;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            _ = Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            encoder.Save(stream);
        }

        public void Dispose()
        {
            _window.Content = null;
            _window.Close();
        }
    }

    [Fact]
    public void A_count_shows_as_a_red_capsule_on_the_alerts_entry_and_the_entry_names_it()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();
            Assert.Equal(Visibility.Collapsed, sidebar.AlertsBadge.Badge.Visibility);

            sidebar.AlertsBadge.Set("441", "441 unacknowledged findings");
            sidebar.Host.Relayout();

            var badge = sidebar.AlertsBadge.Badge;
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("441", badge.Value);
            Assert.Equal(InfoBadgeSeverity.Critical, badge.Severity);
            Assert.Same(badge, sidebar.Alerts.InfoBadge);
            Assert.True(badge.IsVisible, "the capsule is not on screen");
            Assert.True(badge.ActualWidth > 0 && badge.ActualHeight > 0);

            Assert.Equal("Alerts, 441 unacknowledged findings", AutomationProperties.GetName(sidebar.Alerts));
            Assert.Equal("Alerts (Ctrl+2) — 441 unacknowledged findings", sidebar.Alerts.ToolTip);
            Assert.Equal("Alerts, 441 unacknowledged findings", UIElementAutomationPeer.CreatePeerForElement(sidebar.Alerts).GetName());

            sidebar.Host.Png("sidebar-badge-count");
        });
    }

    [Fact]
    public void A_count_that_reaches_the_window_limit_reads_500_plus_and_clearing_it_takes_the_badge_and_the_name_away()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();

            sidebar.AlertsBadge.Set("500+", "500+ unacknowledged findings");
            sidebar.Host.Relayout();
            Assert.Equal("500+", sidebar.AlertsBadge.Badge.Value);
            Assert.Equal("Alerts, 500+ unacknowledged findings", AutomationProperties.GetName(sidebar.Alerts));

            sidebar.AlertsBadge.Set(string.Empty, string.Empty);
            sidebar.Host.Relayout();
            Assert.Equal(Visibility.Collapsed, sidebar.AlertsBadge.Badge.Visibility);
            Assert.Equal("Alerts", AutomationProperties.GetName(sidebar.Alerts));
            Assert.Equal("Alerts (Ctrl+2)", sidebar.Alerts.ToolTip);
        });
    }

    [Fact]
    public void The_overview_entry_carries_a_caution_mark_while_the_gateway_is_degraded()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();

            sidebar.OverviewBadge.Set("!", "gateway stopped");
            sidebar.AlertsBadge.Set("441", "441 unacknowledged findings");
            sidebar.Host.Relayout();

            var badge = sidebar.OverviewBadge.Badge;
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal(InfoBadgeSeverity.Caution, badge.Severity);
            Assert.Equal("!", badge.Value);
            Assert.True(badge.FontSize >= 12, $"the capsule's text is {badge.FontSize} px");
            Assert.True(sidebar.AlertsBadge.Badge.FontSize >= 12);
            Assert.Equal("Overview, gateway stopped", AutomationProperties.GetName(sidebar.Overview));
            Assert.True(badge.IsVisible);

            sidebar.Host.Png("sidebar-badge-count-and-caution");

            sidebar.OverviewBadge.Set("!", string.Empty);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.Equal("Overview", AutomationProperties.GetName(sidebar.Overview));
        });
    }

    [Fact]
    public void The_capsule_is_decorative_to_a_screen_reader_the_entry_says_it_all()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();
            sidebar.AlertsBadge.Set("441", "441 unacknowledged findings");
            sidebar.Host.Relayout();

            var peer = UIElementAutomationPeer.CreatePeerForElement(sidebar.AlertsBadge.Badge);

            Assert.False(peer.IsControlElement());
            Assert.False(peer.IsContentElement());
            Assert.Empty(peer.GetChildren() ?? new List<AutomationPeer>());
        });
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Linear")]
    [InlineData("Tui")]
    [InlineData("Cisco")]
    public void The_capsule_is_red_under_every_style_and_follows_a_live_switch_without_being_rebuilt(string styleName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();
            sidebar.AlertsBadge.Set("441", "441 unacknowledged findings");
            sidebar.OverviewBadge.Set("!", "gateway stopped");
            sidebar.Host.Relayout();
            var badge = sidebar.AlertsBadge.Badge;
            var before = FillOf(badge);

            fixture.Service.SetStyle(style);
            sidebar.Host.Relayout();

            // The same instance, switched live: its fill is the style's critical tone (the bridge maps the tokens onto
            // WPF-UI's InfoBadge resources, which the template reads dynamically).
            Assert.Same(badge, sidebar.Alerts.InfoBadge);
            Assert.Equal(AppearanceFixture.ColorOf("InfoBadgeCriticalSeverityBackgroundBrush"), FillOf(badge));
            Assert.Equal(AppearanceFixture.ColorOf("InfoBadgeCautionSeverityBackgroundBrush"), FillOf(sidebar.OverviewBadge.Badge));
            if (style != AppearanceStyle.Default)
            {
                Assert.NotEqual(before, FillOf(badge));
            }

            sidebar.Host.Png("sidebar-badge-" + styleName.ToLowerInvariant());

            fixture.Service.SetMode(AppearanceMode.Light);
            sidebar.Host.Relayout();
            Assert.Equal(AppearanceFixture.ColorOf("InfoBadgeCriticalSeverityBackgroundBrush"), FillOf(badge));
            Assert.NotEqual(AppearanceFixture.ColorOf("ApplicationBackgroundBrush"), FillOf(badge));
            sidebar.Host.Png("sidebar-badge-" + styleName.ToLowerInvariant() + "-light");
        });
    }

    /// <summary>The colour the capsule's own background is drawn with (the template's border).</summary>
    private static Color FillOf(InfoBadge badge)
    {
        var border = VisualTree.Descendants<System.Windows.Controls.Border>(badge).First();
        return ((SolidColorBrush)border.Background).Color;
    }
}
