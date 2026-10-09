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
/// The AI Discovery tuning dialog (CUST-271) as the operator sees it: built over its view-model, laid out at the size of the page, with the review
/// of a cadence change on top. Pictures are written only when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryTuningViewTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", name));

    private static Task<HelpProbeResult> Screens(IReadOnlyList<string> path, CancellationToken _) =>
        Task.FromResult(new HelpProbeResult(Fixture(path[^1] == "disable" ? "agent-discovery-disable.txt" : "agent-discovery-enable.txt"), null));

    private sealed class Scope : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Scope(string yaml = "ai_discovery:\n  enabled: true\n", bool managed = false)
        {
            _services = TestServices.Create(_temp, configYaml: yaml, installation: managed ? TestInstallations.Managed(_temp.Path) : null);
            Vm = new AiDiscoveryTuningViewModel(_services, Screens);
            Dialog = new AiDiscoveryTuningDialog { DataContext = Vm };
            Host = new OffscreenHost(Dialog, 940, 900);
        }

        public AiDiscoveryTuningViewModel Vm { get; }

        public AiDiscoveryTuningDialog Dialog { get; }

        public OffscreenHost Host { get; }

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
    public void A_dialog_that_is_not_open_shows_nothing()
    {
        UiThread.Run(() =>
        {
            using var scope = new Scope();

            Assert.DoesNotContain(VisualTree.Descendants<ToggleSwitch>(scope.Dialog), t => t.IsVisible);
            Assert.Empty(ButtonTexts(scope.Dialog));
        });
    }

    [Theory]
    [InlineData("Cisco", "Light")]
    [InlineData("Linear", "Dark")]
    public void The_open_dialog_has_the_enabled_switch_six_option_switches_and_the_boxes_and_renders(string styleName, string modeName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));

        UiThread.Run(() =>
        {
            using var scope = new Scope();
            scope.Vm.Open();
            scope.Host.Relayout();

            // On/off, four sources, two privacy options.
            Assert.Equal(7, VisualTree.Descendants<ToggleSwitch>(scope.Dialog).Count(t => t.IsVisible));
            Assert.Contains("Review changes…", ButtonTexts(scope.Dialog));
            Assert.False(ButtonNamed(scope.Dialog, "Review changes…").IsEnabled); // nothing moved
            RenderTo.Png(scope.Host, $"cust271-tuning-clean-{style}-{mode}");
        });
    }

    [Theory]
    [InlineData("Cisco", "Light")]
    [InlineData("Linear", "Dark")]
    public void A_cadence_change_shows_the_command_in_the_form_and_the_review_on_top_of_it(string styleName, string modeName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));

        UiThread.Run(() =>
        {
            using var scope = new Scope();
            scope.Vm.Open();
            scope.Vm.ScanInterval.Text = "10";
            scope.Host.Relayout();

            Assert.True(ButtonNamed(scope.Dialog, "Review changes…").IsEnabled);
            Assert.Contains(
                VisualTree.Descendants<System.Windows.Controls.TextBlock>(scope.Dialog),
                t => t.IsVisible && t.Text == "defenseclaw agent discovery enable --yes --scan-interval-min 10");
            RenderTo.Png(scope.Host, $"cust271-tuning-cadence-{style}-{mode}");

            scope.Vm.ReviewChangesCommand.Execute(null);
            scope.Host.Relayout();

            var control = Assert.Single(VisualTree.Descendants<CommandReviewControl>(scope.Dialog), c => c.IsVisible);
            Assert.Equal(
                "defenseclaw agent discovery enable --yes --scan-interval-min 10",
                Assert.Single(control.Review!.Steps).CommandText);
            RenderTo.Png(scope.Host, $"cust271-tuning-review-{style}-{mode}");
        });
    }

    [Fact]
    public void A_value_out_of_range_shows_its_problem_beside_the_box_and_turns_review_off()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            using var scope = new Scope();
            scope.Vm.Open();
            scope.Vm.ScanInterval.Text = "0";
            scope.Host.Relayout();

            Assert.False(ButtonNamed(scope.Dialog, "Review changes…").IsEnabled);
            Assert.Contains(
                VisualTree.Descendants<System.Windows.Controls.TextBlock>(scope.Dialog),
                t => t.IsVisible && t.Text == scope.Vm.ScanInterval.Problem);
            RenderTo.Png(scope.Host, "cust271-tuning-problem-Cisco-Light");
        });
    }

    [Fact]
    public void A_read_only_installation_turns_the_controls_off_and_the_banner_says_why()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            using var scope = new Scope(managed: true);
            scope.Vm.Open();
            scope.Host.Relayout();

            Assert.All(VisualTree.Descendants<ToggleSwitch>(scope.Dialog).Where(t => t.IsVisible), t => Assert.False(t.IsEnabled));
            Assert.False(ButtonNamed(scope.Dialog, "Review changes…").IsEnabled);
            Assert.Contains(VisualTree.Descendants<InfoBar>(scope.Dialog), bar => bar.IsOpen && bar.Message == scope.Vm.InstallationBlockedReason);
        });
    }
}
