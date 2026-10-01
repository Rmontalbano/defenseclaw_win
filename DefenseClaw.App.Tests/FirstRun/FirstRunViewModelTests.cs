using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.FirstRun;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>
/// The first-run window's model (CUST-210). The services are isolated (an empty PATH: any CLI call ends in "not found"), so a test that
/// passes proves nothing was run: detection reads a file, the form builds a plan, and the plan only opens a review.
/// </summary>
public sealed class FirstRunViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public FirstRunViewModelTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static GatewaySnapshot Snapshot(InstallState? install, bool running = false) => new()
    {
        State = install switch
        {
            InstallState.NotInstalled => AppGatewayState.NotInstalled,
            InstallState.InstalledNotInitialized => AppGatewayState.NotInitialized,
            InstallState.Running => AppGatewayState.Running,
            InstallState.GatewayStopped => AppGatewayState.GatewayStopped,
            _ => AppGatewayState.Unknown,
        },
        Install = install,
        CliPath = install is null or InstallState.NotInstalled ? null : @"C:\synthetic\defenseclaw.exe",
    };

    private FirstRunViewModel Create(InstallState? install = InstallState.InstalledNotInitialized, Func<CancellationToken, Task<IReadOnlyList<WizardDefinition>>>? catalog = null) =>
        new(_services, catalog ?? (_ => Task.FromResult<IReadOnlyList<WizardDefinition>>(Array.Empty<WizardDefinition>())), Snapshot(install));

    private static WizardDefinition Card(string target, PlatformStatus status) => new()
    {
        Target = target,
        Title = target,
        Group = WizardGroups.Connectors,
        PlatformStatus = status,
    };

    private void WriteScan(params string[] connectors) =>
        _temp.WriteFile(
            "ai_discovery_state.json",
            "{\"signals\":{" + string.Join(",", connectors.Select((c, i) => $"\"s{i}\":{{\"name\":\"{c}\",\"category\":\"active_process\",\"state\":\"seen\",\"supported_connector\":\"{c}\"}}")) + "}}");

    // ---- state rows ----

    [Fact]
    public void The_three_state_rows_say_what_the_snapshot_says()
    {
        using var vm = Create(InstallState.InstalledNotInitialized);

        Assert.Equal(new[] { "Runtime", "Configuration", "Gateway" }, vm.Checks.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { "found", "not initialized", "not running" }, vm.Checks.Select(c => c.Text).ToArray());
        Assert.True(vm.IsInstalled);
        Assert.False(vm.IsInitialized);
    }

    [Fact]
    public void A_running_install_shows_every_row_ok()
    {
        using var vm = Create(InstallState.Running);

        Assert.All(vm.Checks, c => Assert.Equal("Ok", c.Key));
    }

    [Fact]
    public void Not_installed_offers_the_installer_and_nothing_to_run()
    {
        using var vm = Create(InstallState.NotInstalled);

        Assert.False(vm.IsInstalled);
        Assert.Equal("not found", vm.Checks[0].Text);
        Assert.Contains("Install the DefenseClaw runtime first", vm.Subtitle, StringComparison.Ordinal);
        Assert.False(vm.CanReview);
        Assert.False(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public void Before_the_first_poll_the_rows_say_checking_and_nothing_can_run()
    {
        using var vm = Create(install: null);

        Assert.All(vm.Checks, c => Assert.Equal("checking", c.Text));
        Assert.False(vm.CanReview);
    }

    // ---- detection is a button, never automatic, and runs nothing ----

    [Fact]
    public async Task Opening_the_window_detects_nothing()
    {
        WriteScan("codex", "claudecode");
        using var vm = Create(InstallState.InstalledNotInitialized);

        await vm.InitializeAsync();

        Assert.Empty(vm.Detected);
        Assert.False(vm.HasDetected);
        Assert.False(vm.HasDetectionNote);
        Assert.Equal("Detect installed agents", vm.DetectLabel);
    }

    [Fact]
    public async Task Detect_reads_the_scan_preselects_what_it_found_and_runs_no_command()
    {
        WriteScan("claudecode", "codex", "qodo");
        using var vm = Create();

        await vm.DetectCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "codex", "claudecode" }, vm.Detected.Select(d => d.Id).ToArray());
        Assert.All(vm.Detected, d => Assert.True(d.IsRegistered));
        Assert.True(vm.HasDetected);
        Assert.Contains("Found 2 installed agents", vm.DetectionNote, StringComparison.Ordinal);
        Assert.Equal("Detect again", vm.DetectLabel);
        Assert.True(vm.CanReview);
    }

    [Fact]
    public async Task Detect_without_a_scan_says_so_and_leaves_the_fallback_picker()
    {
        using var vm = Create();

        await vm.DetectCommand.ExecuteAsync(null);

        Assert.Empty(vm.Detected);
        Assert.Contains("No AI discovery scan has been recorded", vm.DetectionNote, StringComparison.Ordinal);
        Assert.True(vm.HasDetectionNote);
        Assert.True(vm.CanReview);
    }

    [Fact]
    public async Task Detect_with_an_unreadable_scan_reports_it_rather_than_throwing()
    {
        _ = _temp.WriteFile("ai_discovery_state.json", "{ not json");
        using var vm = Create();

        await vm.DetectCommand.ExecuteAsync(null);

        Assert.Contains("could not be read", vm.DetectionNote, StringComparison.Ordinal);
        Assert.Empty(vm.Detected);
    }

    [Fact]
    public async Task Detecting_again_keeps_the_operators_choices_for_agents_that_are_still_there()
    {
        WriteScan("codex", "claudecode");
        using var vm = Create();
        await vm.DetectCommand.ExecuteAsync(null);
        vm.Detected.Single(d => d.Id == "codex").IsRegistered = false;

        await vm.DetectCommand.ExecuteAsync(null);

        Assert.False(vm.Detected.Single(d => d.Id == "codex").IsRegistered);
        Assert.True(vm.Detected.Single(d => d.Id == "claudecode").IsRegistered);
    }

    // ---- the catalog filters what is offered ----

    [Fact]
    public async Task The_catalog_decides_which_connectors_are_offered_on_windows()
    {
        WriteScan("codex", "omnigent");
        using var vm = Create(catalog: _ => Task.FromResult<IReadOnlyList<WizardDefinition>>(new[]
        {
            Card("codex", PlatformStatus.Certified),
            Card("cursor", PlatformStatus.NotCertified),
            Card("omnigent", PlatformStatus.Unsupported),
        }));

        await vm.InitializeAsync();
        await vm.DetectCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "codex", "cursor" }, vm.Fallbacks.Select(f => f.Id).ToArray());
        Assert.Equal(new[] { "codex" }, vm.Detected.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_leaves_the_built_in_list()
    {
        using var vm = Create(catalog: _ => throw new InvalidOperationException("cli missing"));

        await vm.InitializeAsync();

        Assert.NotEmpty(vm.Fallbacks);
        Assert.Contains(vm.Fallbacks, f => f.Id == "codex");
    }

    // ---- the form ----

    [Fact]
    public async Task Unchecking_every_detected_connector_blocks_the_review_with_the_reason()
    {
        WriteScan("codex", "claudecode");
        using var vm = Create();
        await vm.DetectCommand.ExecuteAsync(null);

        foreach (var choice in vm.Detected)
        {
            choice.IsRegistered = false;
        }

        Assert.False(vm.CanReview);
        Assert.Equal("Select at least one connector to register.", vm.SelectionProblem);
        Assert.False(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_action_profile_needs_a_connector_to_enforce_and_unchecking_register_clears_enforce()
    {
        WriteScan("codex", "claudecode");
        using var vm = Create();
        await vm.DetectCommand.ExecuteAsync(null);

        vm.Profile = "action";
        Assert.Equal("Select at least one connector for Action mode.", vm.SelectionProblem);
        Assert.False(vm.CanReview);

        var codex = vm.Detected.Single(d => d.Id == "codex");
        codex.IsAction = true;
        Assert.True(vm.CanReview);

        codex.IsRegistered = false;
        Assert.False(codex.IsAction);
        Assert.False(vm.CanReview);
    }

    [Fact]
    public async Task The_plan_follows_the_form()
    {
        WriteScan("codex", "claudecode");
        using var vm = Create();
        await vm.DetectCommand.ExecuteAsync(null);
        vm.Detected.Single(d => d.Id == "codex").IsRegistered = false;
        vm.FailMode = "closed";

        var plan = vm.BuildPlan();

        Assert.Equal(new[] { "claudecode" }, plan.Connectors.ToArray());
        Assert.Contains("closed", plan.Steps[0].Argv);
    }

    // ---- nothing runs until the review is confirmed ----

    [Fact]
    public async Task Run_opens_the_review_with_the_exact_commands_and_runs_nothing()
    {
        WriteScan("codex");
        using var vm = Create();
        await vm.DetectCommand.ExecuteAsync(null);

        vm.RunCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        Assert.True(vm.Review.IsConfirming);
        Assert.False(vm.Review.IsRunning);
        var review = vm.Review.CommandReview!;
        Assert.Equal(2, review.Steps.Count);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.StartsWith("defenseclaw init --non-interactive --yes --json-summary --observe-all", review.Steps[0].CommandText, StringComparison.Ordinal);
        Assert.Equal("defenseclaw-gateway start", review.Steps[1].CommandText);
        Assert.Equal("Set up DefenseClaw", review.ConfirmLabel);
        Assert.DoesNotContain(review.Steps.SelectMany(s => s.Argv), a => a.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_connector_not_certified_on_windows_puts_its_caution_in_the_review()
    {
        WriteScan("cursor");
        using var vm = Create(catalog: _ => Task.FromResult<IReadOnlyList<WizardDefinition>>(new[] { Card("cursor", PlatformStatus.NotCertified) }));
        await vm.InitializeAsync();
        await vm.DetectCommand.ExecuteAsync(null);

        vm.RunCommand.Execute(null);

        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Message.Contains("not certified on Windows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_the_review_runs_nothing_and_leaves_the_form()
    {
        using var vm = Create();
        vm.RunCommand.Execute(null);

        vm.Review.DismissCommand.Execute(null);

        Assert.False(vm.Review.IsOpen);
        Assert.False(vm.Review.IsFinished);
        Assert.False(vm.HasReport);
        await Task.CompletedTask;
    }

    // ---- the report ----

    [Fact]
    public void A_report_with_a_warning_lists_it_and_reads_partial()
    {
        using var vm = Create();
        var report = InitReportValidator.Validate(
            "{\"status\":\"partial\",\"setup\":[{\"name\":\"Sidecar\",\"status\":\"skip\",\"detail\":\"not started\",\"next_command\":\"\"}]," +
            "\"readiness\":[{\"name\":\"Judge\",\"status\":\"warn\",\"detail\":\"no key\",\"next_command\":\"\"}],\"next_commands\":[\"defenseclaw-gateway start\"]}");

        vm.ShowReport(report, succeeded: true);

        Assert.True(vm.HasReport);
        Assert.Equal("Warn", vm.ReportKey);
        Assert.Equal(new[] { "Sidecar", "Judge" }, vm.ReportRows.Select(r => r.Name).ToArray());
        Assert.Contains("defenseclaw-gateway start", vm.ReportNext, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_report_reads_bad_with_the_failing_step()
    {
        using var vm = Create();
        var report = InitReportValidator.Validate(
            "{\"status\":\"needs_attention\",\"setup\":[{\"name\":\"Config\",\"status\":\"fail\",\"detail\":\"schema\",\"next_command\":\"\"}],\"readiness\":[]}");

        vm.ShowReport(report, succeeded: false);

        Assert.Equal("Bad", vm.ReportKey);
        Assert.Equal("Config", Assert.Single(vm.ReportRows).Name);
        Assert.Contains("gateway was not started", vm.ReportText, StringComparison.Ordinal);
    }
}
