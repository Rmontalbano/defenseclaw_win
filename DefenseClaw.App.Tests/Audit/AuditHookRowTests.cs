using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-261: a connector's hook call in the Audit table and inspector. Its row says only <c>connector-hook</c> and the phase, so the Target cell reads
/// <c>claudecode · preToolUse</c>, the Details cell <c>allow · 320ms</c> (the decision, the severity when it says something, how long), the inspector is
/// titled for the call and lays out its details the way the TUI does - and every other row is exactly what it was. Synthetic rows from the real DDL.
/// </summary>
public sealed class AuditHookRowTests : IDisposable
{
    private const string Passing = "connector=claudecode action=allow severity=NONE mode=observe would_block=false elapsed=320ms";

    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private string DbPath => Path.Combine(_temp.Path, "audit.db");

    private async Task<AuditPanelViewModel> OpenAsync(Action seed)
    {
        AuditTestDatabase.Create(DbPath, 0, newest: _clock.GetUtcNow());
        seed();
        _services = TestServices.Create(_temp);
        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = false };
        await panel.InitializeAsync();
        return panel;
    }

    private void Add(string id, double secondsAgo, string action, string target, string? details, string? connector = "claudecode", string severity = "INFO") =>
        CorrelatedRows.Add(
            DbPath, id, _clock.GetUtcNow().AddSeconds(-secondsAgo), action, severity, connector, "guardrail.evaluation", "evt", target, "gateway", details);

    // ------------------------------------------------------------------ the acceptance

    [Fact]
    public async Task A_hook_call_reads_claudecode_preToolUse_and_allow_320ms()
    {
        var panel = await OpenAsync(() => Add("hook-1", 1, "connector-hook", "preToolUse", Passing));
        var row = panel.Rows.Single();

        Assert.True(row.IsHook);
        Assert.Equal("claudecode · preToolUse", row.TargetText);
        Assert.Equal("allow · 320ms", row.DetailsText);
        Assert.Equal("claudecode preToolUse", row.Title);

        // The row itself is not changed: its target is the phase, its summary the details - what a copy, a sort-by-column or a screen reader of the
        // underlying event would name.
        Assert.Equal("preToolUse", row.Target);
        Assert.Equal(Passing, row.Summary);
        Assert.Contains(Passing, row.CopyLine, StringComparison.Ordinal);
        Assert.Contains("allow · 320ms", row.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inspector_lays_out_the_details_like_the_TUI_without_the_noise()
    {
        var panel = await OpenAsync(() => Add("hook-1", 1, "connector-hook", "preToolUse", Passing));
        var row = panel.Rows.Single();

        Assert.True(row.HasDetailPairs);
        Assert.Equal(
            new[] { ("Connector", "claudecode"), ("Decision", "allow"), ("Enforcement mode", "observe"), ("Elapsed", "320ms") },
            row.DetailPairs.Select(p => (p.Label, p.Value)).ToArray());
    }

    [Fact]
    public async Task A_decision_that_says_something_and_the_real_gateways_elapsed_ms_read_as_they_should()
    {
        var panel = await OpenAsync(
            () =>
            {
                Add("hook-block", 1, "connector-hook", "preToolUse", "connector=codex result=ok action=block raw_action=block severity=HIGH mode=enforce would_block=true elapsed_ms=41", "codex", "HIGH");
                Add("hook-quiet", 2, "connector-hook", "postToolUse", "connector=codex result=ok action=allow raw_action=allow severity=NONE mode=observe would_block=false elapsed_ms=23", "codex");
            });

        var block = panel.Rows.Single(r => r.Id == "hook-block");
        Assert.Equal("block · HIGH · 41ms", block.DetailsText);
        Assert.Equal("codex · preToolUse", block.TargetText);
        Assert.Contains(("Severity (decision)", "HIGH"), block.DetailPairs.Select(p => (p.Label, p.Value)));
        Assert.Contains(("Would block", "yes"), block.DetailPairs.Select(p => (p.Label, p.Value)));
        Assert.Contains(("Elapsed (ms)", "41"), block.DetailPairs.Select(p => (p.Label, p.Value)));

        var quiet = panel.Rows.Single(r => r.Id == "hook-quiet");
        Assert.Equal("allow · 23ms", quiet.DetailsText);
        Assert.DoesNotContain(quiet.DetailPairs, p => p.Label.StartsWith("Would block", StringComparison.Ordinal) || p.Label.StartsWith("Severity", StringComparison.Ordinal));
        Assert.Contains(("Result", "ok"), quiet.DetailPairs.Select(p => (p.Label, p.Value)));
    }

    [Fact]
    public async Task A_hook_row_whose_details_do_not_name_the_connector_takes_it_from_its_own_column()
    {
        var panel = await OpenAsync(
            () =>
            {
                Add("by-column", 1, "connector-hook", "preToolUse", "action=allow elapsed=5ms", "codex");
                Add("by-nothing", 2, "connector-hook", "preToolUse", "action=allow elapsed=5ms", connector: null);
                Add("no-details", 3, "connector-hook", "preToolUse", null, "codex");
            });

        Assert.Equal("codex · preToolUse", panel.Rows.Single(r => r.Id == "by-column").TargetText);
        Assert.Equal("preToolUse", panel.Rows.Single(r => r.Id == "by-nothing").TargetText);
        Assert.Equal("preToolUse", panel.Rows.Single(r => r.Id == "by-nothing").Title);

        // No details to read a decision from: the row says what it says. (A column connector is not a decision.)
        var none = panel.Rows.Single(r => r.Id == "no-details");
        Assert.Equal("codex · preToolUse", none.TargetText);
        Assert.Equal(none.Summary, none.DetailsText);
    }

    [Fact]
    public async Task Every_other_row_is_what_it_was()
    {
        var panel = await OpenAsync(
            () =>
            {
                Add("other-1", 1, "hook_decision", "tool/x", "connector=codex action=allow elapsed=1ms");
                Add("other-2", 2, "scan-finding", "skills/a", "plain prose details", "claudecode", "HIGH");
                Add("other-3", 3, "connector-hook-tampered", "hook.json", "connector=codex action=allow elapsed=1ms");
            });

        foreach (var row in panel.Rows)
        {
            Assert.False(row.IsHook);
            Assert.Equal(row.Target, row.TargetText);
            Assert.Equal(row.Summary, row.DetailsText);
            Assert.Equal(row.Action, row.Title);
        }
    }
}
