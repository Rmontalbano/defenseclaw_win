using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Guardrail;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Guardrail;

/// <summary>
/// The protected-file watch's setting (CUST-422): shown only on a per-user 1.0.6 or later, what it says is checked, the exact reviewed command, the review's warnings, and
/// the restart it queues. Nothing runs: the composition's CLI cannot be found, and no test confirms a review. Verified against upstream source, not a 1.0.6 run.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ProtectedFilesWatchViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private async Task<ProtectedFilesWatchViewModel> CreateAsync(string? version = "1.0.6", bool managed = false, string? config = null)
    {
        _services = TestServices.Create(_temp, config ?? "guardrail:\n  enabled: true\n", version is null ? null : FirstRun.FirstRunFailModeTests.Versioned(version));
        if (version is not null)
        {
            _ = await _services.Runtime.RefreshAsync();
        }

        if (managed)
        {
            _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        }

        var services = _services;
        return UiThread.Run(() => new ProtectedFilesWatchViewModel(services, new DiscoverActionReview(services)));
    }

    // ------------------------------------------------------------------ where it shows

    [Theory]
    [InlineData("1.0.6", false, true)]
    [InlineData("1.1.0", false, true)]
    [InlineData("1.0.6", true, false)]
    [InlineData("1.0.5", false, false)]
    [InlineData("1.0.5", true, false)]
    [InlineData("0.8.10", false, false)]
    [InlineData(null, false, false)]
    public async Task It_shows_only_on_a_per_user_runtime_with_the_watch(string? version, bool managed, bool expected)
    {
        var vm = await CreateAsync(version, managed);

        Assert.Equal(expected, vm.IsShown);
    }

    [Fact]
    public void The_Setup_panel_hosts_it_under_the_guardrail_controls_and_binds_its_visibility()
    {
        var xaml = AppSource.Read("SetupPanel.xaml");

        Assert.Contains("guardrail:ProtectedFilesWatchView", xaml, StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding ProtectedFiles}\"", xaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ what it says

    [Fact]
    public async Task Unset_reads_as_alert_on_the_Windows_built_in_list_and_claims_nothing_more()
    {
        var vm = await CreateAsync();

        Assert.Equal(ProtectedFilesSettings.Alert, vm.SelectedMode);
        Assert.Equal("Alert", vm.BadgeText);
        Assert.False(vm.CanReview);
        Assert.Contains(".ssh/authorized_keys, .ssh/authorized_keys2", vm.WatchedText, StringComparison.Ordinal);
        Assert.Contains("Linux and macOS only", vm.WatchedText, StringComparison.Ordinal);
        Assert.DoesNotContain(".bashrc", vm.WatchedText, StringComparison.Ordinal);
        Assert.Contains("while it runs", vm.Summary, StringComparison.Ordinal);
        Assert.Contains("blocks nothing", vm.Summary, StringComparison.Ordinal);
        Assert.Contains("Attribution is by time", vm.AttributionText, StringComparison.Ordinal);
        Assert.Contains("refuses it", vm.RollbackText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Off_says_nothing_is_checked_and_configured_paths_say_they_replace_the_list()
    {
        var off = await CreateAsync(config: "guardrail:\n  protected_files:\n    on_change: off\n    paths: [.ssh/authorized_keys]\n");
        Assert.Equal("Off", off.BadgeText);
        Assert.Equal("Nothing is checked: on_change is off.", off.WatchedText);

        Assert.Equal(
            "Checked, in your home folder (guardrail.protected_files.paths, which replaces the built-in list): .ssh/authorized_keys, .config/app/.",
            ProtectedFilesWatchViewModel.WatchedSentence(new ProtectedFilesSettings(ProtectedFilesSettings.Alert, false, null, new[] { ".ssh/authorized_keys", ".config/app/" })));
    }

    [Fact]
    public async Task A_value_the_runtime_refuses_is_said_and_can_be_replaced()
    {
        var vm = await CreateAsync(config: "guardrail:\n  protected_files:\n    on_change: sometimes\n");

        Assert.True(vm.HasUnrecognized);
        Assert.Contains("sometimes", vm.UnrecognizedText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the command

    [Fact]
    public void The_argv_is_config_set_and_going_back_to_the_default_is_config_unset()
    {
        Assert.Equal(["config", "set", "guardrail.protected_files.on_change", "restore"], ProtectedFilesWatchViewModel.ArgvFor(ProtectedFilesSettings.Restore));
        Assert.Equal(["config", "set", "guardrail.protected_files.on_change", "off"], ProtectedFilesWatchViewModel.ArgvFor(ProtectedFilesSettings.Off));
        Assert.Equal(["config", "unset", "guardrail.protected_files.on_change"], ProtectedFilesWatchViewModel.ArgvFor(ProtectedFilesSettings.Alert));
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("off")]
    [InlineData("alert")]
    public void The_commands_do_not_restart_the_gateway_and_the_review_says_so(string mode)
    {
        Assert.False(CommandReview.RestartsGatewayFor(ProtectedFilesWatchViewModel.ArgvFor(mode)));
        Assert.Contains(ProtectedFilesWatchViewModel.WarningsFor(mode), w => w.Message == ProtectedFileWatchFacts.RestartNote);
    }

    [Fact]
    public async Task The_review_of_restore_shows_the_warning_and_runs_nothing_until_confirmed()
    {
        _services = TestServices.Create(_temp, "guardrail:\n  enabled: true\n", FirstRun.FirstRunFailModeTests.Versioned("1.0.6"));
        _ = await _services.Runtime.RefreshAsync();
        var services = _services;

        UiThread.Run(() =>
        {
            var review = new DiscoverActionReview(services);
            var vm = new ProtectedFilesWatchViewModel(services, review);
            vm.SelectedMode = ProtectedFilesSettings.Restore;
            Assert.True(vm.CanReview);
            Assert.Contains("Off in every pack", vm.SelectedNote, StringComparison.Ordinal);

            vm.ReviewChangeCommand.Execute(null);

            Assert.True(review.IsOpen);
            var opened = review.CommandReview!;
            var argv = Assert.Single(opened.Steps).Argv;
            Assert.Equal(["config", "set", "guardrail.protected_files.on_change", "restore"], argv);
            Assert.False(opened.RestartsGateway);

            var warnings = string.Join(' ', opened.Warnings.Select(w => w.Message));
            Assert.Contains("puts back the last accepted content", warnings, StringComparison.Ordinal);
            Assert.Contains("a file the agent created is removed", warnings, StringComparison.Ordinal);
            Assert.Contains("strict setups only", warnings, StringComparison.Ordinal);
            Assert.Contains("off in every pack", warnings, StringComparison.Ordinal);
            Assert.Contains("Configuration tools that rewrite these files", warnings, StringComparison.Ordinal);
            Assert.Contains("refuses it", warnings, StringComparison.Ordinal);
            Assert.Empty(services.Cli.Activity);
        });
    }

    [Fact]
    public void Off_warns_that_nothing_is_reported_and_alert_has_no_rollback_warning_because_nothing_is_written()
    {
        Assert.Contains(ProtectedFilesWatchViewModel.WarningsFor(ProtectedFilesSettings.Off), w => w.Title == "This turns the watch off");
        Assert.DoesNotContain(ProtectedFilesWatchViewModel.WarningsFor(ProtectedFilesSettings.Off), w => w.Message == ProtectedFileWatchFacts.RestoreWarning);
        Assert.DoesNotContain(ProtectedFilesWatchViewModel.WarningsFor(ProtectedFilesSettings.Alert), w => w.Message == ProtectedFileWatchFacts.RollbackWarning);
        Assert.Contains(ProtectedFilesWatchViewModel.WarningsFor(ProtectedFilesSettings.Restore), w => w.Message == ProtectedFileWatchFacts.RollbackWarning);
    }

    [Fact]
    public async Task A_read_only_installation_cannot_open_a_review()
    {
        _services = TestServices.Create(_temp, "guardrail:\n  enabled: true\n", FirstRun.FirstRunFailModeTests.Versioned("1.0.6"));
        _ = await _services.Runtime.RefreshAsync();
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        var services = _services;

        UiThread.Run(() =>
        {
            var review = new DiscoverActionReview(services);
            var vm = new ProtectedFilesWatchViewModel(services, review);
            vm.SelectedMode = ProtectedFilesSettings.Off;

            Assert.False(vm.CanReview);
            Assert.False(vm.ReviewChangeCommand.CanExecute(null));
            Assert.False(review.IsOpen);
        });
    }

    [Fact]
    public async Task A_change_that_ran_joins_the_restart_queue_and_one_that_failed_does_not()
    {
        var vm = await CreateAsync();
        var services = _services!;

        await UiThread.Run(() => vm.OnFinishedAsync(new DiscoverReviewResult(false, Array.Empty<CliInvocation>()), ProtectedFilesSettings.Restore));
        Assert.Empty(services.RestartQueue.Reason);

        await UiThread.Run(() => vm.OnFinishedAsync(new DiscoverReviewResult(true, Array.Empty<CliInvocation>()), ProtectedFilesSettings.Restore));
        Assert.Contains("guardrail.protected_files.on_change", services.RestartQueue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_choice_part_way_through_survives_a_reload_and_an_untouched_one_follows_the_file()
    {
        var vm = await CreateAsync();
        var services = _services!;
        UiThread.Run(() => vm.SelectedMode = ProtectedFilesSettings.Off);

        _ = _temp.WriteFile("config.yaml", "guardrail:\n  protected_files:\n    on_change: restore\n");
        services.ReloadConfig();
        UiThread.Run(vm.Refresh);
        Assert.Equal(ProtectedFilesSettings.Off, vm.SelectedMode);
        Assert.Equal("Restore", vm.BadgeText);

        UiThread.Run(() => vm.SelectedMode = ProtectedFilesSettings.Restore);
        _ = _temp.WriteFile("config.yaml", "guardrail:\n  enabled: true\n");
        services.ReloadConfig();
        UiThread.Run(vm.Refresh);
        Assert.Equal(ProtectedFilesSettings.Alert, vm.SelectedMode);
    }

    // ------------------------------------------------------------------ the config editor

    [Fact]
    public void The_config_editor_offers_the_three_values_for_a_file_that_sets_the_key()
    {
        var info = DefenseClaw.App.ViewModels.ConfigEditor.ConfigFieldCatalog.Resolve("guardrail.protected_files.on_change");

        Assert.NotNull(info);
        Assert.Equal(["alert", "restore", "off"], info.Options);
    }

    // ------------------------------------------------------------------ pictures

    public static IEnumerable<object[]> Renders() =>
        from size in new[] { (980, 692), (1468, 875) }
        from mode in new[] { "Light", "Dark" }
        select new object[] { size.Item1, size.Item2, mode };

    [Theory]
    [MemberData(nameof(Renders))]
    public async Task Rendered_setting(int width, int height, string mode)
    {
        var vm = await CreateAsync();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));

        UiThread.Run(() =>
        {
            vm.SelectedMode = ProtectedFilesSettings.Restore;
            var view = new ProtectedFilesWatchView { DataContext = vm, VerticalAlignment = System.Windows.VerticalAlignment.Top };
            using var host = new OffscreenHost(view, width, height);

            var combo = VisualTree.Find<ComboBox>(view, c => AutomationProperties.GetName(c) == "What the gateway does on a protected-file change")!;
            Assert.Equal(3, combo.Items.Count);
            Assert.Contains(VisualTree.Descendants<TextBlock>(view), t => t.Text == "Protected files");
            Assert.Contains(VisualTree.Descendants<Wpf.Ui.Controls.Button>(view), b => Equals(b.Content, "Review change") && b.IsEnabled);

            RenderTo.Png(host, string.Create(CultureInfo.InvariantCulture, $"cust422-setting-{mode.ToLowerInvariant()}-{width}x{height}"));
        });
    }
}
