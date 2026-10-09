using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-263: the Logs panel's No Noise rule held to the 0.8.10 TUI's (<c>tui/panels/logs.py</c>). The three tables below are copied from that file as they are
/// and are the oracle: <see cref="LogSignalRule"/> must carry the same lists in the same order, and every pattern of every list is tried as the TUI tries it. The
/// Audit / Alerts rule (<see cref="ActionableRule"/>) is a different table of the same shape, and the last tests say how.
/// </summary>
public sealed class LogSignalRuleTests
{
    // NOISE_PATTERNS, ACTIONABLE_LOG_PATTERNS and LOW_SIGNAL_LOG_PATTERNS of defenseclaw/tui/panels/logs.py, verbatim.
    private static readonly string[] TuiNoise =
    {
        "event tick seq=",
        "event health seq=",
        "payload_len=20",
        "mallocstacklogging",
        "event sessions.changed seq=nil",
        "content-length=0",
    };

    private static readonly string[] TuiActionable =
    {
        "critical",
        " high ",
        "severity=high",
        "severity:high",
        "error",
        "fatal",
        "panic",
        "warn",
        "block",
        "blocked",
        "deny",
        "denied",
        "reject",
        "rejected",
        "quarantine",
        "fail",
        "failed",
        "failure",
    };

    private static readonly string[] TuiLowSignal =
    {
        " info ",
        "severity=info",
        "severity:info",
        " low ",
        "severity=low",
        "severity:low",
        " medium ",
        "severity=medium",
        "severity:medium",
    };

    public static TheoryData<string> NoisePatterns() => Data(TuiNoise);

    public static TheoryData<string> ActionablePatterns() => Data(TuiActionable);

    public static TheoryData<string> LowSignalPatterns() => Data(TuiLowSignal);

    private static TheoryData<string> Data(IEnumerable<string> patterns)
    {
        var data = new TheoryData<string>();
        foreach (var pattern in patterns)
        {
            data.Add(pattern);
        }

        return data;
    }

    // ---- The tables ----

    [Fact]
    public void The_three_tables_are_the_TUIs_verbatim_and_in_its_order()
    {
        Assert.Equal(TuiNoise, LogSignalRule.NoisePatterns);
        Assert.Equal(TuiActionable, LogSignalRule.ActionablePatterns);
        Assert.Equal(TuiLowSignal, LogSignalRule.LowSignalPatterns);
    }

    // ---- NOISE_PATTERNS ----

    [Theory]
    [MemberData(nameof(NoisePatterns))]
    public void A_heartbeat_line_is_noise_whatever_else_it_says(string pattern)
    {
        Assert.True(LogSignalRule.IsNoise("12:00:01 [ws] " + pattern + "44"));
        Assert.True(LogSignalRule.IsNoise("12:00:01 [ws] ERROR " + pattern.ToUpperInvariant()));
    }

    [Fact]
    public void A_sessions_changed_event_is_noise_only_without_a_sequence_number()
    {
        // The Mac (and this app until CUST-263) drop every "event sessions.changed"; the TUI drops "... seq=nil" only.
        Assert.True(LogSignalRule.IsNoise("event sessions.changed seq=nil payload_len=3"));
        Assert.False(LogSignalRule.IsNoise("event sessions.changed seq=42 payload_len=3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[api] a real line")]
    [InlineData("event tick without a sequence")]
    public void Other_lines_are_not_noise(string line) => Assert.False(LogSignalRule.IsNoise(line));

    // ---- ACTIONABLE_LOG_PATTERNS: a signal word keeps a line the low-signal markers would drop ----

    [Theory]
    [MemberData(nameof(ActionablePatterns))]
    public void A_signal_word_keeps_a_line_that_is_marked_quiet(string pattern)
    {
        var line = "abc info def " + pattern + " ghi";

        Assert.Contains(" info ", line, StringComparison.Ordinal);
        Assert.True(LogSignalRule.HasActionableSignal(line));
        Assert.True(LogSignalRule.HasActionableSignal(line.ToUpperInvariant()));
    }

    // ---- LOW_SIGNAL_LOG_PATTERNS: a marker alone drops it ----

    [Theory]
    [MemberData(nameof(LowSignalPatterns))]
    public void A_quiet_marker_with_nothing_to_say_drops_the_line(string pattern)
    {
        var line = "abc" + pattern + "def";

        Assert.False(LogSignalRule.HasActionableSignal(line));
        Assert.False(LogSignalRule.HasActionableSignal(line.ToUpperInvariant()));
    }

    // ---- Neither: kept ----

    [Theory]
    [InlineData("")]
    [InlineData("[api] a real line")]
    [InlineData("12:00:01.123 LIFECYCLE GATEWAY READY")]
    [InlineData("the information was shown")]                  // "info" inside a word is not " info "
    [InlineData("a follow-up on the lowest tier")]             // nor is "low" inside one
    [InlineData("severity=none")]
    public void A_line_with_no_marker_and_no_signal_word_is_kept(string line) => Assert.True(LogSignalRule.HasActionableSignal(line));

    [Theory]
    [InlineData("12:00:01.123 VERDICT ALLOW   INFO     input  chat gpt-4o -- clean prompt", false)]
    [InlineData("12:00:01.123 VERDICT BLOCK   HIGH     input  chat gpt-4o -- matched a rule", true)]
    [InlineData("12:00:01.123 VERDICT ALERT   MEDIUM   input  chat gpt-4o -- suspicious", false)]
    [InlineData("12:00:01.123 VERDICT ALERT   MEDIUM   input  chat gpt-4o -- request blocked by policy", true)]
    [InlineData("12:00:01.123 V8 LOW      scan.completed target=skills/example", false)]
    [InlineData("12:00:01.123 V8 WARNING  sink.checked target=-", true)]
    [InlineData("12:00:01.123 V8 CRITICAL boot.checked target=-", true)]
    [InlineData("12:00:01.123 ERROR     SIDECAR    code=- msg=connection refused", true)]
    [InlineData("level=info msg=started severity:info", false)]
    public void A_rendered_line_is_judged_the_way_the_TUI_judges_it(string line, bool kept) =>
        Assert.Equal(kept, LogSignalRule.HasActionableSignal(line.ToLowerInvariant()));

    // ---- Relation to the Audit / Alerts rule ----

    [Fact]
    public void Every_word_of_the_Audit_rule_is_a_signal_word_here_and_the_Logs_list_adds_five()
    {
        Assert.All(ActionableRule.Tokens, token => Assert.Contains(token, LogSignalRule.ActionablePatterns));

        // critical, the padded and tagged "high", and warn: the severity words a log line carries in its text and an audit row carries in its column.
        Assert.Equal(
            new[] { "critical", " high ", "severity=high", "severity:high", "warn" },
            LogSignalRule.ActionablePatterns.Except(ActionableRule.Tokens, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_two_rules_agree_on_the_words_and_differ_on_warnings_and_how_a_row_is_marked_quiet()
    {
        // The same words: a block keeps an INFO row in both.
        Assert.True(ActionableRule.IsActionable("INFO", "install blocked"));
        Assert.True(LogSignalRule.HasActionableSignal(" info     install blocked"));

        // A WARNING row that only says "warn" is low-signal to Audit and Alerts (the level is in the low-signal set, the word is not a token) and signal to Logs.
        Assert.False(ActionableRule.IsActionable("WARNING", "warn about the disk"));
        Assert.True(LogSignalRule.HasActionableSignal(" warning  warn about the disk"));

        // And a level the Audit rule has no opinion on (NONE) is never hidden there; a line says "low" in words and the Logs rule hides it.
        Assert.True(ActionableRule.IsActionable("NONE", "risk is low"));
        Assert.False(LogSignalRule.HasActionableSignal("the risk is low today"));
    }

    // ---- The shared matcher ----

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("nothing to see", false)]
    [InlineData("a BLOCKED call", true)]
    [InlineData("a Failure", true)]
    public void The_shared_matcher_reads_a_list_as_case_insensitive_substrings(string? text, bool expected)
    {
        Assert.Equal(expected, ActionableRule.ContainsAny(text, ActionableRule.Tokens));
        Assert.Equal(expected, ActionableRule.HasToken(text));
    }

    [Fact]
    public void The_shared_matcher_wants_a_list()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ActionableRule.ContainsAny("text", null!));
    }
}
