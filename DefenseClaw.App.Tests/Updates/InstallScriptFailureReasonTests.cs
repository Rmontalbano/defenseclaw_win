using System.Text.Json;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>CUST-359: the pre-flight "Pending config writes" row and the installer's own failure reason. Synthetic data throughout.</summary>
public sealed class InstallScriptFailureReasonTests : IDisposable
{
    private const string Recovery =
        @"Version change to 1.0.3 refused: active connector recovery remains in C:\Users\operator\.defenseclaw. " +
        "Resolve it with the current DefenseClaw version before retrying; do not remove or replay its receipts.";

    private const string Id = "0123456789abcdef01234567";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------ pre-flight row

    private sealed class Probes : IUpgradeProbes
    {
        public Func<string, IReadOnlyList<CasPendingWrite>>? Pending { get; set; }

        public IReadOnlyList<string> RunningProcessNames() => Array.Empty<string>();

        public long? FreeDiskBytes(string path) => 100L * 1024 * 1024 * 1024;

        public long DatabaseBytes(string dataDirectory) => 0;

        public IReadOnlyList<CasPendingWrite> PendingConfigWrites(string dataDirectory) =>
            Pending is null ? CasReceipts.FindPending(dataDirectory, DateTimeOffset.UtcNow, TimeSpan.Zero) : Pending(dataDirectory);
    }

    private PreflightRow PendingRow(Probes probes)
    {
        var report = InstallScriptPreflight.Evaluate(probes, null, _temp.Path, PreflightVerification.Verified, "detail");
        return report.Rows.Single(r => r.Id == InstallScriptPreflight.PendingWritesId);
    }

    private void WriteReceipt(string target)
    {
        var path = _temp.File($".defenseclaw-cas-v2-{Id}.intent");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["phase"] = "staging",
            ["logical_path"] = target,
            ["input_path"] = target,
            ["target_path"] = target,
            ["stage"] = new Dictionary<string, object?> { ["name"] = "config.toml" },
            ["old"] = new Dictionary<string, object?> { ["name"] = "config.toml" },
        }));
    }

    [Fact]
    public void One_pending_set_of_any_age_blocks_and_names_the_file_and_the_fix()
    {
        WriteReceipt(@"C:\synthetic\home\.codex\config.toml");

        var row = PendingRow(new Probes());

        Assert.Equal(PreflightStatus.Block, row.Status);
        Assert.Contains("1 pending config write", row.Reason, StringComparison.Ordinal);
        Assert.Contains("config.toml", row.Reason, StringComparison.Ordinal);
        Assert.Contains("refuses to change version", row.Reason, StringComparison.Ordinal);
        Assert.Contains("Clear the stuck pending write", row.Reason, StringComparison.Ordinal);
        Assert.Equal(PreflightActionIds.OpenOverview, row.ActionId);
    }

    [Fact]
    public void No_receipts_passes_with_no_action()
    {
        var row = PendingRow(new Probes());

        Assert.Equal(PreflightStatus.Pass, row.Status);
        Assert.False(row.HasAction);
    }

    [Fact]
    public void A_probe_that_throws_warns_and_does_not_block()
    {
        var row = PendingRow(new Probes { Pending = _ => throw new IOException("synthetic denied") });

        Assert.Equal(PreflightStatus.Warn, row.Status);
        Assert.Contains("Could not check", row.Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ failure reason

    private static string Transcript(string eol, params string[] failures) =>
        string.Join(eol, new[]
        {
            "**********************",
            "PowerShell transcript start",
            "Start time: 20261021090000",
            @"Username: SYNTH\operator",
            "Machine: SYNTH-HOST (Microsoft Windows NT 10.0.26300.0)",
            "**********************",
            "  > Installed: 1.0.2",
        }.Concat(failures).Append("**********************").Append(string.Empty));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_last_x_line_is_the_reason_and_the_header_is_never_returned(string eol)
    {
        var text = Transcript(eol, "  x an earlier problem", "  x " + Recovery);

        var reason = InstallScriptPlan.ExtractFailureReason(text);

        Assert.Equal(Recovery, reason);
        Assert.DoesNotContain("Username", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH-HOST", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transcript_without_an_x_line_gives_nothing()
    {
        Assert.Null(InstallScriptPlan.ExtractFailureReason(Transcript("\n")));
        Assert.Null(InstallScriptPlan.ExtractFailureReason(null));
        Assert.Null(InstallScriptPlan.ExtractFailureReason(string.Empty));
    }

    [Fact]
    public void A_long_reason_is_capped()
    {
        var reason = InstallScriptPlan.ExtractFailureReason("  x " + new string('q', 2000));

        Assert.NotNull(reason);
        Assert.True(reason!.Length <= InstallScriptPlan.MaxFailureReasonChars);
    }

    [Fact]
    public void The_reason_comes_from_the_log_of_this_run_and_a_missing_log_gives_nothing()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs")).FullName;
        var started = DateTimeOffset.UtcNow;
        var old = Path.Combine(logs, "install-20261020T080000.log");
        var current = Path.Combine(logs, "install-20261021T090000.log");
        File.WriteAllText(old, Transcript("\r\n", "  x the old run's reason"));
        File.WriteAllText(current, Transcript("\r\n", "  x " + Recovery));
        File.SetLastWriteTimeUtc(old, started.UtcDateTime - TimeSpan.FromDays(1));
        File.SetLastWriteTimeUtc(current, started.UtcDateTime + TimeSpan.FromSeconds(21));

        var found = InstallScriptPlan.FindInstallLog(_temp.Path, started);

        Assert.Equal(current, found);
        Assert.Equal(Recovery, InstallScriptPlan.ReadFailureReason(found));
        Assert.Null(InstallScriptPlan.ReadFailureReason(Path.Combine(logs, "install-missing.log")));
        Assert.Null(InstallScriptPlan.ReadFailureReason(null));
    }

    [Fact]
    public void A_log_written_a_few_seconds_before_the_start_stamp_still_counts()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs")).FullName;
        var started = DateTimeOffset.UtcNow;
        var log = Path.Combine(logs, "install-20261021T090000.log");
        File.WriteAllText(log, "x");
        File.SetLastWriteTimeUtc(log, started.UtcDateTime - TimeSpan.FromSeconds(4));

        Assert.Equal(log, InstallScriptPlan.FindInstallLog(_temp.Path, started));
    }

    [Fact]
    public void The_recovery_message_gets_the_overview_hint_and_other_messages_do_not()
    {
        var hint = InstallScriptPlan.HintFor(Recovery);

        Assert.NotNull(hint);
        Assert.Contains("Clear the stuck pending write", hint, StringComparison.Ordinal);
        Assert.Null(InstallScriptPlan.HintFor("something else failed"));
        Assert.Null(InstallScriptPlan.HintFor(null));
    }

    [Fact]
    public void The_last_install_row_carries_the_reason_and_the_hint_for_a_refused_run()
    {
        var run = InstallRunReader.Parse(Transcript("\r\n", "  x " + Recovery), null, DateTimeOffset.UtcNow) with { Outcome = InstallRunOutcome.Failed };

        var text = InstallRunReader.Describe(run, DateTimeOffset.UtcNow);

        Assert.Contains("Installer said: Version change to 1.0.3 refused", text, StringComparison.Ordinal);
        Assert.Contains("Clear the stuck pending write", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Username", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH-HOST", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_install_shows_no_reason()
    {
        var text = InstallRunReader.Describe(
            InstallRunReader.Parse(Transcript("\n") + "+ Installed DefenseClaw 1.0.3.\n", null, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

        Assert.DoesNotContain("Installer said", text, StringComparison.Ordinal);
    }
}
