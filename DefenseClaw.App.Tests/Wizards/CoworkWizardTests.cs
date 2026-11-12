using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The curated Claude Cowork wizard (runtime 1.0.8, CUST-445, G8C-02): the exact command, and that a clean exit of it is never shown as success. Nothing
/// runs: the review's step runner and the state read are replaced, the clipboard and Explorer are never touched. Synthetic paths only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class CoworkWizardTests : IDisposable
{
    private const string Zip = @"C:\Users\example\.defenseclaw\cowork\defenseclaw-cowork-plugin.zip";

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private static CoworkProtection Awaiting() => new(
        false,
        "not protected yet: no Cowork event has reached DefenseClaw since plugin 1.0.20261101120000 was built",
        "upload the zip in Claude (Customize > Plugins > Add > Upload plugin), then start a Cowork task",
        CoworkReasonCodes.NoEvents,
        null,
        "1.0.20261101120000",
        Zip);

    private static CoworkProtection Protected() => new(
        true,
        "protected, action mode: policy hits are blocked (plugin 1.0.20261101120000; last Cowork event 2026-11-01 12:30:00Z)",
        string.Empty,
        string.Empty,
        DateTimeOffset.Parse("2026-11-01T12:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
        "1.0.20261101120000",
        Zip);

    private async Task<AppServices> ServicesAsync(string? version = "1.0.8", bool managed = false, string config = "guardrail:\n  enabled: true\n")
    {
        var services = TestServices.Create(_temp, config, version is null ? null : FirstRun.FirstRunFailModeTests.Versioned(version));
        _services.Add(services);
        if (version is not null)
        {
            _ = await services.Runtime.RefreshAsync();
        }

        if (managed)
        {
            services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        }

        return services;
    }

    private static CoworkSetupViewModel Create(AppServices services, params CoworkStateRead[] reads)
    {
        var queue = new Queue<CoworkStateRead>(reads);
        var last = reads.Length > 0 ? reads[^1] : new CoworkStateRead(null, null);
        return UiThread.Run(() => new CoworkSetupViewModel(services, new DiscoverActionReview(services))
        {
            ReadState = _ => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : last),
        });
    }

    private static CliInvocation Run(int exit, string stdout = "", string stderr = "")
    {
        var run = InvocationFactory.Create(false, "setup", "cowork");
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            InvocationFactory.Append(run, line);
        }

        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            InvocationFactory.Append(run, line, CliStream.StandardError);
        }

        InvocationFactory.Finish(run, exit);
        return run;
    }

    /// <summary>Opens the review, confirms it with <paramref name="run"/> as the command's result, and waits for the view-model to finish handling it.</summary>
    private static async Task BuildAsync(CoworkSetupViewModel vm, CliInvocation run, List<IReadOnlyList<string>>? seen = null)
    {
        UiThread.Run(() =>
        {
            vm.Review.RunStep = (_, argv, _) =>
            {
                seen?.Add(argv);
                return Task.FromResult(run);
            };
            vm.BuildPluginCommand.Execute(null);
            Assert.True(vm.Review.IsOpen);
        });

        await UiThread.Run(() => vm.Review.ConfirmCommand.ExecuteAsync(null));
        UiThread.WaitFor(() => UiThread.Run(() => vm.HasResult), "the build result");
    }

    // ------------------------------------------------------------------ the review says Claude Code is turned on too

    [Fact]
    public async Task The_review_warns_a_codex_only_user_that_setup_cowork_also_turns_on_claude_code()
    {
        var services = await ServicesAsync(config: "guardrail:\n  enabled: true\n  connectors:\n    codex:\n      enabled: true\n");
        var vm = Create(services, new CoworkStateRead(null, null));

        UiThread.Run(() => vm.BuildPluginCommand.Execute(null));

        var warnings = UiThread.Run(() => vm.Review.CommandReview!.Warnings);
        var bar = Assert.Single(warnings, w => w.Title == CoworkProtectionFacts.ClaudeCodeToo);
        Assert.Contains("restarts the gateway", bar.Message, StringComparison.Ordinal);
        Assert.Contains("hooks", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_review_has_no_claude_code_bar_when_claude_code_is_already_a_connector()
    {
        var services = await ServicesAsync(config: "guardrail:\n  enabled: true\n  connectors:\n    claudecode:\n      enabled: true\n    codex:\n      enabled: true\n");
        var vm = Create(services, new CoworkStateRead(null, null));

        UiThread.Run(() => vm.BuildPluginCommand.Execute(null));

        var warnings = UiThread.Run(() => vm.Review.CommandReview!.Warnings);
        Assert.DoesNotContain(warnings, w => w.Title == CoworkProtectionFacts.ClaudeCodeToo);
    }

    // ------------------------------------------------------------------ the command and the gate

    [Fact]
    public void The_command_is_exactly_setup_cowork_yes_no_open_claude_no_wait()
    {
        Assert.Equal(new[] { "setup", "cowork", "--yes", "--no-open-claude", "--no-wait" }, CoworkProtectionFacts.SetupArgv);
    }

    [Theory]
    [InlineData("1.0.6", false)]
    [InlineData("1.0.7", false)]
    [InlineData("1.0.8", true)]
    public async Task The_wizard_takes_over_the_cowork_target_only_on_1_0_8(string version, bool expected)
    {
        var services = await ServicesAsync(version);

        Assert.Equal(expected, CoworkWizard.Handles(services, "cowork"));
        Assert.False(CoworkWizard.Handles(services, "claude-code"));
    }

    [Fact]
    public async Task Below_1_0_8_the_store_has_no_state_and_reads_nothing()
    {
        var services = await ServicesAsync("1.0.6");

        var store = CoworkProtectionStore.For(services);
        Assert.Null(await store.RefreshAsync());
        Assert.Null(store.Current);
        Assert.Null(WizardCardStates.For("cowork", WizardGroups.Other, null, store.Current));
    }

    [Fact]
    public async Task The_build_review_runs_the_exact_argv_and_says_what_it_does()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(null, null));
        await UiThread.Run(() => vm.LoadAsync());
        var seen = new List<IReadOnlyList<string>>();

        await BuildAsync(vm, Run(0, "  Built the DefenseClaw Cowork plugin 1.0.20261101120000: " + Zip), seen);

        Assert.Single(seen);
        Assert.Equal(CoworkProtectionFacts.SetupArgv, seen[0]);
        Assert.Equal("Builds the DefenseClaw plugin for Claude Cowork. Sets up Claude Code hooks first if they are off.", vm.BeforeRun);
        Assert.Equal(CoworkProtectionFacts.ClaudeCodeSettingsApply, vm.SettingsNote);
    }

    // ------------------------------------------------------------------ what the badge says

    [Fact]
    public async Task Protected_is_the_only_state_that_says_protected()
    {
        var services = await ServicesAsync();

        var ok = Create(services, new CoworkStateRead(Protected(), null));
        await UiThread.Run(() => ok.LoadAsync());
        Assert.Equal("Protected", ok.BadgeText);
        Assert.Equal("Ok", ok.BadgeKey);
        Assert.StartsWith("Cowork is protected.", ok.ProtectedText, StringComparison.Ordinal);
        Assert.False(ok.ShowUpload);

        var waiting = Create(services, new CoworkStateRead(Awaiting(), null));
        await UiThread.Run(() => waiting.LoadAsync());
        Assert.Equal("Not protected yet", waiting.BadgeText);
        Assert.Equal("Warn", waiting.BadgeKey);
        Assert.Equal(string.Empty, waiting.ProtectedText);
        Assert.StartsWith("Next: upload the zip", waiting.NextText, StringComparison.Ordinal);

        var other = Create(services, new CoworkStateRead(new CoworkProtection(false, "not protected: the Claude Code connector is off", "defenseclaw setup claude-code", CoworkReasonCodes.ClaudeCodeOff, null, null, null), null));
        await UiThread.Run(() => other.LoadAsync());
        Assert.Equal("Not protected", other.BadgeText);
        Assert.Equal("Warn", other.BadgeKey);
    }

    [Fact]
    public async Task A_state_that_could_not_be_read_is_unknown_and_shows_the_problem()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(null, "status --json exited 1"));

        await UiThread.Run(() => vm.LoadAsync());

        Assert.Equal("Unknown", vm.BadgeText);
        Assert.Contains("status --json exited 1", vm.DetailText, StringComparison.Ordinal);
        Assert.False(vm.ShowUpload);
    }

    [Fact]
    public async Task No_cowork_object_is_unknown_and_says_what_that_means_without_alarm()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(null, null));

        await UiThread.Run(() => vm.LoadAsync());

        Assert.Equal("Unknown", vm.BadgeText);
        Assert.Equal("Neutral", vm.BadgeKey);
        Assert.Equal("status --json reported no Cowork state: Claude Desktop was not found for this account and no plugin was built.", vm.DetailText);
        Assert.False(vm.ShowUpload);
        Assert.Equal("Build the plugin", vm.BuildText);
    }

    // ------------------------------------------------------------------ after the command

    [Fact]
    public async Task Exit_0_with_the_plugin_not_yet_uploaded_is_not_protected_and_shows_the_upload_steps_from_the_state()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(null, null), new CoworkStateRead(Awaiting(), null));
        await UiThread.Run(() => vm.LoadAsync());

        await BuildAsync(vm, Run(0, "  Built the DefenseClaw Cowork plugin 1.0.20261101120000: C:\\elsewhere\\not-this.zip"));

        Assert.Equal("Not protected yet", vm.BadgeText);
        Assert.Equal("Warn", vm.BadgeKey);
        Assert.True(vm.ShowUpload);
        Assert.Equal(Zip, vm.ZipPath);
        Assert.Equal(CoworkProtectionFacts.UploadSteps(Zip), vm.UploadSteps);
        Assert.Equal(string.Empty, vm.ZipNote);
        Assert.Equal(string.Empty, vm.ProtectedText);
        Assert.Contains("not protected until you upload it", vm.ResultText, StringComparison.Ordinal);
        Assert.DoesNotContain("completed", vm.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("success", vm.ResultText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Ok", vm.ResultKey);
        Assert.False(vm.Review.IsOpen); // the review's green "Done." about the command is closed: the window's state is the answer
        Assert.Equal("Build the plugin again", vm.BuildText);
        Assert.Contains("remove the older one", vm.RebuildNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_0_when_the_read_has_no_zip_falls_back_to_the_default_path_and_says_so()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(null, null));
        await UiThread.Run(() => vm.LoadAsync());

        await BuildAsync(vm, Run(0, "built"));

        Assert.True(vm.ShowUpload);
        Assert.Equal(CoworkProtectionFacts.PluginZipPath(services.Paths.DataDirectory), vm.ZipPath);
        Assert.Contains("did not give the zip's path", vm.ZipNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_again_turns_protected_only_when_the_cli_says_so()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(Awaiting(), null), new CoworkStateRead(Awaiting(), null), new CoworkStateRead(Protected(), null));
        await UiThread.Run(() => vm.LoadAsync());
        Assert.True(vm.ShowUpload);

        await UiThread.Run(() => vm.CheckAgainCommand.ExecuteAsync(null));
        Assert.Equal("Not protected yet", vm.BadgeText);
        Assert.Equal(string.Empty, vm.ProtectedText);

        await UiThread.Run(() => vm.CheckAgainCommand.ExecuteAsync(null));
        Assert.Equal("Protected", vm.BadgeText);
        Assert.Equal("Ok", vm.BadgeKey);
        Assert.Contains("last Cowork event", vm.ProtectedText, StringComparison.Ordinal);
        Assert.False(vm.ShowUpload);
    }

    [Fact]
    public async Task Exit_3_shows_the_managed_host_message_and_stays_not_protected()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(null, null));
        await UiThread.Run(() => vm.LoadAsync());
        const string Message = "This computer's DefenseClaw is managed by your organization (managed_enterprise), so per-user Cowork setup is disabled: Cowork is covered by the Claude Code machine policy your administrator deploys. Nothing was changed.";

        await BuildAsync(vm, Run(3, stderr: Message));

        Assert.Equal(Message, vm.ResultText);
        Assert.Equal("Not protected", vm.BadgeText);
        Assert.False(vm.ShowUpload);
    }

    [Fact]
    public async Task Another_non_zero_exit_says_setup_cowork_failed_with_the_clis_error_line()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(Awaiting(), null));
        await UiThread.Run(() => vm.LoadAsync());

        await BuildAsync(vm, Run(1, stderr: "Error: defenseclaw-gateway was not found, so the Cowork plugin cannot be built."));

        Assert.StartsWith("setup cowork failed (exit 1)", vm.ResultText, StringComparison.Ordinal);
        Assert.Contains("defenseclaw-gateway was not found", vm.ResultText, StringComparison.Ordinal);
        Assert.Equal("Bad", vm.ResultKey);
        Assert.NotEqual("Protected", vm.BadgeText);
    }

    // ------------------------------------------------------------------ copy, open, managed

    [Fact]
    public async Task Copy_path_and_open_folder_use_the_zip_from_the_state()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(Awaiting(), null));
        string? copied = null;
        string? revealed = null;
        vm.CopyText = text => copied = text;
        vm.RevealFile = path => revealed = path;
        await UiThread.Run(() => vm.LoadAsync());

        UiThread.Run(() =>
        {
            vm.CopyPathCommand.Execute(null);
            vm.OpenFolderCommand.Execute(null);
        });

        Assert.Equal(Zip, copied);
        Assert.Equal(Zip, revealed);
    }

    [Fact]
    public async Task A_managed_installation_disables_the_build_button_with_a_reason()
    {
        var services = await ServicesAsync(managed: true);
        var vm = Create(services, new CoworkStateRead(null, null));
        await UiThread.Run(() => vm.LoadAsync());

        Assert.False(vm.CanBuild);
        Assert.True(vm.HasBlock);
        Assert.Contains("managed by your organization", vm.BlockedReason, StringComparison.Ordinal);
        Assert.False(UiThread.Run(() => vm.BuildPluginCommand.CanExecute(null)));
    }

    [Fact]
    public async Task A_per_user_installation_can_build()
    {
        var services = await ServicesAsync();
        var vm = Create(services, new CoworkStateRead(null, null));
        await UiThread.Run(() => vm.LoadAsync());

        Assert.True(vm.CanBuild);
        Assert.Null(vm.BlockedReason);
    }

    // ------------------------------------------------------------------ the Setup card line

    [Theory]
    [InlineData(true, "", "Protected")]
    [InlineData(false, CoworkReasonCodes.NoEvents, "Built, not uploaded yet")]
    [InlineData(false, CoworkReasonCodes.PluginNotBuilt, "Not set up")]
    [InlineData(false, CoworkReasonCodes.ClaudeCodeOff, "Not protected")]
    public void The_card_line_follows_the_clis_state(bool isProtected, string code, string expected)
    {
        var state = new CoworkProtection(isProtected, "the CLI's sentence", string.Empty, code, null, null, null);

        var line = WizardCardStates.For("cowork", WizardGroups.Other, null, state);

        Assert.NotNull(line);
        Assert.Equal(expected, line.Text);
        // A Cowork that was never set up carries the label in its detail too (CUST-444): "Not set up: <the CLI's sentence>".
        Assert.Equal(code == CoworkReasonCodes.PluginNotBuilt ? "Not set up: the CLI's sentence" : "the CLI's sentence", line.Detail);
        Assert.Null(WizardCardStates.For("cowork", WizardGroups.Other, null, null));
    }
}
