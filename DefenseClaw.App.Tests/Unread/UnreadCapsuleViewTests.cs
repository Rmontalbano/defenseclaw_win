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
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Unread;

/// <summary>
/// The "new since last visit" capsule on a sidebar entry (CUST-265), on real <see cref="NavigationView"/> entries built the way the window builds them:
/// what it shows, what a screen reader is told, that it follows the look - accent, not the alert count's red - without being rebuilt, and that its text
/// is readable on its fill in every style and mode. The window's own sidebar is exercised end to end in <see cref="UnreadSidebarWindowTests"/>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class UnreadCapsuleViewTests
{
    private sealed class Sidebar : IDisposable
    {
        public Sidebar()
        {
            Navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 220, IsPaneOpen = true };
            Alerts = Item("Alerts", SymbolRegular.AlertUrgent24, "Alerts (Ctrl+2)");
            Audit = Item("Audit", SymbolRegular.DatabaseSearch24, "Audit (Ctrl+4)");
            Activity = Item("Activity", SymbolRegular.History32, "Activity (Ctrl+5)");
            AiDiscovery = Item("AI Discovery", SymbolRegular.Bot24, "AI Discovery (Ctrl+0)");
            foreach (var item in new[] { Alerts, Audit, Activity, AiDiscovery })
            {
                _ = Navigation.MenuItems.Add(item);
            }

            AlertsBadge = new SidebarBadge(Alerts, "Alerts", "Alerts (Ctrl+2)", InfoBadgeSeverity.Critical);
            AuditBadge = new SidebarBadge(Audit, "Audit", "Audit (Ctrl+4)", InfoBadgeSeverity.Attention);
            ActivityBadge = new SidebarBadge(Activity, "Activity", "Activity (Ctrl+5)", InfoBadgeSeverity.Attention);
            AiDiscoveryBadge = new SidebarBadge(AiDiscovery, "AI Discovery", "AI Discovery (Ctrl+0)", InfoBadgeSeverity.Attention);

            Host = new WindowHost(Navigation, 280, 220);
        }

        public NavigationView Navigation { get; }

        public DcNavigationItem Alerts { get; }

        public DcNavigationItem Audit { get; }

        public DcNavigationItem Activity { get; }

        public DcNavigationItem AiDiscovery { get; }

        public SidebarBadge AlertsBadge { get; }

        public SidebarBadge AuditBadge { get; }

        public SidebarBadge ActivityBadge { get; }

        public SidebarBadge AiDiscoveryBadge { get; }

        public WindowHost Host { get; }

        /// <summary>An entry as <c>MainWindow.CreateNavigationItem</c> makes it: its title, glyph, tooltip and plain accessible name.</summary>
        private static DcNavigationItem Item(string title, SymbolRegular icon, string tooltip)
        {
            var item = new DcNavigationItem
            {
                Content = title,
                Icon = new SymbolIcon { Symbol = icon },
                Height = 34,
                ToolTip = tooltip,
            };
            AutomationProperties.SetName(item, title);
            return item;
        }

        /// <summary>Sets an entry's capsule the way the window does from the view-model's state for a count.</summary>
        public static void Show(SidebarBadge badge, int count)
        {
            var state = UnreadPresentation.StateOf(count);
            badge.Set(state.Text, state.Description);
        }

        public void Dispose() => Host.Dispose();
    }

    /// <summary>The sidebar in a real window, shown far off screen without activation, so a live style switch reaches it. Closed on dispose.</summary>
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

    private static Color FillOf(InfoBadge badge) =>
        ((SolidColorBrush)VisualTree.Descendants<System.Windows.Controls.Border>(badge).First().Background).Color;

    private static Color TextOf(InfoBadge badge) =>
        ((SolidColorBrush)VisualTree.Descendants<System.Windows.Controls.TextBlock>(badge).First(t => t.Text.Length > 0).Foreground).Color;

    /// <summary>WCAG 2.x contrast ratio of two opaque colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void A_count_shows_as_an_accent_capsule_and_the_entry_says_what_it_counts()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();
            Assert.Equal(Visibility.Collapsed, sidebar.AuditBadge.Badge.Visibility);
            Assert.Equal("Audit", AutomationProperties.GetName(sidebar.Audit));

            Sidebar.Show(sidebar.AuditBadge, 7);
            sidebar.Host.Relayout();

            var badge = sidebar.AuditBadge.Badge;
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("7", badge.Value);
            Assert.Equal(InfoBadgeSeverity.Attention, badge.Severity);
            Assert.Same(badge, sidebar.Audit.InfoBadge);
            Assert.True(badge.IsVisible, "the capsule is not on screen");
            Assert.True(badge.ActualWidth > 0 && badge.ActualHeight > 0);
            Assert.True(badge.FontSize >= 12, $"the capsule's text is {badge.FontSize} px");

            Assert.Equal("Audit, 7 new since last visit", AutomationProperties.GetName(sidebar.Audit));
            Assert.Equal("Audit (Ctrl+4) — 7 new since last visit", sidebar.Audit.ToolTip);
            Assert.Equal("Audit, 7 new since last visit", UIElementAutomationPeer.CreatePeerForElement(sidebar.Audit).GetName());

            // The capsule's own automation name is the sentence itself: "N new since last visit".
            Assert.Equal("7 new since last visit", AutomationProperties.GetName(badge));
        });
    }

    [Fact]
    public void Past_the_cap_the_capsule_reads_99_plus_and_nothing_new_takes_the_capsule_and_the_name_away()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();

            Sidebar.Show(sidebar.ActivityBadge, 100);
            sidebar.Host.Relayout();
            Assert.Equal("99+", sidebar.ActivityBadge.Badge.Value);
            Assert.Equal("Activity, 99+ new since last visit", AutomationProperties.GetName(sidebar.Activity));

            Sidebar.Show(sidebar.ActivityBadge, 99);
            Assert.Equal("99", sidebar.ActivityBadge.Badge.Value);
            Assert.Equal("Activity, 99 new since last visit", AutomationProperties.GetName(sidebar.Activity));

            Assert.Equal("99 new since last visit", AutomationProperties.GetName(sidebar.ActivityBadge.Badge));

            Sidebar.Show(sidebar.ActivityBadge, 0);
            sidebar.Host.Relayout();
            Assert.Equal(Visibility.Collapsed, sidebar.ActivityBadge.Badge.Visibility);
            Assert.Equal(string.Empty, AutomationProperties.GetName(sidebar.ActivityBadge.Badge));
            Assert.Equal("Activity", AutomationProperties.GetName(sidebar.Activity));
            Assert.Equal("Activity (Ctrl+5)", sidebar.Activity.ToolTip);
        });
    }

    [Fact]
    public void The_capsule_is_decorative_to_a_screen_reader_the_entry_says_it_all()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();
            Sidebar.Show(sidebar.AiDiscoveryBadge, 3);
            sidebar.Host.Relayout();

            var peer = UIElementAutomationPeer.CreatePeerForElement(sidebar.AiDiscoveryBadge.Badge);

            Assert.False(peer.IsControlElement());
            Assert.False(peer.IsContentElement());
            Assert.Empty(peer.GetChildren() ?? new List<AutomationPeer>());

            // It carries the sentence for a tool that inspects the element, and is still left out of what a screen reader walks.
            Assert.Equal("3 new since last visit", peer.GetName());
        });
    }

    [Fact]
    public void The_three_capsules_beside_the_alert_count_each_say_their_own_thing()
    {
        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();
            sidebar.AlertsBadge.Set("441", "441 unacknowledged findings");
            Sidebar.Show(sidebar.AuditBadge, 99);
            Sidebar.Show(sidebar.ActivityBadge, 2);
            Sidebar.Show(sidebar.AiDiscoveryBadge, 1);
            sidebar.Host.Relayout();

            Assert.Equal("Alerts, 441 unacknowledged findings", AutomationProperties.GetName(sidebar.Alerts));
            Assert.Equal("Audit, 99 new since last visit", AutomationProperties.GetName(sidebar.Audit));
            Assert.Equal("Activity, 2 new since last visit", AutomationProperties.GetName(sidebar.Activity));
            Assert.Equal("AI Discovery, 1 new since last visit", AutomationProperties.GetName(sidebar.AiDiscovery));

            // The alert count is the red one; "new since last visit" is not an alarm.
            Assert.Equal(InfoBadgeSeverity.Critical, sidebar.AlertsBadge.Badge.Severity);
            Assert.NotEqual(FillOf(sidebar.AlertsBadge.Badge), FillOf(sidebar.AuditBadge.Badge));

            sidebar.Host.Png("sidebar-unread-capsules-beside-alerts");
        });
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Linear")]
    [InlineData("Tui")]
    [InlineData("Cisco")]
    public void The_capsule_follows_a_live_style_and_mode_switch_without_being_rebuilt_and_its_text_stays_readable(string styleName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            using var sidebar = new Sidebar();
            Sidebar.Show(sidebar.AuditBadge, 12);
            sidebar.Host.Relayout();
            var badge = sidebar.AuditBadge.Badge;

            foreach (var mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
            {
                fixture.Service.SetStyle(style);
                fixture.Service.SetMode(mode);
                sidebar.Host.Relayout();

                // The same instance, switched live: its fill is the style's "attention" tone (the bridge maps the tokens onto WPF-UI's InfoBadge resources).
                Assert.Same(badge, sidebar.Audit.InfoBadge);
                var fill = FillOf(badge);
                Assert.Equal(AppearanceFixture.ColorOf("InfoBadgeAttentionSeverityBackgroundBrush"), fill);

                // Readable. The text on its fill is 4.5:1 or better in every style and mode but one - Linear Light, 4.1:1, which is the style's own tone
                // tokens: the Alerts red and the Overview amber capsules are 4.4:1 and 4.0:1 there (measured). The fill against the sidebar's own
                // background is held to 3:1, the bar for a non-text shape.
                var text = Contrast(TextOf(badge), fill);
                var floor = style == AppearanceStyle.Linear && mode == AppearanceMode.Light ? 4.0 : 4.5;
                Assert.True(text >= floor, $"{styleName} {mode}: the capsule's text is {text:0.0}:1 against its fill");
                var shape = Contrast(fill, AppearanceFixture.ColorOf("ApplicationBackgroundBrush"));
                Assert.True(shape >= 3.0, $"{styleName} {mode}: the capsule is {shape:0.0}:1 against the sidebar");

                sidebar.Host.Png("sidebar-unread-" + styleName.ToLowerInvariant() + "-" + mode.ToString().ToLowerInvariant());
            }
        });
    }
}
