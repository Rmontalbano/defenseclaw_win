using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-263: the default No Noise preset through the panel - the TUI's whole rule on the log files and on Verdicts, the heartbeat half only on Events (whose quiet rows
/// belong to the "Actionable only" switch of CUST-262) - so that the wiring of <c>LogPresets.Matches(..., hideLowSignal)</c> is held as well as the rule itself.
/// </summary>
public sealed class LogsNoNoisePanelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public LogsNoNoisePanelTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private LogsPanelViewModel ActivePanel()
    {
        var panel = new LogsPanelViewModel(_services);
        panel.SetActive(true);
        return panel;
    }

    private string Database(Action<LogsInspectorFieldsTests.AddEvent> seed)
    {
        var path = _temp.File("nonoise-audit.db");
        LogsInspectorFieldsTests.Fill(path, seed);
        return path;
    }

    private static string[] Ids(LogsPanelViewModel panel) => panel.DisplayedLines.Select(l => l.Fields.Single(f => f.Name == "ID").Value).ToArray();

    /// <summary>Two quiet rows (an INFO allow, a MEDIUM scan), one with an actionable word at a low severity, a HIGH, and an ERROR the severity ladder does not have.</summary>
    private static void Mixed(LogsInspectorFieldsTests.AddEvent add)
    {
        add("q-allow", 1, "guardrail.evaluation", "guardrail.evaluated", "INFO", "act", null, """{"defenseclaw.guardrail.decision":"allow"}""", null, null);
        add("q-scan", 2, "asset.scan", "scan.completed", "MEDIUM", "act", "scan done", null, null, null);
        add("loud-word", 3, "enforcement.action", "enforcement.applied", "INFO", "act", "install-blocked", null, null, null);
        add("loud-high", 4, "guardrail.evaluation", "guardrail.evaluated", "HIGH", "act", "matched a rule", null, null, null);
        add("loud-error", 5, "platform.health", "sink.checked", "ERROR", "act", "sink unreachable", null, null, null);
    }

    // ---- The log files ----

    [Fact]
    public void A_log_file_drops_the_heartbeats_and_the_lines_marked_quiet_and_keeps_the_rest()
    {
        var panel = ActivePanel();

        panel.AcceptLines("Gateway", new[]
        {
            LogLine.Parse("[ws] event tick seq=1", 1),
            LogLine.Parse("[api] level info here", 2),
            LogLine.Parse("[api] severity=low rule matched", 3),
            LogLine.Parse("[api] a plain line", 4),
            LogLine.Parse("[api] level info but the call failed", 5),
            LogLine.Parse("[api] panic: nil pointer", 6),
        });

        Assert.Equal(new[] { "a plain line", "level info but the call failed", "panic: nil pointer" }, panel.DisplayedLines.Select(l => l.Message).ToArray());
        Assert.Equal("3 shown · 3 matching · 6 total", panel.StatusLineCount);

        panel.SelectedPreset = LogPresets.All;
        Assert.Equal(6, panel.DisplayedLines.Count);
    }

    // ---- Verdicts ----

    private async Task<LogsPanelViewModel> LoadedAsync(string source, string path)
    {
        var panel = ActivePanel();
        panel.StreamReader = new EventStreamReader(path);
        panel.ActiveSource = source;
        await panel.LoadStructuredAsync();
        return panel;
    }

    [Fact]
    public async Task Verdicts_open_without_the_quiet_rows_as_the_TUIs_do_and_all_shows_them()
    {
        var panel = await LoadedAsync("Verdicts", Database(Mixed));

        Assert.Equal(LogPresets.NoNoise, panel.SelectedPreset);
        Assert.Equal(new[] { "loud-word", "loud-high", "loud-error" }, Ids(panel));

        panel.SelectedPreset = LogPresets.All;
        Assert.Equal(new[] { "q-allow", "q-scan", "loud-word", "loud-high", "loud-error" }, Ids(panel));
    }

    [Fact]
    public async Task A_verdicts_view_that_No_Noise_empties_says_so_and_names_the_preset()
    {
        var panel = await LoadedAsync(
            "Verdicts",
            Database(add => add("q-allow", 1, "guardrail.evaluation", "guardrail.evaluated", "INFO", "act", null, """{"defenseclaw.guardrail.decision":"allow"}""", null, null)));

        Assert.True(panel.IsEmpty);
        Assert.Equal("No log lines", panel.EmptyTitle);
        Assert.Contains("no-noise", panel.EmptyDetail, StringComparison.Ordinal);
        Assert.Contains("preset \"all\"", panel.EmptyDetail, StringComparison.Ordinal);

        panel.SelectedPreset = LogPresets.All;
        Assert.False(panel.IsEmpty);
    }

    // ---- Events ----

    [Fact]
    public async Task Events_keep_the_quiet_rows_when_the_switch_is_off_because_the_preset_leaves_them_to_it()
    {
        var panel = await LoadedAsync("Events", Database(Mixed));

        // On (the default): the Audit / Alerts rule narrows the stream.
        Assert.True(panel.ActionableOnly);
        Assert.Equal(new[] { "loud-word", "loud-high", "loud-error" }, Ids(panel));

        // Off: every event, though the preset is still No Noise - which, here, drops the heartbeat lines only.
        panel.ActionableOnly = false;
        Assert.Equal(LogPresets.NoNoise, panel.SelectedPreset);
        Assert.Equal(new[] { "q-allow", "q-scan", "loud-word", "loud-high", "loud-error" }, Ids(panel));
    }

    [Fact]
    public async Task An_event_that_is_a_heartbeat_is_dropped_on_every_stream()
    {
        var path = Database(add =>
        {
            add("tick", 1, "platform.health", "event tick seq=3", "HIGH", "act", "event tick seq=3 payload_len=20", null, null, null);
            add("real", 2, "platform.health", "sink.checked", "HIGH", "act", "sink unreachable", null, null, null);
        });

        var verdicts = await LoadedAsync("Verdicts", path);
        Assert.Equal(new[] { "real" }, Ids(verdicts));

        var events = await LoadedAsync("Events", path);
        Assert.Equal(new[] { "real" }, Ids(events));
    }
}
