using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Paths;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// What a read-only installation looks like on the real views (CUST-308), in the shell stand-in: the Overview's banner and its button, the Scan
/// Skills button that is off with the reason as its tooltip, the Settings -> Connection -> Installation block and the switch it turns off, and a
/// Govern toolbar button. A PNG of the Overview banner and of the Settings block is written when the <c>DC_RENDER_DIR</c> environment variable
/// names a folder, and never otherwise.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationViewTests
{
    // ------------------------------------------------------------------ the Overview

    private sealed class OverviewScreen : IDisposable
    {
        private readonly OverviewScene _data;
        private readonly PanelShell _shell;

        private OverviewScreen(OverviewScene data, PanelShell shell, OverviewPanel panel)
        {
            _data = data;
            _shell = shell;
            Panel = panel;
        }

        public OverviewPanel Panel { get; }

        public AppServices Services => _data.Services;

        public OffscreenHost Host => _shell.Host;

        public OverviewPanelViewModel ViewModel => (OverviewPanelViewModel)_shell.ViewModel;

        /// <summary>The Overview of a scene whose installation is <paramref name="installation"/> (null: the usual writable one).</summary>
        public static OverviewScreen Open(InstallationContext? installation, int width = 1400, int height = 900)
        {
            var data = OverviewScene.Create(seedAudit: true);
            PanelShell? shell = null;
            try
            {
                var panel = UiThread.Run(() =>
                {
                    if (installation is not null)
                    {
                        data.Services.Installation.Replace(installation);
                    }

                    data.Publish(OverviewScene.Snapshot());
                    shell = new PanelShell(data.Services, width, height);
                    var view = shell.Show<OverviewPanel>();
                    var vm = (OverviewPanelViewModel)shell.ViewModel;
                    vm.Apply(OverviewScene.Snapshot());
                    vm.ApplyStatus(DefenseClawStatusReader.Parse(OverviewScene.StatusJson));
                    shell.Host.Relayout();
                    return view;
                });

                return new OverviewScreen(data, shell!, panel);
            }
            catch
            {
                UiThread.Run(() => shell?.Dispose());
                data.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            UiThread.Run(() => _shell.Dispose());
            _data.Dispose();
        }
    }

    private static IEnumerable<TextBlock> BannerText(OverviewPanel panel) =>
        VisualTree.Descendants<TextBlock>(panel).Where(t => t.Text.StartsWith("State-changing actions disabled", StringComparison.Ordinal));

    private static Wpf.Ui.Controls.Button ButtonLabelled(DependencyObject root, string label) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(root).First(b => b.Content as string == label);

    [Fact]
    public void A_writable_installation_shows_no_banner_and_its_scan_button_keeps_its_usual_tooltip()
    {
        using var screen = OverviewScreen.Open(null);

        UiThread.Run(() =>
        {
            Assert.DoesNotContain(BannerText(screen.Panel), t => t.IsVisible);

            var scan = ButtonLabelled(screen.Panel, "Scan Skills");
            Assert.True(scan.IsEnabled);
            Assert.StartsWith("Scan every configured skill", (string)scan.ToolTip, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_managed_installation_shows_the_banner_with_its_button_and_turns_scan_skills_off_with_the_reason_as_its_tooltip()
    {
        using var screen = OverviewScreen.Open(TestInstallations.ManagedByConfigMode());

        UiThread.Run(() =>
        {
            var banner = Assert.Single(BannerText(screen.Panel), t => t.IsVisible);
            Assert.Equal("State-changing actions disabled: " + TestInstallations.ManagedReason, banner.Text);

            var review = ButtonLabelled(screen.Panel, "Review Installation");
            Assert.True(review.IsVisible);
            Assert.True(review.IsEnabled);

            var scan = ButtonLabelled(screen.Panel, "Scan Skills");
            Assert.False(scan.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, scan.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(scan));

            // The gateway button beside it says the same.
            var gateway = VisualTree.Descendants<Wpf.Ui.Controls.Button>(screen.Panel).First(b => b.Content as string == screen.ViewModel.GatewayActionLabel);
            Assert.False(gateway.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, gateway.ToolTip);

            RenderTo.Png(screen.Host, "cust308-overview-banner");
        });
    }

    [Fact]
    public void Review_Installation_opens_Settings()
    {
        using var screen = OverviewScreen.Open(TestInstallations.ManagedByConfigMode());

        UiThread.Run(() =>
        {
            // The shell's catalog takes a request for a panel that exists as it is raised, so it is caught as it goes by.
            NavigationRequest? requested = null;
            screen.Services.Navigation.Requested += (_, e) => requested = e.Request;

            var review = ButtonLabelled(screen.Panel, "Review Installation");
            Assert.Same(screen.ViewModel.ReviewInstallationCommand, review.Command);
            review.Command.Execute(review.CommandParameter);

            Assert.Equal(new NavigationRequest("settings"), requested);
        });
    }

    [Fact]
    public void The_banner_appears_when_the_installation_turns_read_only_while_the_page_is_open_and_goes_when_it_is_fixed()
    {
        using var screen = OverviewScreen.Open(null);

        UiThread.Run(() =>
        {
            Assert.DoesNotContain(BannerText(screen.Panel), t => t.IsVisible);

            screen.Services.Installation.Replace(TestInstallations.Managed());
            screen.Host.Relayout();
            Assert.Single(BannerText(screen.Panel), t => t.IsVisible);
            Assert.False(ButtonLabelled(screen.Panel, "Scan Skills").IsEnabled);

            screen.Services.Installation.Replace(TestInstallations.UserDefault());
            screen.Host.Relayout();
            Assert.DoesNotContain(BannerText(screen.Panel), t => t.IsVisible);
            Assert.True(ButtonLabelled(screen.Panel, "Scan Skills").IsEnabled);
        });
    }

    // ------------------------------------------------------------------ Settings

    private sealed class SettingsScreen : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;
        private readonly PanelShell _shell;

        public SettingsScreen(Func<string, InstallationContext?> installation, int width = 1100, int height = 760)
        {
            _services = TestServices.Create(_temp, "config_version: 8\ngateway:\n  api_port: 18970\n", installation: installation(_temp.Path));
            _shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                Page = shell.Show<SettingsPanel>();
                return shell;
            });
            UiThread.Run(() =>
            {
                var model = (SettingsPanelViewModel)Page.DataContext;
                var look = model.RefreshMachineFactsAsync();
                var deadline = Environment.TickCount64 + 30_000;
                while (!look.IsCompleted && Environment.TickCount64 < deadline)
                {
                    UiThread.Settle();
                    Thread.Sleep(10);
                }

                _shell.Host.Relayout();
            });
        }

        public SettingsPanel Page { get; private set; } = null!;

        public OffscreenHost Host => _shell.Host;

        public void Dispose()
        {
            UiThread.Run(() => _shell.Dispose());
            _services.Dispose();
            _temp.Dispose();
        }
    }

    private static HeaderedContentControl Row(DependencyObject page, string header) =>
        VisualTree.Descendants<HeaderedContentControl>(page).First(row => row.Header as string == header);

    private static bool Shows(DependencyObject root, string text) =>
        VisualTree.Descendants<TextBlock>(root).Any(t => t.IsVisible && t.Text == text) ||
        VisualTree.Descendants<TextBox>(root).Any(t => t.IsVisible && t.Text == text);

    private static void ScrollToInstallation(SettingsScreen screen)
    {
        var heading = VisualTree.Descendants<TextBlock>(screen.Page).First(t => t.Text == "Installation" && t.IsVisible);
        heading.BringIntoView(new Rect(0, 0, heading.ActualWidth, heading.ActualHeight + 260));
        screen.Host.Relayout();
    }

    [Fact]
    public void The_installation_block_of_a_user_default_install_says_so_and_has_no_reason()
    {
        using var screen = new SettingsScreen(_ => null);

        UiThread.Run(() =>
        {
            Assert.True(Shows(Row(screen.Page, "Selected by"), "User default"));
            Assert.True(Shows(Row(screen.Page, "Access"), "Unmanaged — setup changes allowed"));
            Assert.True(Shows(Row(screen.Page, "Override"), "None (automatic)"));

            var model = (SettingsPanelViewModel)screen.Page.DataContext;
            Assert.False(model.HasInstallationReason);
            Assert.True(model.CanChangeAutoStart);
        });
    }

    [Fact]
    public void The_installation_block_of_a_managed_install_names_what_chose_it_the_access_and_the_reason_and_turns_the_auto_start_off()
    {
        using var screen = new SettingsScreen(home => TestInstallations.Managed(home));

        UiThread.Run(() =>
        {
            Assert.True(Shows(Row(screen.Page, "Selected by"), "Managed installation (Cisco Secure Client)"));
            Assert.True(Shows(Row(screen.Page, "Access"), "Managed enterprise — read only"));
            Assert.True(Shows(Row(screen.Page, "Access"), TestInstallations.ManagedReason), "the reason is the caption under Access");
            Assert.True(Shows(Row(screen.Page, "Override"), "None (automatic)"));

            // The switch that arms an automatic gateway start is off, and says why on hover.
            var toggle = VisualTree.Descendants<ToggleSwitch>(screen.Page).First(t => AutomationProperties.GetName(t) == "Start the gateway automatically");
            Assert.False(toggle.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, toggle.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(toggle));

            ScrollToInstallation(screen);
            RenderTo.Png(screen.Host, "cust308-settings-installation");
        });
    }

    [Fact]
    public void The_installation_block_of_an_invalid_selection_says_why_it_cannot_be_trusted()
    {
        using var screen = new SettingsScreen(_ => TestInstallations.Invalid());

        UiThread.Run(() =>
        {
            Assert.True(Shows(Row(screen.Page, "Selected by"), "DEFENSECLAW_HOME"));
            Assert.True(Shows(Row(screen.Page, "Access"), "Invalid installation selection — read only"));
            var model = (SettingsPanelViewModel)screen.Page.DataContext;
            Assert.True(Shows(Row(screen.Page, "Access"), model.InstallationReason));
            Assert.Contains("absolute path", model.InstallationReason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_developer_selectors_folder_is_the_override_row()
    {
        using var screen = new SettingsScreen(_ => TestInstallations.DeveloperRuntime());

        UiThread.Run(() =>
        {
            Assert.True(Shows(Row(screen.Page, "Selected by"), "Developer runtime selector (Settings → Advanced)"));
            Assert.True(Shows(Row(screen.Page, "Override"), TestInstallations.DeveloperHome));
        });
    }

    // ------------------------------------------------------------------ a Govern panel

    [Fact]
    public void A_govern_toolbar_button_is_off_on_a_managed_installation_with_the_reason_as_its_tooltip_not_a_stale_list_reason()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, installation: TestInstallations.ManagedAt(temp.Path));

        UiThread.Run(() =>
        {
            using var shell = new PanelShell(services, 1200, 800);
            var page = shell.Show<SkillsPanel>();

            var install = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page).First(b => AutomationProperties.GetName(b) == "Install skill…");
            Assert.False(install.IsEnabled);
            Assert.Equal(TestInstallations.ManagedReason, install.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(install));
        });
    }
}
