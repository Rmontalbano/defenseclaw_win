using DefenseClaw.App.Services;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.PendingRestart;

/// <summary>
/// What leaves a gateway restart pending (CUST-267), as tables: which finished commands queue a line and which do not, which config.yaml saves
/// are changes to the gateway and which are only changes to the file, and how the gateway's start time is read from a snapshot. The queue itself
/// (<see cref="RestartQueueTests"/>) only keeps what these decide.
/// </summary>
public sealed class RestartQueueRulesTests
{
    // Assembled so no source scanner takes these test literals for credentials; none is real.
    private const string OldSecret = "sample-old-" + "secret-value-12345";
    private const string NewSecret = "sample-new-" + "secret-value-67890";

    private static string[] Words(string commandLine) => commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ a finished command that queues

    [Theory]
    [InlineData("guardrail enable --yes --no-restart", "guardrail enable ran with --no-restart")]
    [InlineData("guardrail disable --yes --connector codex --no-restart", "guardrail disable ran with --no-restart")]
    [InlineData("guardrail fail-mode open --yes --no-restart", "guardrail fail-mode ran with --no-restart")]
    [InlineData("guardrail hilt on --yes --connector claudecode --no-restart", "guardrail hilt ran with --no-restart")]
    [InlineData("guardrail block-message --yes --no-restart", "guardrail block-message ran with --no-restart")]
    [InlineData("guardrail judge add codex --enable --no-restart", "guardrail judge ran with --no-restart")]
    [InlineData("setup claudecode --yes --mode observe --no-restart", "setup claudecode ran with --no-restart")]
    [InlineData("setup guardrail --no-restart", "setup guardrail ran with --no-restart")]
    [InlineData("setup a-verb-a-newer-cli-adds --no-restart", "setup a-verb-a-newer-cli-adds ran with --no-restart")]
    [InlineData("agent discovery enable --yes --no-restart --no-scan", "agent discovery ran with --no-restart")]
    [InlineData("agent discovery disable --yes --no-restart", "agent discovery ran with --no-restart")]
    [InlineData("agent discovery runtime enable --yes --no-restart", "agent discovery ran with --no-restart")]
    public void A_successful_change_told_not_to_restart_queues_a_line_that_names_the_command_and_nothing_else(string commandLine, string expected) =>
        Assert.Equal(expected, RestartQueueRules.PendingReasonFor("defenseclaw", Words(commandLine), succeeded: true));

    [Fact]
    public void The_flag_is_found_whatever_its_case()
    {
        Assert.NotNull(RestartQueueRules.PendingReasonFor("defenseclaw", Words("guardrail enable --yes --NO-RESTART"), succeeded: true));
    }

    [Fact]
    public void The_reason_never_carries_a_value_typed_after_the_double_dash()
    {
        var reason = RestartQueueRules.PendingReasonFor(
            "defenseclaw",
            new[] { "guardrail", "block-message", "--yes", "--no-restart", "--", "a message that may say anything" },
            succeeded: true);

        Assert.Equal("guardrail block-message ran with --no-restart", reason);
    }

    [Fact]
    public void A_name_from_outside_is_drawn_with_its_control_characters_spelled_out()
    {
        var reason = RestartQueueRules.PendingReasonFor("defenseclaw", new[] { "setup", "evil‮name", "--yes", "--no-restart" }, succeeded: true);

        Assert.NotNull(reason);
        Assert.DoesNotContain('‮', reason);
        Assert.Contains("\\u202E", reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', reason);
    }

    // ------------------------------------------------------------------ a finished command that does not

    [Theory]
    [InlineData("guardrail enable --yes")]                                // restarts by default: nothing is waiting (and the restart clears the queue)
    [InlineData("setup claudecode --yes --mode observe")]
    [InlineData("agent discovery enable --yes")]
    [InlineData("guardrail hilt on --connector --no-restart")]           // the flag is the value of --connector, not a flag
    [InlineData("guardrail block-message --yes -- --no-restart")]        // after the double dash it is the message
    public void Without_a_standalone_no_restart_nothing_queues(string commandLine) =>
        Assert.Null(RestartQueueRules.PendingReasonFor("defenseclaw", Words(commandLine), succeeded: true));

    [Theory]
    [InlineData("setup claudecode --dry-run --no-restart")]               // a preview writes nothing
    [InlineData("guardrail status --no-restart")]                         // a read
    [InlineData("setup claudecode --help --no-restart")]
    public void A_preview_or_a_read_changed_nothing_so_nothing_queues(string commandLine) =>
        Assert.Null(RestartQueueRules.PendingReasonFor("defenseclaw", Words(commandLine), succeeded: true));

    [Theory]
    [InlineData("setup observability list --json --no-restart")]          // the review's own rule: a list restarts nothing even without the flag
    [InlineData("setup local-observability down --no-restart")]           // plain down rewrites no config.yaml
    [InlineData("setup splunk dashboards plan --no-restart")]             // writes Terraform's files, never config.yaml
    [InlineData("guardrail hilt --no-restart")]                           // nothing to set: a read
    public void A_verb_the_reviews_rule_says_restarts_nothing_even_without_the_flag_has_nothing_waiting(string commandLine) =>
        Assert.Null(RestartQueueRules.PendingReasonFor("defenseclaw", Words(commandLine), succeeded: true));

    [Fact]
    public void A_run_that_did_not_succeed_saved_nothing_that_is_waiting()
    {
        Assert.Null(RestartQueueRules.PendingReasonFor("defenseclaw", Words("guardrail enable --yes --no-restart"), succeeded: false));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("start --no-restart")]
    [InlineData("stop")]
    public void The_gateways_own_verbs_take_no_such_flag_and_queue_nothing(string commandLine) =>
        Assert.Null(RestartQueueRules.PendingReasonFor("defenseclaw-gateway", Words(commandLine), succeeded: true));

    [Fact]
    public void The_rule_is_the_reviews_rule_not_a_second_one()
    {
        // For every verb the review's rule knows (and that takes the flag), "queues" is exactly "would have restarted without the flag", so the two
        // cannot drift. (setup local-observability up restarts through the setup group's callback and takes no such flag: the rule says it restarts
        // with or without one, so the run is a restart to the queue, not a queued line.)
        foreach (var commandLine in new[]
                 {
                     "guardrail enable --yes",
                     "guardrail status",
                     "guardrail hilt",
                     "guardrail hilt on --yes",
                     "setup claudecode --yes",
                     "setup observability list",
                     "setup local-observability down",
                     "setup splunk dashboards plan",
                     "setup codex --dry-run",
                 })
        {
            var argv = Words(commandLine);
            var told = argv.Append("--no-restart").ToArray();

            var restartsAlone = CommandReview.RestartsGatewayFor(argv);
            var queuesWhenTold = RestartQueueRules.PendingReasonFor("defenseclaw", told, succeeded: true) is not null;

            Assert.True(restartsAlone == queuesWhenTold, $"'{commandLine}' restarts={restartsAlone} but --no-restart queues={queuesWhenTold}");
            Assert.False(CommandReview.RestartsGatewayFor(told), commandLine);
        }
    }

    // ------------------------------------------------------------------ a config.yaml save

    [Fact]
    public void A_save_that_changes_no_value_is_not_a_change_to_the_gateway()
    {
        const string before = "gateway:\n  host: 127.0.0.1\n  api_port: 18970\nguardrail:\n  enabled: true\n";

        Assert.Null(RestartQueueRules.ConfigSaveReason(before, before));

        // A comment, blank lines, a different indentation, another order of keys and a flow list instead of a block one are the file, not what it says.
        Assert.Null(RestartQueueRules.ConfigSaveReason(before, "# the gateway\n" + before.Replace("host: 127.0.0.1", "host: 127.0.0.1   # loopback", StringComparison.Ordinal) + "\n\n"));
        Assert.Null(RestartQueueRules.ConfigSaveReason(before, "guardrail:\n    enabled: true\ngateway:\n    api_port: 18970\n    host: 127.0.0.1\n"));
        Assert.Null(RestartQueueRules.ConfigSaveReason("tags:\n- a\n- b\n", "tags: [a, b]\n"));
        Assert.Null(RestartQueueRules.ConfigSaveReason("a: 1\n", "a: 1\r\n"));
    }

    [Theory]
    [InlineData("guardrail:\n  enabled: false\n", "guardrail:\n  enabled: true\n", "config.yaml saved in the config editor (guardrail)")]
    [InlineData("a: 1\n", "a: 1\nllm:\n  model: example-model\n", "config.yaml saved in the config editor (llm)")]                   // a section added
    [InlineData("a: 1\nllm:\n  model: x\n", "a: 1\n", "config.yaml saved in the config editor (llm)")]                                           // and removed
    [InlineData("gateway:\n  host: a\n", "gateway:\n  host: a\n  api_port: 1\n", "config.yaml saved in the config editor (gateway)")]          // a key added inside one
    [InlineData("observability:\n  destinations:\n  - name: a\n", "observability:\n  destinations:\n  - name: b\n", "config.yaml saved in the config editor (observability)")] // a list item
    [InlineData("gateway:\n  api_port: 8080\n", "gateway:\n  api_port: \"8080\"\n", "config.yaml saved in the config editor (gateway)")]       // an int became a string
    public void A_save_that_changes_a_key_or_a_value_queues_a_line_that_names_the_top_level_section(string before, string after, string expected) =>
        Assert.Equal(expected, RestartQueueRules.ConfigSaveReason(before, after));

    [Fact]
    public void Every_section_that_differs_is_named_in_the_order_of_the_new_file_and_a_long_list_is_cut()
    {
        const string before = "a: 1\nb: 1\nc: 1\nd: 1\ne: 1\nf: 1\n";

        Assert.Equal(
            "config.yaml saved in the config editor (b, d)",
            RestartQueueRules.ConfigSaveReason(before, "a: 1\nb: 2\nc: 1\nd: 2\ne: 1\nf: 1\n"));
        Assert.Equal(
            "config.yaml saved in the config editor (a, b, c, d, …)",
            RestartQueueRules.ConfigSaveReason(before, "a: 2\nb: 2\nc: 2\nd: 2\ne: 2\nf: 1\n"));
    }

    [Fact]
    public void A_value_is_never_in_the_reason_and_a_key_that_is_not_a_plain_name_is_not_named()
    {
        var reason = RestartQueueRules.ConfigSaveReason(
            "gateway:\n  token: " + OldSecret + "\n",
            "gateway:\n  token: " + NewSecret + "\n");

        Assert.Equal("config.yaml saved in the config editor (gateway)", reason);
        Assert.DoesNotContain("secret-value", reason, StringComparison.Ordinal);

        Assert.Equal("config.yaml saved in the config editor", RestartQueueRules.ConfigSaveReason("a b: 1\n", "a b: 2\n"));
    }

    [Fact]
    public void A_text_that_does_not_parse_cannot_be_compared_and_is_called_a_change()
    {
        Assert.Equal("config.yaml saved in the config editor", RestartQueueRules.ConfigSaveReason("a: 1\n", "a: [unclosed\n"));
        Assert.Equal("config.yaml saved in the config editor", RestartQueueRules.ConfigSaveReason("a: [unclosed\n", "a: [unclosed\n"));
        Assert.Equal("config.yaml saved in the config editor", RestartQueueRules.ConfigSaveReason("a: 1\na: 2\n", "a: 1\n"));
    }

    [Fact]
    public void An_empty_file_and_a_file_of_comments_are_the_same_and_either_against_a_real_one_is_a_change()
    {
        Assert.Null(RestartQueueRules.ConfigSaveReason(string.Empty, "# nothing here\n"));
        Assert.NotNull(RestartQueueRules.ConfigSaveReason(string.Empty, "a: 1\n"));
        Assert.NotNull(RestartQueueRules.ConfigSaveReason("a: 1\n", string.Empty));
    }

    [Fact]
    public void A_restore_says_so_in_the_same_words()
    {
        Assert.Equal(
            "config.yaml restored from a backup in the config editor (llm)",
            RestartQueueRules.ConfigSaveReason("llm:\n  model: a\n", "llm:\n  model: b\n", RestartQueueRules.ConfigRestoredText));
    }

    // ------------------------------------------------------------------ the gateway's start

    private static readonly DateTimeOffset Polled = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static GatewaySnapshot Snapshot(GatewayHealth? health, DateTimeOffset? polledAt = null) => new() { Health = health, PolledAt = polledAt ?? Polled };

    [Fact]
    public void The_start_is_what_health_reports()
    {
        var started = Polled.AddHours(-3);

        Assert.Equal(started, RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth { StartedAt = started, UptimeMs = 1 })));
    }

    [Fact]
    public void Without_a_start_time_it_is_the_poll_less_the_uptime_health_reports()
    {
        var start = RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth { UptimeMs = 90_000 }));

        Assert.Equal(Polled.AddSeconds(-90), start);
    }

    [Fact]
    public void A_gateway_that_does_not_answer_or_does_not_say_has_no_start()
    {
        Assert.Null(RestartQueueRules.GatewayStart(Snapshot(null)));
        Assert.Null(RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth())));
        Assert.Null(RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth { UptimeMs = 5_000 }, DateTimeOffset.MinValue)));
    }

    [Fact]
    public void A_start_that_cannot_be_true_is_not_a_start()
    {
        // Go's zero time, a start after the poll that saw it (a clock that is wrong, or a gateway that is lying), an uptime of centuries.
        Assert.Null(RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth { StartedAt = DateTimeOffset.MinValue })));
        Assert.Null(RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth { StartedAt = Polled.AddDays(3) })));
        Assert.Null(RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth { UptimeMs = long.MaxValue })));

        // ... but a clock a few seconds out is believed.
        Assert.Equal(Polled.AddSeconds(5), RestartQueueRules.GatewayStart(Snapshot(new GatewayHealth { StartedAt = Polled.AddSeconds(5) })));
    }

    // ------------------------------------------------------------------ text

    [Fact]
    public void A_reason_is_one_trimmed_line_of_at_most_the_longest_length()
    {
        Assert.Equal("a b", RestartQueueRules.Tidy("  a b \n "));
        Assert.Equal("a\\nb", RestartQueueRules.Tidy("a\nb"));
        Assert.Equal(string.Empty, RestartQueueRules.Tidy("   "));
        Assert.Equal(string.Empty, RestartQueueRules.Tidy(null));

        var long200 = RestartQueueRules.Tidy(new string('x', 500));
        Assert.Equal(RestartQueueRules.MaxReasonLength, long200.Length);
        Assert.EndsWith("…", long200, StringComparison.Ordinal);
    }
}
