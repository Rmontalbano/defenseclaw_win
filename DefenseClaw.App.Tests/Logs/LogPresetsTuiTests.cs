using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-263: the Logs presets held to the 0.8.10 TUI's (<c>tui/panels/logs.py</c> <c>line_matches_current_filter</c>). The TUI's pattern lists are copied below as they are
/// and are the oracle; a row is a log-file line or an event, and every pattern of every list is tried against every preset the way the TUI tries it. Rows are built
/// with <see cref="LogEntry"/> alone, no window and no database.
/// </summary>
public sealed class LogPresetsTuiTests
{
    // IMPORTANT_PATTERNS, NOISE_PATTERNS, ACTIONABLE_LOG_PATTERNS and LOW_SIGNAL_LOG_PATTERNS of defenseclaw/tui/panels/logs.py, verbatim.
    private static readonly string[] TuiImportant =
    {
        "error", "fatal", "panic", "warn", "block", "allow", "reject", "quarantine", "scan", "drift", "verdict", "guardrail", "connected", "disconnected", "started", "stopped",
    };

    private static readonly string[] TuiNoise =
    {
        "event tick seq=", "event health seq=", "payload_len=20", "mallocstacklogging", "event sessions.changed seq=nil", "content-length=0",
    };

    private static readonly string[] TuiActionable =
    {
        "critical", " high ", "severity=high", "severity:high", "error", "fatal", "panic", "warn", "block", "blocked", "deny", "denied", "reject", "rejected", "quarantine",
        "fail", "failed", "failure",
    };

    private static readonly string[] TuiLowSignal =
    {
        " info ", "severity=info", "severity:info", " low ", "severity=low", "severity:low", " medium ", "severity=medium", "severity:medium",
    };

    // The TUI's per-preset words (line_matches_current_filter).
    private static readonly string[] TuiErrors = { "error", "fatal", "panic" };
    private static readonly string[] TuiWarnings = { "error", "fatal", "panic", "warn" };
    private static readonly string[] TuiScan = { "scan", "finding" };
    private static readonly string[] TuiDrift = { "drift", "rescan" };
    private static readonly string[] TuiGuardrail = { "guardrail", "guard" };
    private static readonly string[] TuiHooks = { "hook" };

    public static TheoryData<string> Important() => Data(TuiImportant);

    public static TheoryData<string> Noise() => Data(TuiNoise);

    public static TheoryData<string> Actionable() => Data(TuiActionable);

    public static TheoryData<string> LowSignal() => Data(TuiLowSignal);

    private static TheoryData<string> Data(IEnumerable<string> patterns)
    {
        var data = new TheoryData<string>();
        foreach (var pattern in patterns)
        {
            data.Add(pattern);
        }

        return data;
    }

    /// <summary>A log-file line around <paramref name="pattern"/>, which is how the TUI reads one: as it is.</summary>
    private static LogEntry Line(string pattern) => new(LogLine.Parse("[api] abc" + pattern + "def", 1));

    private static LogEntry Quiet() => new(LogLine.Parse("[api] a plain line", 1));

    /// <summary>An event with this severity as stored and this message, as the Verdicts and Events streams make one.</summary>
    private static LogEntry Event(string severity, string message = "guardrail.evaluated allow — clean", string bucket = "guardrail.evaluation", string eventType = "verdict", string action = "allow")
    {
        var severityLevel = AuditSeverityExtensions.Parse(severity);
        var streamEvent = new StreamEvent(
            "id-1",
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            bucket,
            "guardrail.evaluated",
            "sidecar",
            severityLevel,
            action,
            eventType,
            message,
            "claudecode",
            "gateway",
            "{}",
            PayloadOmitted: false)
        {
            SeverityText = severity,
        };
        return new LogEntry(streamEvent, 0, "verdicts");
    }

    // ---- All and the menu ----

    [Fact]
    public void The_menu_is_the_Macs_and_the_TUIs_nine_presets_in_order()
    {
        Assert.Equal(new[] { "all", "no-noise", "important", "errors", "warnings+", "scan", "drift", "guardrail", "hooks" }, LogPresets.Names);
        Assert.True(LogPresets.Matches("all", Quiet()));
        Assert.True(LogPresets.Matches("something-else", Quiet()));
    }

    // ---- No Noise ----

    [Theory]
    [MemberData(nameof(Noise))]
    public void No_noise_drops_a_heartbeat_line_even_when_it_says_something_actionable(string pattern)
    {
        Assert.False(LogPresets.Matches("no-noise", Line(pattern)));
        Assert.False(LogPresets.Matches("no-noise", new LogEntry(LogLine.Parse("[ws] ERROR blocked " + pattern, 1))));
        Assert.False(LogPresets.Matches("no-noise", Line(pattern), hideLowSignal: false));
    }

    [Theory]
    [MemberData(nameof(Actionable))]
    public void No_noise_keeps_a_line_with_an_actionable_pattern_even_when_it_is_marked_quiet(string pattern)
    {
        var line = new LogEntry(LogLine.Parse("[api] abc info def " + pattern + " ghi", 1));

        Assert.True(LogPresets.Matches("no-noise", line));
        Assert.True(LogPresets.Matches("no-noise", Line(pattern)));
    }

    [Theory]
    [MemberData(nameof(LowSignal))]
    public void No_noise_drops_a_line_with_a_low_signal_pattern_and_nothing_to_say(string pattern)
    {
        Assert.False(LogPresets.Matches("no-noise", Line(pattern)));

        // The Events stream leaves that half to its Actionable-only switch (CUST-262): asked not to, the line stays.
        Assert.True(LogPresets.Matches("no-noise", Line(pattern), hideLowSignal: false));
    }

    [Theory]
    [InlineData("[api] a plain line", true)]
    [InlineData("[sidecar] ready", true)]
    [InlineData("[api] the information was shown", true)]
    [InlineData("[api] level info here", false)]
    [InlineData("[api] severity=low rule matched", false)]
    [InlineData("[api] severity=low but the call failed", true)]
    [InlineData("[api] event sessions.changed seq=nil", false)]
    [InlineData("[api] event sessions.changed seq=7", true)]
    public void No_noise_reads_a_file_line_as_it_is(string raw, bool kept) =>
        Assert.Equal(kept, LogPresets.Matches("no-noise", new LogEntry(LogLine.Parse(raw, 1))));

    // ---- No Noise on an event: the severity is a word of the line the TUI renders ----

    [Theory]
    [InlineData("INFO", false)]
    [InlineData("LOW", false)]
    [InlineData("MEDIUM", false)]
    [InlineData("", false)]          // the TUI renders a row with no severity as INFO
    [InlineData("HIGH", true)]
    [InlineData("CRITICAL", true)]
    [InlineData("ERROR", true)]
    [InlineData("FATAL", true)]
    [InlineData("WARNING", true)]    // "warn" is an actionable word, the severity's among them
    [InlineData("WARN", true)]
    public void No_noise_judges_an_event_by_its_severity_word_the_way_the_TUIs_rendered_line_carries_it(string severity, bool kept)
    {
        var entry = Event(severity);

        Assert.Equal(kept, LogPresets.Matches("no-noise", entry));
        Assert.True(LogPresets.Matches("no-noise", entry, hideLowSignal: false));
    }

    [Theory]
    [InlineData("INFO", "guardrail.evaluated block — matched a rule")]
    [InlineData("LOW", "enforcement.applied — install-blocked")]
    [InlineData("MEDIUM", "sink.checked — connection failed")]
    [InlineData("INFO", "scan.completed — the scan was denied")]
    public void An_event_marked_quiet_is_kept_when_what_it_says_is_actionable(string severity, string message) =>
        Assert.True(LogPresets.Matches("no-noise", Event(severity, message)));

    [Fact]
    public void The_matching_text_of_an_event_has_its_severity_padded_with_spaces_and_its_bucket()
    {
        var text = Event("INFO", "x y").MatchText;

        Assert.Contains(" info ", text, StringComparison.Ordinal);
        Assert.Contains("guardrail.evaluation", text, StringComparison.Ordinal);
        Assert.Contains("x y", text, StringComparison.Ordinal);
        Assert.Equal(text.ToLowerInvariant(), text);
    }

    // ---- Important ----

    [Theory]
    [MemberData(nameof(Important))]
    public void Important_keeps_a_line_with_any_of_the_TUIs_important_words(string pattern)
    {
        Assert.True(LogPresets.Matches("important", Line(pattern)));
        Assert.False(LogPresets.Matches("important", Quiet()));
    }

    [Fact]
    public void Important_is_the_TUIs_list_exactly()
    {
        Assert.Equal(TuiImportant, LogPresets.ImportantKeywords);
    }

    // ---- Errors and Warnings+ ----

    [Theory]
    [InlineData("error")]
    [InlineData("fatal")]
    [InlineData("panic")]
    public void Errors_keeps_the_TUIs_three_words_and_panic_is_one_of_them(string word)
    {
        var line = new LogEntry(LogLine.Parse("[api] the process hit a " + word + " just now", 1));

        Assert.True(LogPresets.Matches("errors", line));
        Assert.True(LogPresets.Matches("warnings+", line));
        Assert.True(line.Severity >= AuditSeverity.High, "a panic reads as an error on the tone bar as well");
    }

    [Fact]
    public void A_panic_is_an_error_not_only_in_the_preset_but_in_the_row()
    {
        var line = new LogEntry(LogLine.Parse("[api] panic: runtime error: index out of range", 1));
        var bare = new LogEntry(LogLine.Parse("[api] panic: nil pointer dereference", 1));

        Assert.Equal(AuditSeverity.High, bare.Severity);
        Assert.Equal("High", bare.Tone);
        Assert.True(LogPresets.Matches("errors", bare));
        Assert.True(LogPresets.Matches("errors", line));
    }

    [Fact]
    public void Errors_leaves_out_a_warning_and_warnings_plus_takes_it()
    {
        var warning = new LogEntry(LogLine.Parse("[api] warn: slow reply", 1));

        Assert.False(LogPresets.Matches("errors", warning));
        Assert.True(LogPresets.Matches("warnings+", warning));
        Assert.False(LogPresets.Matches("warnings+", Quiet()));
    }

    [Fact]
    public void The_preset_words_are_the_TUIs()
    {
        Assert.Equal(TuiErrors, LogPresets.ErrorWords);
        Assert.Equal(TuiWarnings, LogPresets.WarningWords);
        Assert.Equal(TuiScan, LogPresets.ScanWords);
        Assert.Equal(TuiDrift, LogPresets.DriftWords);
        Assert.Equal(TuiHooks, LogPresets.HookWords);

        // Guardrail keeps the Mac's two extra words beside the TUI's two.
        Assert.Equal(TuiGuardrail.Concat(new[] { "verdict", "judge" }), LogPresets.GuardrailWords);
    }

    [Theory]
    [InlineData("[api] upstream returned HTTP 503", true)]
    [InlineData("[api] request refused: http 404", true)]
    [InlineData("[api] GET /health -> http 200", false)]
    public void An_http_status_line_stays_an_error_here_though_the_TUI_reads_only_error_fatal_and_panic(string raw, bool isError)
    {
        var line = new LogEntry(LogLine.Parse(raw, 1));

        // What the TUI would do: its Errors preset is the three words, and "http 503" has none of them.
        Assert.DoesNotContain(TuiErrors, word => raw.Contains(word, StringComparison.OrdinalIgnoreCase));

        // What this app does, on purpose (LogPresets): a 4xx / 5xx status is an error on the tone bar, above a severity floor, and in both presets.
        Assert.Equal(isError, LogPresets.Matches("errors", line));
        Assert.Equal(isError, LogPresets.Matches("warnings+", line));
        Assert.Equal(isError, line.Severity >= AuditSeverity.High);
        Assert.Equal(isError ? "High" : string.Empty, line.Tone);
    }

    [Theory]
    [InlineData("ERROR", true, true)]      // not on the severity ladder, but the word "error" is in the TUI's line
    [InlineData("FATAL", true, true)]
    [InlineData("HIGH", true, true)]       // the Mac's floor: High and above
    [InlineData("CRITICAL", true, true)]
    [InlineData("WARNING", false, true)]   // "warn" in the line
    [InlineData("MEDIUM", false, true)]    // the Mac's floor for Warnings+
    [InlineData("LOW", false, false)]
    [InlineData("INFO", false, false)]
    public void Errors_and_warnings_plus_keep_the_severity_floor_and_add_the_words_the_TUI_reads_in_the_severity_column(string severity, bool errors, bool warnings)
    {
        var entry = Event(severity);

        Assert.Equal(errors, LogPresets.Matches("errors", entry));
        Assert.Equal(warnings, LogPresets.Matches("warnings+", entry));
    }

    [Fact]
    public void An_event_that_says_error_in_its_reason_is_an_error_whatever_its_severity()
    {
        Assert.True(LogPresets.Matches("errors", Event("INFO", "sink.checked — connection error: refused")));
        Assert.True(LogPresets.Matches("errors", Event("LOW", "boot.checked — panic while loading")));
        Assert.False(LogPresets.Matches("errors", Event("INFO", "sink.checked — all good")));
    }

    // ---- Scan, Drift, Guardrail, Hooks ----

    [Theory]
    [InlineData("scan", true)]
    [InlineData("finding", true)]
    [InlineData("a rescan was queued", true)]
    [InlineData("no such topic", false)]
    public void Scan_keeps_scan_and_finding(string text, bool kept) =>
        Assert.Equal(kept, LogPresets.Matches("scan", new LogEntry(LogLine.Parse("[api] " + text, 1))));

    [Theory]
    [InlineData("drift", true)]
    [InlineData("rescan", true)]
    [InlineData("drifting apart", true)]
    [InlineData("scan only", false)]
    public void Drift_keeps_drift_and_rescan(string text, bool kept) =>
        Assert.Equal(kept, LogPresets.Matches("drift", new LogEntry(LogLine.Parse("[api] " + text, 1))));

    [Theory]
    [InlineData("guardrail", true)]
    [InlineData("guard", true)]
    [InlineData("the guard rejected it", true)]
    [InlineData("verdict", true)]
    [InlineData("judge", true)]
    [InlineData("nothing here", false)]
    public void Guardrail_keeps_the_TUIs_words_and_the_Macs(string text, bool kept) =>
        Assert.Equal(kept, LogPresets.Matches("guardrail", new LogEntry(LogLine.Parse("[api] " + text, 1))));

    [Fact]
    public void The_topical_presets_keep_the_lines_with_their_words_and_only_those()
    {
        // One table over Scan, Drift, Guardrail and Hooks: a line is kept by a preset when it contains one of that preset's words (the TUI's, plus the Mac's two for
        // Guardrail), whichever preset's word it was written for - so "rescan" is a Drift word that Scan keeps too, because it contains "scan".
        var table = new (string Preset, string[] Words)[]
        {
            ("scan", TuiScan),
            ("drift", TuiDrift),
            ("guardrail", TuiGuardrail.Concat(new[] { "verdict", "judge" }).ToArray()),
            ("hooks", TuiHooks),
        };

        foreach (var word in table.SelectMany(t => t.Words).Distinct())
        {
            foreach (var (preset, words) in table)
            {
                var mentions = words.Any(w => word.Contains(w, StringComparison.OrdinalIgnoreCase));
                Assert.True(mentions == LogPresets.Matches(preset, Line(word)), $"{preset} on a line saying '{word}'");
            }
        }
    }

    [Fact]
    public void Hooks_keeps_the_hook_calls()
    {
        Assert.True(LogPresets.Matches("hooks", new LogEntry(LogLine.Parse("[api] hook fired", 1))));
        Assert.True(LogPresets.Matches("hooks", Event("INFO", "hook_decision allow — ok", eventType: "hook")));
        Assert.False(LogPresets.Matches("hooks", Quiet()));
    }
}
