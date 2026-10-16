using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Why the gateway's latest start ended (CUST-352), read from synthetic gateway.log text modelled on the two 2026-10-15 incidents. Nothing here reads
/// a real log: every line is made up, every path is under a made-up user.
/// </summary>
public sealed class GatewayLogClassifierTests : IDisposable
{
    private const string OldExe = @"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\0123456789abcdef\codex.exe";
    private const string CodexToml = @"C:\Users\tester\.codex\config.toml";

    private readonly TempDirectory _temp = new("dcw-exit-reason");

    public void Dispose() => _temp.Dispose();

    private static string Listening(int second = 0) => $"2026-10-15T21:18:{second:00}Z [sidecar-api] listening on 127.0.0.1:18970";

    private static string StaleFailure(int second = 1) =>
        $"2026-10-15T21:18:{second:00}Z [guardrail] connector codex setup failed: connector codex setup failed: inspect effective Codex managed requirements: " +
        $"inspect selected Codex executable: GetFileAttributesEx {OldExe}: The system cannot find the path specified.";

    private static string StuckFailure(int second = 1) =>
        $"2026-10-15T21:18:{second:00}Z [guardrail] connector codex setup failed: connector codex setup failed: codex config.toml patch: write codex config: " +
        $"requested recovery leaf is occupied by an unrecognized inode: {CodexToml}";

    private static string Exited(int second = 2) =>
        $"2026-10-15T21:18:{second:00}Z [sidecar] guardrail exited with error: connector codex setup failed";

    [Fact]
    public void A_missing_agent_executable_is_the_stale_executable_with_its_path()
    {
        var reason = GatewayLogClassifier.Classify(new[] { Listening(), StaleFailure(), Exited() });

        var stale = Assert.IsType<GatewayExitReason.StaleAgentExecutable>(reason);
        Assert.Equal("codex", stale.Connector);
        Assert.Equal(OldExe, stale.Path);
        Assert.Equal("Codex's executable moved after a Codex update.", stale.Describe());
    }

    [Fact]
    public void An_unrecognized_inode_is_a_stuck_config_write_with_the_file_it_names()
    {
        var reason = GatewayLogClassifier.Classify(new[] { Listening(), StuckFailure(), Exited() });

        var stuck = Assert.IsType<GatewayExitReason.StuckConfigWrite>(reason);
        Assert.Equal("codex", stuck.Connector);
        Assert.Equal(CodexToml, stuck.Path);
    }

    [Fact]
    public void Another_connector_setup_failure_keeps_the_gateways_own_words()
    {
        var reason = GatewayLogClassifier.Classify(new[]
        {
            Listening(),
            "2026-10-15T21:18:01Z [guardrail] connector cursor setup failed: connector cursor setup failed: hooks.json is not valid JSON",
        });

        var failed = Assert.IsType<GatewayExitReason.ConnectorSetupFailed>(reason);
        Assert.Equal("cursor", failed.Connector);
        Assert.Contains("hooks.json is not valid JSON", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_that_is_not_a_connector_is_other_with_its_line()
    {
        var reason = GatewayLogClassifier.Classify(new[]
        {
            Listening(),
            "2026-10-15T21:18:02Z [sidecar] guardrail exited with error: listen tcp 127.0.0.1:4000: bind: address already in use",
        });

        var other = Assert.IsType<GatewayExitReason.Other>(reason);
        Assert.Contains("address already in use", other.Line, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-10-15", other.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_that_reached_listening_with_nothing_after_it_has_no_failure()
    {
        Assert.Null(GatewayLogClassifier.Classify(new[]
        {
            Listening(),
            "2026-10-15T21:18:01Z [guardrail] direct-upstream mode: codex policy_mode=action enforcement=true",
        }));
    }

    [Fact]
    public void A_success_after_a_failure_is_no_failure_and_a_failure_after_a_success_is()
    {
        var lines = new List<string> { Listening(0), StaleFailure(1), Exited(2), Listening(40) };
        Assert.Null(GatewayLogClassifier.Classify(lines));

        lines.Add(StuckFailure(41));
        Assert.IsType<GatewayExitReason.StuckConfigWrite>(GatewayLogClassifier.Classify(lines));
    }

    [Fact]
    public void Only_the_latest_attempts_failure_counts_when_the_cause_changed()
    {
        var reason = GatewayLogClassifier.Classify(new[] { Listening(0), StaleFailure(1), Exited(2), Listening(20), StuckFailure(21), Exited(22) });

        Assert.IsType<GatewayExitReason.StuckConfigWrite>(reason);
    }

    [Fact]
    public void A_failure_with_no_listening_line_in_the_window_is_still_a_failure()
    {
        Assert.IsType<GatewayExitReason.StaleAgentExecutable>(GatewayLogClassifier.Classify(new[] { StaleFailure() }));
    }

    [Fact]
    public void Garbage_and_empty_logs_say_nothing()
    {
        Assert.Null(GatewayLogClassifier.Classify(Array.Empty<string>()));
        Assert.Null(GatewayLogClassifier.Classify(new[] { "", "\u0000\u0001garbage", "{not json", "just words", new string('x', 100) }));
    }

    [Fact]
    public void Only_the_last_two_hundred_lines_are_classified()
    {
        var lines = new List<string> { Listening(), StaleFailure() };
        lines.AddRange(Enumerable.Repeat("2026-10-15T21:19:00Z [watcher] scan done", GatewayLogClassifier.ClassifiedLines));

        Assert.Null(GatewayLogClassifier.Classify(lines));
    }

    [Fact]
    public void A_huge_line_keeps_its_head_and_its_tail_so_the_path_at_the_end_survives()
    {
        var padding = new string('a', 100_000);
        var huge = $"2026-10-15T21:18:01Z [guardrail] connector codex setup failed: {padding} requested recovery leaf is occupied by an unrecognized inode: {CodexToml}";
        _ = _temp.Write("gateway.log", Listening() + "\n" + huge + "\n");

        var tail = GatewayLogClassifier.ReadTail(_temp.File("gateway.log"));

        Assert.All(tail, line => Assert.True(line.Length <= GatewayLogClassifier.MaxLineChars + 3));
        var stuck = GatewayLogClassifier.Classify(tail);
        var found = Assert.IsType<GatewayExitReason.StuckConfigWrite>(stuck);
        Assert.EndsWith("config.toml", found.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_far_larger_than_the_window_is_bounded_and_never_a_fault()
    {
        _ = _temp.Write("gateway.log", Listening() + Environment.NewLine + new string('q', 5_000_000) + Environment.NewLine);

        var tail = GatewayLogClassifier.ReadTail(_temp.File("gateway.log"));

        Assert.All(tail, line => Assert.True(line.Length <= GatewayLogClassifier.MaxLineChars + 3));
        Assert.Null(GatewayLogClassifier.Classify(tail));
    }

    [Fact]
    public void A_huge_log_is_read_from_its_end_only_and_a_line_cut_in_half_is_dropped()
    {
        var filler = string.Join('\n', Enumerable.Range(0, 30_000).Select(i => $"2026-10-15T20:00:00Z [watcher] line {i} " + new string('z', 40)));
        _ = _temp.Write("gateway.log", filler + "\n" + Listening() + "\n" + StaleFailure() + "\n");

        var tail = GatewayLogClassifier.ReadTail(_temp.File("gateway.log"));

        Assert.True(tail.Count <= GatewayLogClassifier.KeptLines);
        Assert.StartsWith("2026-", tail[0], StringComparison.Ordinal);
        Assert.IsType<GatewayExitReason.StaleAgentExecutable>(GatewayLogClassifier.Classify(tail));
    }

    [Fact]
    public void A_log_that_the_gateway_still_has_open_is_read_without_getting_in_its_way()
    {
        var path = _temp.File("gateway.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(Listening() + "\n" + StuckFailure() + "\n");
        writer.Write(bytes);
        writer.Flush();

        Assert.IsType<GatewayExitReason.StuckConfigWrite>(GatewayLogClassifier.ClassifyFile(path));
    }

    [Fact]
    public void A_missing_log_is_no_evidence_not_an_error()
    {
        Assert.Empty(GatewayLogClassifier.ReadTail(_temp.File("nope.log")));
        Assert.Null(GatewayLogClassifier.ClassifyFile(_temp.File("nope.log")));
    }

    [Fact]
    public void The_policy_mode_is_the_last_one_logged_for_the_connector()
    {
        var lines = new[]
        {
            "2026-10-15T20:00:00Z [guardrail] direct-upstream mode: codex policy_mode=observe enforcement=false",
            "2026-10-15T20:00:00Z [guardrail] direct-upstream mode: claudecode policy_mode=action enforcement=true",
            "2026-10-15T21:00:00Z [guardrail] direct-upstream mode: codex policy_mode=action enforcement=true",
        };

        Assert.Equal("action", GatewayLogClassifier.LatestPolicyMode(lines, "codex"));
        Assert.Equal("action", GatewayLogClassifier.LatestPolicyMode(lines, "claudecode"));
        Assert.Null(GatewayLogClassifier.LatestPolicyMode(lines, "cursor"));
    }

    [Fact]
    public void Secrets_and_control_characters_in_a_failure_line_are_not_carried_into_what_is_shown()
    {
        // Built at run time so the fake value never appears in the source (secret scanners would flag a literal).
        var fakeToken = string.Concat(Enumerable.Repeat("ab12", 8));
        var reason = GatewayLogClassifier.Classify(new[]
        {
            Listening(),
            $"2026-10-15T21:18:01Z [guardrail] connector codex setup failed: bad value token={fakeToken} \u202E end",
        });

        var failed = Assert.IsType<GatewayExitReason.ConnectorSetupFailed>(reason);
        Assert.DoesNotContain(fakeToken, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', failed.Message);
    }
}
