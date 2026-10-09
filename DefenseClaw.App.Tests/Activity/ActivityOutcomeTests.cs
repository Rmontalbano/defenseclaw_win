using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// What an Activity entry says under its command once it has finished (CUST-264; the TUI's <c>meta_footer</c>): <c>config reloaded</c>,
/// <c>gateway restarted</c>, <c>doctor cache refreshed</c> and the <c>next:</c> hint. Each part is held to the rule the issue states, and to the
/// TUI's own (<c>app.py: _run_command</c>), on invocations built without starting anything.
/// </summary>
public sealed class ActivityOutcomeTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2030, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string Cli = "defenseclaw";
    private const string Gateway = "defenseclaw-gateway";

    /// <summary>A run of <paramref name="executable"/> that started at <see cref="T0"/> and ended three seconds later with <paramref name="exit"/>.</summary>
    private static CliInvocation Finished(string executable, int exit, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv, T0);
        InvocationFactory.Finish(invocation, exit, T0.AddSeconds(3));
        return invocation;
    }

    private static CliInvocation Stopped(string executable, string reason, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv, T0);
        InvocationFactory.Fail(invocation, reason);
        return invocation;
    }

    // ------------------------------------------------------------------ config reloaded

    [Theory]
    [InlineData("setup", "guardrail")]
    [InlineData("setup", "codex", "--yes")]
    [InlineData("guardrail", "enable")]
    [InlineData("guardrail", "hilt", "on", "--yes")]
    [InlineData("settings", "save")]
    [InlineData("init")]
    [InlineData("init", "--non-interactive", "--yes", "--verify")]
    [InlineData("registry", "sync")]
    public void A_successful_setup_guardrail_settings_init_or_registry_command_reloaded_the_config(params string[] argv)
    {
        var outcome = CommandOutcome.Of(Finished(Cli, 0, argv));

        Assert.True(outcome.ConfigReloaded);
        Assert.StartsWith("config reloaded", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("skill", "block", "--", "pdf-tools")]
    [InlineData("doctor")]
    [InlineData("config", "show")]
    [InlineData("alerts", "acknowledge")]
    [InlineData("keys", "list")]
    [InlineData("status")]
    public void Any_other_command_did_not(params string[] argv) =>
        Assert.False(CommandOutcome.Of(Finished(Cli, 0, argv)).ConfigReloaded);

    [Theory]
    [InlineData("setup", "guardrail")]
    [InlineData("init")]
    [InlineData("registry", "sync")]
    public void A_failed_one_reloaded_nothing(params string[] argv)
    {
        var outcome = CommandOutcome.Of(Finished(Cli, 1, argv));

        Assert.False(outcome.ConfigReloaded);
        Assert.False(outcome.GatewayRestarted);
        Assert.DoesNotContain("config reloaded", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("guardrail", "status")]
    [InlineData("registry", "list")]
    [InlineData("registry", "entries")]
    [InlineData("setup", "--help")]
    [InlineData("registry", "sync", "--dry-run")]
    [InlineData("setup", "local-observability", "status")]
    [InlineData("setup", "local-observability", "logs")]
    public void A_command_that_only_reads_changed_nothing_to_reload(params string[] argv)
    {
        // The TUI says "config reloaded" after any exit-0 command whose first word is on its list; a read of that family reloaded nothing.
        var outcome = CommandOutcome.Of(Finished(Cli, 0, argv));

        Assert.False(outcome.ConfigReloaded);
        Assert.False(outcome.GatewayRestarted);
    }

    [Fact]
    public void The_family_is_the_defenseclaw_clis_not_the_gateways()
    {
        Assert.False(CommandOutcome.Of(Finished(Gateway, 0, "setup", "guardrail")).ConfigReloaded);
        Assert.False(CommandOutcome.Of(Finished(Gateway, 0, "init")).ConfigReloaded);
    }

    // ------------------------------------------------------------------ gateway restarted

    [Theory]
    [InlineData("setup", "guardrail")]
    [InlineData("setup", "claude-code", "--yes")]
    [InlineData("guardrail", "enable")]
    [InlineData("guardrail", "disable")]
    [InlineData("guardrail", "judge", "add")]
    [InlineData("setup", "local-observability", "up")]
    public void A_command_the_review_says_restarts_the_gateway_did_so_when_it_exited_zero(params string[] argv)
    {
        Assert.True(CommandReview.RestartsGatewayFor(argv));

        var outcome = CommandOutcome.Of(Finished(Cli, 0, argv));

        Assert.True(outcome.GatewayRestarted);
        Assert.True(outcome.ConfigReloaded);
        Assert.Contains("config reloaded · gateway restarted", outcome.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup", "guardrail", "--no-restart")]
    [InlineData("guardrail", "status")]
    [InlineData("guardrail", "list-packs")]
    [InlineData("settings", "save")]
    [InlineData("init")]
    [InlineData("registry", "sync")]
    [InlineData("skill", "list")]
    [InlineData("setup", "provider", "list")]
    [InlineData("setup", "local-observability", "down")]
    public void A_command_that_does_not_restart_it_did_not(params string[] argv)
    {
        Assert.False(CommandReview.RestartsGatewayFor(argv));
        Assert.False(CommandOutcome.Of(Finished(Cli, 0, argv)).GatewayRestarted);
    }

    [Fact]
    public void The_gateways_own_restart_is_a_restart_that_reread_the_config_and_its_start_and_stop_are_neither()
    {
        var restart = CommandOutcome.Of(Finished(Gateway, 0, "restart"));
        Assert.True(restart.GatewayRestarted);
        Assert.True(restart.ConfigReloaded);
        Assert.Equal("config reloaded · gateway restarted · next: refresh gateway health", restart.Text);

        foreach (var verb in new[] { "start", "stop", "status" })
        {
            var other = CommandOutcome.Of(Finished(Gateway, 0, verb));
            Assert.False(other.GatewayRestarted, verb);
            Assert.False(other.ConfigReloaded, verb);
        }

        Assert.False(CommandOutcome.Of(Finished(Gateway, 1, "restart")).GatewayRestarted);
        Assert.False(CommandOutcome.Of(Finished(Gateway, 0, "watchdog", "start")).GatewayRestarted);
    }

    // ------------------------------------------------------------------ doctor cache refreshed

    [Fact]
    public void A_doctor_that_wrote_the_cache_while_it_ran_refreshed_it()
    {
        var run = Finished(Cli, 0, "doctor");

        Assert.True(CommandOutcome.Of(run, T0.AddSeconds(2).UtcDateTime).DoctorCacheRefreshed);
        Assert.True(CommandOutcome.Of(run, T0.UtcDateTime).DoctorCacheRefreshed);
        Assert.True(CommandOutcome.Of(run, T0.AddSeconds(3).UtcDateTime).DoctorCacheRefreshed);
        Assert.Equal("doctor cache refreshed · next: review readiness", CommandOutcome.Of(run, T0.AddSeconds(2).UtcDateTime).Text);
    }

    [Fact]
    public void A_cache_that_was_not_written_during_the_run_is_not_the_runs()
    {
        var run = Finished(Cli, 0, "doctor");

        Assert.False(CommandOutcome.Of(run, T0.AddSeconds(-1).UtcDateTime).DoctorCacheRefreshed);
        Assert.False(CommandOutcome.Of(run, T0.AddMinutes(-30).UtcDateTime).DoctorCacheRefreshed);

        // After the run's end and a little slack, it is a later doctor's.
        Assert.True(CommandOutcome.Of(run, run.FinishedAt!.Value.UtcDateTime + CommandOutcome.CacheWriteSlack).DoctorCacheRefreshed);
        Assert.False(CommandOutcome.Of(run, run.FinishedAt!.Value.UtcDateTime + CommandOutcome.CacheWriteSlack + TimeSpan.FromSeconds(1)).DoctorCacheRefreshed);

        // No cache at all (never written, or not looked at).
        Assert.False(CommandOutcome.Of(run, null).DoctorCacheRefreshed);
        Assert.False(CommandOutcome.Of(run).DoctorCacheRefreshed);
    }

    [Fact]
    public void Only_a_doctor_run_can_have_refreshed_it()
    {
        var written = T0.AddSeconds(2).UtcDateTime;

        Assert.False(CommandOutcome.Of(Finished(Cli, 0, "skill", "list"), written).DoctorCacheRefreshed);
        Assert.False(CommandOutcome.Of(Finished(Cli, 0, "status"), written).DoctorCacheRefreshed);
        Assert.False(CommandOutcome.Of(Finished(Gateway, 0, "doctor"), written).DoctorCacheRefreshed);
        Assert.False(CommandOutcome.Of(Finished(Cli, 0, "setup", "doctor"), written).DoctorCacheRefreshed);
    }

    [Fact]
    public void A_doctor_that_found_problems_still_rewrote_the_cache_and_says_where_to_go()
    {
        // The doctor writes its cache before it exits, passing or failing (cmd_doctor.py: "so failing runs still update the cache").
        var outcome = CommandOutcome.Of(Finished(Cli, 1, "doctor"), T0.AddSeconds(2).UtcDateTime);

        Assert.True(outcome.DoctorCacheRefreshed);
        Assert.Equal("doctor cache refreshed · next: open readiness or rerun doctor", outcome.Text);
    }

    [Fact]
    public void The_cache_is_a_stat_of_a_file_that_may_not_be_there()
    {
        var path = _temp.File("doctor_cache.json");
        Assert.Null(CommandOutcome.StampOf(path));

        File.WriteAllText(path, "{}");
        var written = new DateTime(2030, 3, 4, 12, 0, 2, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal(written, CommandOutcome.StampOf(path));

        Assert.Null(CommandOutcome.StampOf(_temp.File("no-such-folder\\doctor_cache.json")));
        Assert.Null(CommandOutcome.StampOf("C:\\<>|bad"));
    }

    // ------------------------------------------------------------------ next: ...

    [Theory]
    [InlineData(0, new[] { "doctor" }, "review readiness")]
    [InlineData(1, new[] { "doctor" }, "open readiness or rerun doctor")]
    [InlineData(0, new[] { "keys", "list" }, "rerun readiness")]
    [InlineData(0, new[] { "keys", "check" }, "rerun readiness")]
    [InlineData(2, new[] { "keys", "check" }, "open Credentials or run keys check")]
    [InlineData(0, new[] { "setup", "llm", "--show" }, "rerun readiness")]
    [InlineData(1, new[] { "skill", "list" }, "review output and rerun when fixed")]
    [InlineData(3, new[] { "agent", "discovery", "scan" }, "review output and rerun when fixed")]
    [InlineData(1, new[] { "setup", "guardrail" }, "review output and rerun when fixed")]
    [InlineData(0, new[] { "skill", "list" }, "")]
    [InlineData(0, new[] { "alerts", "acknowledge" }, "")]
    public void The_hint_is_the_TUIs_for_the_command_and_how_it_ended(int exit, string[] argv, string hint)
    {
        var outcome = CommandOutcome.Of(Finished(Cli, exit, argv));

        Assert.Equal(hint, outcome.NextAction);
        if (hint.Length > 0)
        {
            Assert.EndsWith("next: " + hint, outcome.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_finished_doctor_shows_a_next_hint()
    {
        // The issue's acceptance: "Finished doctor shows a next hint".
        var row = new ActivityRow(Finished(Cli, 0, "doctor"));

        Assert.True(row.HasMeta);
        Assert.Equal("next: review readiness", row.MetaText);
    }

    [Fact]
    public void A_failure_says_to_review_the_output_and_rerun_when_fixed()
    {
        var row = new ActivityRow(Finished(Cli, 2, "skill", "scan", "--all"));

        Assert.Equal("next: review output and rerun when fixed", row.MetaText);
    }

    [Theory]
    [InlineData("timed out after 120 s — process tree killed")]
    [InlineData("The system cannot find the file specified.")]
    [InlineData("not started — DefenseClaw for Windows is exiting, so no new commands are launched")]
    public void A_run_that_did_not_end_on_its_own_is_a_failure_with_the_failure_hint_and_no_side_effect(string reason)
    {
        var outcome = CommandOutcome.Of(Stopped(Cli, reason, "setup", "guardrail"));

        Assert.False(outcome.ConfigReloaded);
        Assert.False(outcome.GatewayRestarted);
        Assert.Equal("review output and rerun when fixed", outcome.NextAction);
        Assert.Equal("open readiness or rerun doctor", CommandOutcome.Of(Stopped(Cli, reason, "doctor")).NextAction);
    }

    [Fact]
    public void A_cancel_is_the_operators_own_doing_and_gets_no_hint()
    {
        Assert.Equal(CommandOutcome.None, CommandOutcome.Of(Stopped(Cli, "cancelled — process tree killed", "doctor")));
        Assert.Equal(string.Empty, new ActivityRow(Stopped(Cli, "cancelled — process tree killed", "setup", "guardrail")).MetaText);
    }

    [Fact]
    public void An_entry_that_never_ran_has_nothing_to_say()
    {
        // Refused before it started (CUST-312) and handed to a terminal: the runner records both without starting anything, and neither has an
        // exit code. Then a run still going, and two that are not DefenseClaw commands (the signature check, the installer).
        using var services = TestServices.Create(_temp);
        var refused = services.Cli.RecordRefusal(Cli, new[] { "skill", "block", "--", "x" }, "The list is out of date.");
        var handedOff = services.Cli.RecordHandOff(Cli, new[] { "keys", "set", "OPENAI_API_KEY" }, "Run this in a terminal.");
        var running = InvocationFactory.CreateFor(Cli, new[] { "doctor" }, T0);
        var cosign = Finished(@"C:\Tools\cosign.exe", 1, "verify-blob", "--bundle", "x");
        var installer = Finished(@"C:\Temp\DefenseClaw-Setup.exe", 0, "/S");

        foreach (var entry in new[] { refused, handedOff, running, cosign, installer })
        {
            Assert.Equal(CommandOutcome.None, CommandOutcome.Of(entry));
            Assert.Equal(string.Empty, new ActivityRow(entry).MetaText);
        }

        Assert.Equal(string.Empty, CommandOutcome.None.Text);
        Assert.Null(refused.ExitCode);
        Assert.Null(handedOff.ExitCode);
        Assert.Null(handedOff.FailureReason);
    }

    [Fact]
    public void The_parts_come_in_the_TUIs_order_state_changes_first_and_the_hint_last()
    {
        var all = new CommandOutcome(true, true, true, "rerun readiness");

        Assert.Equal("config reloaded · gateway restarted · doctor cache refreshed · next: rerun readiness", all.Text);
        Assert.Equal("gateway restarted", new CommandOutcome(false, true, false, string.Empty).Text);
        Assert.Equal("next: review readiness", new CommandOutcome(false, false, false, "review readiness").Text);
        Assert.Equal(string.Empty, new CommandOutcome(false, false, false, string.Empty).Text);
    }

    // ------------------------------------------------------------------ the row

    [Fact]
    public void A_row_has_no_outcome_while_it_runs_and_gets_it_once_when_it_finishes()
    {
        var run = InvocationFactory.CreateFor(Cli, new[] { "setup", "guardrail" }, DateTimeOffset.UtcNow);
        var row = new ActivityRow(run);
        Assert.False(row.HasMeta);
        Assert.Equal(string.Empty, row.MetaText);

        InvocationFactory.Finish(run, 0);
        row.Tick();

        Assert.True(row.HasMeta);
        Assert.Equal("config reloaded · gateway restarted · next: rerun readiness", row.MetaText);
    }

    [Fact]
    public void The_row_asks_for_the_doctor_cache_once_and_only_for_a_doctor()
    {
        var asked = 0;
        DateTime? Stamp()
        {
            asked++;
            return T0.AddSeconds(1).UtcDateTime;
        }

        var doctor = new ActivityRow(Finished(Cli, 0, "doctor"), doctorCacheWrittenUtc: Stamp);
        doctor.Tick();
        doctor.Tick();
        Assert.Equal(1, asked);
        Assert.Equal("doctor cache refreshed · next: review readiness", doctor.MetaText);

        _ = new ActivityRow(Finished(Cli, 0, "skill", "list"), doctorCacheWrittenUtc: Stamp);
        _ = new ActivityRow(Finished(Cli, 0, "setup", "guardrail"), doctorCacheWrittenUtc: Stamp);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_later_doctor_does_not_change_what_an_earlier_entry_said()
    {
        DateTime? stamp = T0.AddSeconds(1).UtcDateTime;
        var earlier = new ActivityRow(Finished(Cli, 0, "doctor"), doctorCacheWrittenUtc: () => stamp);
        Assert.Contains("doctor cache refreshed", earlier.MetaText, StringComparison.Ordinal);

        stamp = T0.AddHours(5).UtcDateTime;
        earlier.Tick();

        Assert.Contains("doctor cache refreshed", earlier.MetaText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_with_no_way_to_look_at_the_cache_leaves_that_part_out()
    {
        var row = new ActivityRow(Finished(Cli, 0, "doctor"));

        Assert.Equal("next: review readiness", row.MetaText);
    }
}
