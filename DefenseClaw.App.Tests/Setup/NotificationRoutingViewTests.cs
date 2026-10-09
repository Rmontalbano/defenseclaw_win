using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The notification routing dialog (CUST-271) as the operator sees it: built over its view-model, laid out at the size of the Setup page, with the
/// review of two moved switches on top of it. Pictures are written only when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class NotificationRoutingViewTests
{
    private static string HelpText() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup-notifications-set.txt"));

    /// <summary>A dialog over a scratch composition, and the things to let go of when the test is done.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Scope(string yaml = "", bool managed = false)
        {
            _services = TestServices.Create(_temp, configYaml: yaml, installation: managed ? TestInstallations.Managed(_temp.Path) : null);
            Vm = new NotificationRoutingViewModel(_services, _ => Task.FromResult(new HelpProbeResult(HelpText(), null)));
            Dialog = new NotificationRoutingDialog { DataContext = Vm };
            Host = new OffscreenHost(Dialog, 940, 760);
        }

        public NotificationRoutingViewModel Vm { get; }

        public NotificationRoutingDialog Dialog { get; }

        public OffscreenHost Host { get; }

        public string DataDirectory => _temp.Path;

        public void Dispose()
        {
            Host.Dispose();
            Vm.Dispose();
            _services.Dispose();
            _temp.Dispose();
        }
    }

    private static IEnumerable<string?> ButtonTexts(DependencyObject root) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(root).Where(b => b.IsVisible).Select(b => b.Content?.ToString());

    private static Wpf.Ui.Controls.Button ButtonNamed(DependencyObject root, string content) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(root).Single(b => b.Content?.ToString() == content);

    [Fact]
    public void A_dialog_that_is_not_open_shows_nothing_and_covers_nothing()
    {
        UiThread.Run(() =>
        {
            using var scope = new Scope();

            Assert.DoesNotContain(VisualTree.Descendants<ToggleSwitch>(scope.Dialog), t => t.IsVisible);
            Assert.DoesNotContain(VisualTree.Descendants<CommandReviewControl>(scope.Dialog), c => c.IsVisible);
            Assert.Empty(ButtonTexts(scope.Dialog));
        });
    }

    [Theory]
    [InlineData("Cisco", "Light")]
    [InlineData("Linear", "Dark")]
    public void The_open_dialog_has_the_master_switch_six_toggles_and_the_review_buttons_and_renders(string styleName, string modeName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));

        UiThread.Run(() =>
        {
            using var scope = new Scope("notifications:\n  block_would_block: true\n");
            scope.Vm.Open();
            scope.Host.Relayout();

            var toggles = VisualTree.Descendants<ToggleSwitch>(scope.Dialog).Where(t => t.IsVisible).ToList();
            Assert.Equal(6, toggles.Count);
            Assert.All(toggles, t => Assert.True(t.IsEnabled)); // the CLI's help lists every slot

            var texts = ButtonTexts(scope.Dialog).ToList();
            Assert.Contains("Turn off…", texts);
            Assert.Contains("Review changes…", texts);
            Assert.Contains("Close", texts);
            Assert.Contains("Refresh", texts);

            // Nothing moved: review is off and the footer says why.
            Assert.False(ButtonNamed(scope.Dialog, "Review changes…").IsEnabled);
            Assert.Equal("Nothing to apply", scope.Vm.Summary);
            Assert.DoesNotContain(VisualTree.Descendants<CommandReviewControl>(scope.Dialog), c => c.IsVisible);
            RenderTo.Png(scope.Host, $"cust271-routing-clean-{style}-{mode}");
        });
    }

    [Theory]
    [InlineData("Cisco", "Light")]
    [InlineData("Linear", "Dark")]
    public void Two_moved_switches_enable_review_and_the_review_shows_both_commands_on_top_of_the_dialog(string styleName, string modeName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));

        UiThread.Run(() =>
        {
            using var scope = new Scope();
            scope.Vm.Open();

            scope.Vm.CategoryRows.Single(r => r.Slot.Id == "block_would_block").IsOn = true;
            scope.Vm.SourceRows.Single(r => r.Slot.Id == "sources.hook").IsOn = false;
            scope.Host.Relayout();

            Assert.True(ButtonNamed(scope.Dialog, "Review changes…").IsEnabled);
            Assert.Equal("2 changes", scope.Vm.Summary);
            RenderTo.Png(scope.Host, $"cust271-routing-two-moved-{style}-{mode}");

            scope.Vm.ReviewChangesCommand.Execute(null);
            scope.Host.Relayout();

            var control = Assert.Single(VisualTree.Descendants<CommandReviewControl>(scope.Dialog), c => c.IsVisible);
            Assert.Same(scope.Vm.Review.CommandReview, control.Review);
            Assert.Equal(2, control.Review!.Steps.Count);
            RenderTo.Png(scope.Host, $"cust271-routing-review-{style}-{mode}");
        });
    }

    [Fact]
    public void A_read_only_installation_turns_the_switches_and_the_buttons_off_and_the_banner_says_why()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            using var scope = new Scope(managed: true);
            scope.Vm.Open();
            scope.Host.Relayout();

            Assert.All(VisualTree.Descendants<ToggleSwitch>(scope.Dialog).Where(t => t.IsVisible), t => Assert.False(t.IsEnabled));
            Assert.False(ButtonNamed(scope.Dialog, "Turn off…").IsEnabled);
            Assert.False(ButtonNamed(scope.Dialog, "Review changes…").IsEnabled);
            Assert.Contains(VisualTree.Descendants<InfoBar>(scope.Dialog), bar => bar.IsOpen && bar.Message == scope.Vm.InstallationBlockedReason);
            RenderTo.Png(scope.Host, "cust271-routing-read-only-Cisco-Light");
        });
    }
}
