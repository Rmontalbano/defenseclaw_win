using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// A connector's hook call as the Audit table and inspector read it (CUST-261), the 0.8.10 TUI's readings over <see cref="StructuredDetailParser"/>: the
/// Target cell <c>claudecode · preToolUse</c>, the Details cell <c>allow · 320ms</c>, the inspector's labelled rows with the two noise fields left out and
/// the flags as yes / no. Synthetic details shaped like what the gateway writes.
/// </summary>
public sealed class ConnectorHookDetailTests
{
    private const string Passing =
        "connector=claudecode action=allow severity=NONE mode=observe would_block=false elapsed=320ms";

    // ------------------------------------------------------------------ the table's cells

    [Fact]
    public void A_hook_call_reads_the_connector_and_the_phase_in_its_target_and_the_decision_and_timing_in_its_details()
    {
        Assert.Equal("claudecode · preToolUse", StructuredDetailParser.HookTarget("preToolUse", Passing));
        Assert.Equal("allow · 320ms", StructuredDetailParser.HookSummary(Passing));
        Assert.Equal("claudecode preToolUse", StructuredDetailParser.HookTitle("preToolUse", Passing));
    }

    [Theory]
    [InlineData("connector=codex action=allow severity=NONE elapsed=12ms", "allow · 12ms")]
    [InlineData("connector=codex action=block severity=HIGH elapsed=41ms", "block · HIGH · 41ms")]
    [InlineData("connector=codex action=alert severity=medium", "alert · MEDIUM")]
    [InlineData("connector=codex decision=allow elapsed=1s", "allow · 1s")]
    [InlineData("connector=codex action=allow decision=deny", "allow")]
    [InlineData("connector=codex action=allow severity=none", "allow")]

    // The 0.8.10 gateway writes elapsed_ms, and the TUI's own reading of duration_ms shows a bare number: a bare number reads as milliseconds.
    [InlineData("connector=codex result=ok action=allow raw_action=allow severity=NONE mode=observe would_block=false elapsed_ms=23", "allow · 23ms")]
    [InlineData("connector=codex action=allow duration_ms=7", "allow · 7ms")]
    [InlineData("connector=codex action=allow elapsed_ms=0.5", "allow · 0.5ms")]
    [InlineData("connector=codex action=allow elapsed_ms=fast", "allow · fast")]
    [InlineData("connector=codex action=allow elapsed=9ms elapsed_ms=23", "allow · 9ms")]
    public void The_details_cell_is_the_decision_the_severity_when_it_says_something_and_how_long(string details, string expected) =>
        Assert.Equal(expected, StructuredDetailParser.HookSummary(details));

    [Theory]
    [InlineData("")]
    [InlineData("no pairs here")]
    [InlineData("connector=codex")]
    [InlineData("connector=codex severity=NONE")]
    public void Details_without_a_decision_a_severity_or_a_timing_have_no_summary_and_the_caller_shows_the_details(string details) =>
        Assert.Equal(string.Empty, StructuredDetailParser.HookSummary(details));

    [Theory]
    [InlineData("preToolUse", "connector=codex", null, "codex · preToolUse")]
    [InlineData("preToolUse", "action=allow", "claudecode", "claudecode · preToolUse")]
    [InlineData("preToolUse", "connector=codex", "claudecode", "codex · preToolUse")]
    [InlineData("", "connector=codex", null, "codex")]
    [InlineData(null, "connector=codex", null, "codex")]
    [InlineData("preToolUse", "", null, "preToolUse")]
    [InlineData("preToolUse", null, "", "preToolUse")]
    [InlineData("  postToolUse ", "connector=codex", null, "codex · postToolUse")]
    [InlineData("", "", null, "")]
    public void The_target_cell_falls_back_from_the_details_connector_to_the_rows_own_and_from_both_to_the_phase_alone(string? target, string? details, string? column, string expected) =>
        Assert.Equal(expected, StructuredDetailParser.HookTarget(target, details, column));

    [Theory]
    [InlineData("preToolUse", "connector=codex", null, "codex preToolUse")]
    [InlineData("", "connector=codex", null, "codex hook")]
    [InlineData("preToolUse", "", null, "preToolUse")]
    [InlineData("", "", null, null)]
    public void The_inspector_title_is_the_connector_and_the_phase(string target, string details, string? column, string? expected) =>
        Assert.Equal(expected, StructuredDetailParser.HookTitle(target, details, column));

    [Fact]
    public void Only_the_hook_action_is_a_hook()
    {
        Assert.True(StructuredDetailParser.IsHook("connector-hook"));
        Assert.True(StructuredDetailParser.IsHook("Connector-Hook"));
        Assert.False(StructuredDetailParser.IsHook("hook_decision"));
        Assert.False(StructuredDetailParser.IsHook("connector-hook-tampered"));
        Assert.False(StructuredDetailParser.IsHook(null));
    }

    // ------------------------------------------------------------------ the inspector's rows

    private static string Rows(string details) =>
        string.Join(" | ", StructuredDetailParser.InspectorRows(details).Select(p => p.Label + ": " + p.Value));

    [Fact]
    public void The_inspector_drops_the_two_fields_that_are_noise_on_a_passing_call_and_names_the_rest_the_way_the_TUI_does()
    {
        // severity=NONE and would_block=false while observing are gone; action is the decision, mode the enforcement mode.
        Assert.Equal("Connector: claudecode | Decision: allow | Enforcement mode: observe | Elapsed: 320ms", Rows(Passing));
    }

    [Fact]
    public void The_known_keys_come_first_in_the_TUIs_order_and_the_rest_after_them_as_they_came()
    {
        var details = "zeta=1 elapsed=5ms request_id=r-1 connector=codex alpha=2 tool=Bash action=allow";

        Assert.Equal(
            "Connector: codex | Tool: Bash | Decision: allow | Elapsed: 5ms | Request ID: r-1 | Zeta: 1 | Alpha: 2",
            Rows(details));
    }

    [Theory]
    [InlineData("connector=c mode=observe would_block=false", "Connector: c | Enforcement mode: observe")]
    [InlineData("connector=c mode=observe would_block=true", "Connector: c | Enforcement mode: observe | Would block: yes")]
    [InlineData("connector=c mode=enforce would_block=false", "Connector: c | Enforcement mode: enforce | Would block: no")]
    [InlineData("connector=c would_block=false", "Connector: c | Would block: no")]
    [InlineData("connector=c registry_configured=true registry_status=ok", "Connector: c | Registry configured: yes | Registry status: ok")]
    [InlineData("connector=c severity=HIGH", "Connector: c | Severity (decision): HIGH")]
    [InlineData("connector=c severity=none", "Connector: c")]
    public void A_flag_reads_yes_or_no_and_the_noise_goes_only_where_it_is_noise(string details, string expected) => Assert.Equal(expected, Rows(details));

    [Fact]
    public void A_redaction_reads_as_its_size_and_digest_and_a_quoted_value_loses_its_quotes()
    {
        var details = "connector=codex raw_args=\"ls -la\" raw_payload=<redacted len=8 sha=84ed0c96> reason=\"two words\"";

        Assert.Equal(
            "Connector: codex | Reason: two words | Tool args: ls -la | Raw payload: redacted · 8 bytes · sha:84ed0c96",
            Rows(details));
    }

    [Fact]
    public void The_real_gateways_elapsed_ms_has_its_label_and_a_repeated_key_keeps_its_last_value_where_it_first_was()
    {
        Assert.Equal("Connector: codex | Decision: allow | Elapsed (ms): 23", Rows("connector=codex action=allow elapsed_ms=23"));
        Assert.Equal("Connector: b | Decision: allow", Rows("connector=a action=allow connector=b"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("prose that is not a record")]
    [InlineData("<redacted len=3 sha=abc> action=block")]
    public void Details_that_are_not_a_record_have_no_rows(string details) => Assert.Empty(StructuredDetailParser.InspectorRows(details));

    [Fact]
    public void The_details_as_written_are_there_too_in_order_and_unlabelled()
    {
        var pairs = StructuredDetailParser.RawPairs("a=1 B=\"x y\" c=<redacted len=3 sha=ff> then prose d=4");

        Assert.Equal(
            new[] { "a=1", "B=x y", "c=<redacted len=3 sha=ff>" },
            pairs.Select(p => p.Key + "=" + p.Value));

        var values = StructuredDetailParser.Values("a=1 a=2 b=3");
        Assert.Equal("2", values["a"]);
        Assert.Equal("3", values["b"]);
        Assert.Empty(StructuredDetailParser.Values(null));
    }

    [Fact]
    public void Pairs_is_what_it_was()
    {
        // The labelled reading every other row gets is unchanged by the hook readings.
        var pairs = StructuredDetailParser.Pairs("action=block rule_ids=CMD-ENV-DUMP would_block=false severity=NONE");

        Assert.Equal(
            new[] { ("Action", "block"), ("Rule IDs", "CMD-ENV-DUMP"), ("Would Block", "false"), ("Severity", "NONE") },
            pairs.Select(p => (p.Label, p.Value)).ToArray());
    }

    [Fact]
    public void None_of_it_throws_on_malformed_details()
    {
        foreach (var text in new[] { "a=\"unterminated", "a=<redacted", "a=<<>", "=", "a=", "<", ">", "a='x\\", "k=<redacted len=>", "elapsed_ms=", "connector=" })
        {
            _ = StructuredDetailParser.HookTarget("t", text);
            _ = StructuredDetailParser.HookSummary(text);
            _ = StructuredDetailParser.HookTitle("t", text);
            _ = StructuredDetailParser.InspectorRows(text);
            _ = StructuredDetailParser.RawPairs(text);
            _ = StructuredDetailParser.Values(text);
        }
    }
}
