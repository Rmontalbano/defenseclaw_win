using System.Text.Json;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// CUST-359: what the result card reads from install.ps1's transcript - the reason in one sanitized line, whether the previous install came back,
/// whether the gateway was the failure, how the release bytes were checked - and which transcript is this run's. The lines are install.ps1 1.0.4's
/// own (scripts/install.ps1 at tag 1.0.4); the user is <c>operator</c> and every host is synthetic.
/// </summary>
public sealed class InstallTranscriptTests : IDisposable
{
    private const string Log = @"C:\Users\operator\.defenseclaw\logs\install-20261029T090000.log";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Transcript(string eol, string host, params string[] lines) => TranscriptAt(new DateTime(2026, 10, 29, 9, 0, 0), eol, host, lines);

    private static string TranscriptAt(DateTime localStart, string eol, string host, params string[] lines) =>
        string.Join(eol, new[]
        {
            "**********************",
            "Windows PowerShell transcript start",
            $"Start time: {localStart:yyyyMMddHHmmss}",
            @"Username: SYNTH\operator",
            "Machine: SYNTH-HOST (Microsoft Windows NT 10.0.26300.0)",
            "Host Application: powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\staging\\1.0.4\\install.ps1 -Version 1.0.4" + host,
            "**********************",
            "--- Preparing DefenseClaw 1.0.4 (windows/amd64)  [09:00:01Z]",
            "  > Installed: 1.0.3",
        }.Concat(lines).Append("**********************").Append(string.Empty));

    public static IEnumerable<object[]> Eols() => new[] { new object[] { "\n" }, new object[] { "\r\n" } };

    [Theory]
    [MemberData(nameof(Eols))]
    public void A_gateway_that_did_not_become_healthy_is_the_cause_and_the_restore_is_its_outcome(string eol)
    {
        var facts = InstallTranscript.Parse(Transcript(eol, string.Empty,
            "  + Release signature verified",
            "  + Assets match checksums.txt",
            "  x gateway exited before it answered /health",
            "  x The 1.0.4 gateway did not become healthy within 90 s [09:02:31Z]; restoring 1.0.3",
            "  x DefenseClaw 1.0.4 was not installed. Your previous install is back. Log: " + Log));

        Assert.True(facts.GatewayUnhealthy);
        Assert.True(facts.PreviousRestored);
        Assert.False(facts.RestoreIncomplete);
        Assert.Equal(
            "The 1.0.4 gateway did not become healthy within 90 s [09:02:31Z]; restoring 1.0.3 — DefenseClaw 1.0.4 was not installed. Your previous install is back.",
            facts.Reason);
        Assert.Equal(TranscriptAssetCheck.SignatureVerified, facts.AssetCheck);
        Assert.False(facts.LocalAssets);
    }

    [Theory]
    [MemberData(nameof(Eols))]
    public void An_install_failure_shows_the_line_before_restoring_and_never_the_log_path_or_the_header(string eol)
    {
        var facts = InstallTranscript.Parse(Transcript(eol, string.Empty,
            "  + Assets match checksums.txt",
            @"  x Access to the path 'C:\Users\operator\.defenseclaw\venv\Lib\site-packages\x.pyd' is denied.",
            "  x Installing 1.0.4 failed; restoring 1.0.3",
            "  x DefenseClaw 1.0.4 was not installed. Your previous install is back. Log: " + Log));

        Assert.Equal(
            @"Access to the path '~\.defenseclaw\venv\Lib\site-packages\x.pyd' is denied. — DefenseClaw 1.0.4 was not installed. Your previous install is back.",
            facts.Reason);
        Assert.DoesNotContain("operator", facts.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTH", facts.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Log:", facts.Reason, StringComparison.Ordinal);
        Assert.False(facts.GatewayUnhealthy);
        Assert.True(facts.PreviousRestored);
        Assert.Equal(TranscriptAssetCheck.ChecksumsOnly, facts.AssetCheck);
    }

    [Fact]
    public void A_restore_that_is_not_proven_is_said_so()
    {
        var facts = InstallTranscript.Parse(Transcript("\n", string.Empty,
            "  x Could not put the previous hook back: synthetic",
            "  x DefenseClaw 1.0.4 was not installed, and the previous install is not proven back (see above). Log: " + Log));

        Assert.True(facts.RestoreIncomplete);
        Assert.False(facts.PreviousRestored);
        Assert.DoesNotContain("Log:", facts.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_in_a_line_is_masked_and_an_address_is_cut_to_its_host()
    {
        var facts = InstallTranscript.Parse(Transcript("\n", string.Empty,
            "  x Could not get checksums.txt from https://operator:synthpass@releases.example.test/x?token=s3cr3t-synthetic: 403"));

        Assert.NotNull(facts.Reason);
        Assert.DoesNotContain("synthpass", facts.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-synthetic", facts.Reason, StringComparison.Ordinal);
        Assert.Contains("releases.example.test", facts.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_assets_and_a_gateway_left_stopped_are_read_and_a_success_has_no_reason()
    {
        var facts = InstallTranscript.Parse(Transcript("\r\n", @" -Local C:\builds\dc",
            @"  > Verifying the release assets in C:\builds\dc",
            "  + Assets match checksums.txt",
            "  + Installed DefenseClaw 1.0.4",
            "  ! The gateway is not running, so agent hooks are not guarded until it is"));

        Assert.True(facts.LocalAssets);
        Assert.True(facts.GatewayLeftStopped);
        Assert.Null(facts.Reason);
        Assert.Equal(InstallTranscriptFacts.Empty, InstallTranscript.Parse(null));
        Assert.Equal(InstallTranscriptFacts.Empty, InstallTranscript.Read(Path.Combine(_temp.Path, "missing.log")));
    }

    // ------------------------------------------------------------------ which transcript is this run's

    [Fact]
    public void The_log_named_after_this_runs_start_wins_over_a_newer_log_of_a_later_run()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs")).FullName;
        var started = DateTimeOffset.Now.AddMinutes(-10);
        var ended = started.AddMinutes(4);
        var ours = Path.Combine(logs, $"install-{started.AddSeconds(2):yyyyMMdd'T'HHmmss}.log");
        var later = Path.Combine(logs, $"install-{ended.AddMinutes(3):yyyyMMdd'T'HHmmss}.log");
        File.WriteAllText(ours, Transcript("\n", string.Empty, "  x ours"));
        File.WriteAllText(later, Transcript("\n", string.Empty, "  x a later console run"));
        File.SetLastWriteTimeUtc(ours, ended.UtcDateTime.AddSeconds(-1));
        File.SetLastWriteTimeUtc(later, ended.UtcDateTime.AddMinutes(4));

        Assert.Equal(later, InstallRunReader.FindNewestLog(_temp.Path));
        var found = InstallScriptPlan.FindInstallLog(_temp.Path, started, ended);
        Assert.Equal(ours, found);
        Assert.Equal("ours", InstallScriptPlan.ReadFailureReason(found));
    }

    [Fact]
    public void The_last_install_row_reads_the_apps_own_runs_log_and_a_later_console_run_still_wins()
    {
        using var services = TestServices.Create(_temp);
        var logs = Directory.CreateDirectory(Path.Combine(_temp.Path, "logs")).FullName;
        var run = services.Cli.BeginExternalRun("powershell.exe", new[] { "-NoProfile", "-File", @"C:\staging\install.ps1", "-Version", "1.0.4" }, "note");
        services.Cli.CompleteExternalRun(run, 1, null, null);
        var start = run.StartedAt.ToLocalTime().DateTime;
        var ours = Path.Combine(logs, $"install-{start:yyyyMMdd'T'HHmmss}.log");
        File.WriteAllText(ours, TranscriptAt(start, "\n", string.Empty, "  x Version change to 1.0.4 refused: active connector recovery remains in C:\\Users\\operator\\.defenseclaw."));

        // An older log written later (copied, touched): newest by write time, but not this run's.
        var stale = Path.Combine(logs, "install-20261001T080000.log");
        File.WriteAllText(stale, Transcript("\n", string.Empty, "  x an old reason"));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(1));

        var row = InstallRunReader.Read(_temp.Path, services.Cli.Activity);
        Assert.Equal(ours, row!.LogPath);
        Assert.Contains(@"recovery remains in ~\.defenseclaw", row.FailureReason, StringComparison.Ordinal);

        // A run that started after the app's (from a console) is the newer record.
        var later = DateTime.Now.AddHours(1);
        var console = Path.Combine(logs, $"install-{later:yyyyMMdd'T'HHmmss}.log");
        File.WriteAllText(console, TranscriptAt(later, "\n", string.Empty, "  + Installed DefenseClaw 1.0.4"));
        File.SetLastWriteTimeUtc(console, DateTime.UtcNow.AddMinutes(2));
        Assert.Equal(console, InstallRunReader.Read(_temp.Path, services.Cli.Activity)!.LogPath);
    }

    // ------------------------------------------------------------------ the pre-flight row says what each receipt amounts to (CUST-360)

    private sealed class Probes : IUpgradeProbes
    {
        public IReadOnlyList<string> RunningProcessNames() => Array.Empty<string>();

        public long? FreeDiskBytes(string path) => 100L * 1024 * 1024 * 1024;

        public long DatabaseBytes(string dataDirectory) => 0;
    }

    private PreflightRow PendingRow() =>
        InstallScriptPreflight.Evaluate(new Probes(), null, _temp.Path, PreflightVerification.Verified, "detail")
            .Rows.Single(r => r.Id == InstallScriptPreflight.PendingWritesId);

    private static string Sha(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private string SeedReceipt(string targetContent, bool hashes = true)
    {
        var agent = Directory.CreateDirectory(Path.Combine(_temp.Path, "home", ".claude")).FullName;
        var target = Path.Combine(agent, "settings.json");
        File.WriteAllText(target, targetContent);
        var body = new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["phase"] = "staging",
            ["target_path"] = target,
            ["old_exists"] = true,
            ["remove"] = false,
        };
        if (hashes)
        {
            body["old"] = new Dictionary<string, object?> { ["name"] = "settings.json", ["sha256"] = Sha("old body") };
            body["stage"] = new Dictionary<string, object?> { ["name"] = ".tmp-cas-v2-stage-aa", ["sha256"] = Sha("new body") };
        }

        File.WriteAllText(Path.Combine(_temp.Path, ".defenseclaw-cas-v2-068ff1aa22bb33cc44dd55ee.intent"), JsonSerializer.Serialize(body));
        return target;
    }

    [Theory]
    [InlineData("someone else's body", "settings.json — rewritten since")]
    [InlineData("old body", "settings.json — never applied")]
    [InlineData("new body", "settings.json — finished; only leftovers remain")]
    public void The_pending_write_row_blocks_and_says_what_the_hash_check_found(string live, string expected)
    {
        SeedReceipt(live);

        var row = PendingRow();

        Assert.Equal(PreflightStatus.Block, row.Status);
        Assert.Contains(expected, row.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be verified", row.Reason, StringComparison.Ordinal);
        Assert.Equal(PreflightActionIds.OpenOverview, row.ActionId);
    }

    [Fact]
    public void An_unverifiable_pending_write_still_blocks_and_offers_overview_with_the_caution()
    {
        SeedReceipt("anything", hashes: false);

        var row = PendingRow();

        Assert.Equal(PreflightStatus.Block, row.Status);
        Assert.Contains("could not verify", row.Reason, StringComparison.Ordinal);
        Assert.Contains(CasWriteText.UnverifiedCaution, row.Reason, StringComparison.Ordinal);
        Assert.Equal(PreflightActionIds.OpenOverview, row.ActionId);
    }

    [Fact]
    public void A_recovery_folder_blocks_like_install_ps1s_name_only_check()
    {
        _ = Directory.CreateDirectory(Path.Combine(_temp.Path, ".defenseclaw-cas-v2-archive"));

        var row = PendingRow();

        Assert.Equal(PreflightStatus.Block, row.Status);
        Assert.Contains("1 DefenseClaw recovery file", row.Reason, StringComparison.Ordinal);
        Assert.False(row.HasAction);
    }
}
