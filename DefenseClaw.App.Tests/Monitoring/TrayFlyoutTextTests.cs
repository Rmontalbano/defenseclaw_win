using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Monitoring;

/// <summary>
/// The flyout's wording and bar lengths are the Mac's (<c>MenuBarPopover.swift</c>): the same counts have to read and draw the same way on both
/// apps, so each rule is held here against the Swift it was taken from.
/// </summary>
public class TrayFlyoutTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0L, "0m up")]
    [InlineData(-5_000L, "0m up")]
    [InlineData(59_000L, "0m up")]
    [InlineData(43L * 60_000, "43m up")]
    [InlineData(3_600_000L, "60m up")]          // the Mac's tests are "greater than", so an hour on the dot is still minutes
    [InlineData(3_601_000L, "1h up")]
    [InlineData(5L * 3_600_000, "5h up")]
    [InlineData(86_400_000L, "24h up")]
    [InlineData(86_401_000L, "1d up")]
    [InlineData(4L * 86_400_000, "4d up")]
    public void Uptime_is_the_largest_whole_unit(long uptimeMs, string expected) =>
        Assert.Equal(expected, TrayFlyoutText.Uptime(uptimeMs));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(9, "just now")]
    [InlineData(10, "10s ago")]
    [InlineData(59, "59s ago")]
    [InlineData(60, "1m ago")]
    [InlineData(3_599, "59m ago")]
    [InlineData(3_600, "1h ago")]
    [InlineData(86_399, "23h ago")]
    [InlineData(86_400, "1d ago")]
    [InlineData(6 * 86_400, "6d ago")]
    [InlineData(7 * 86_400, "1w ago")]
    [InlineData(21 * 86_400, "3w ago")]
    [InlineData(364 * 86_400, "52w ago")]
    [InlineData(365 * 86_400, "1y ago")]
    public void A_relative_time_reads_in_its_largest_whole_unit(int secondsAgo, string expected) =>
        Assert.Equal(expected, TrayFlyoutText.Relative(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void A_time_in_the_future_is_just_now_and_an_unreadable_one_says_so()
    {
        Assert.Equal("just now", TrayFlyoutText.Relative(Now.AddMinutes(5), Now));
        Assert.Equal("unknown time", TrayFlyoutText.Relative(DateTimeOffset.MinValue, Now));
    }

    [Theory]
    [InlineData(0, 0, "recent block decisions")]
    [InlineData(3, 0, "recent block decisions")]     // no hook calls to divide by
    [InlineData(0, 46, "0% block rate")]
    [InlineData(4, 46, "9% block rate")]            // 8.7 %, rounded
    [InlineData(1, 3, "33% block rate")]
    [InlineData(1, 1, "100% block rate")]
    [InlineData(1, 200, "0.5% block rate")]         // under 1 %, one decimal
    [InlineData(1, 1000, "0.1% block rate")]
    [InlineData(1, 100, "1% block rate")]
    [InlineData(3, 2, "150% block rate")]           // blocks are counted over every row, hook calls over hook rows: the Mac does not clamp it
    public void The_block_rate_is_worded_as_the_mac_words_it(int blocks, int hookCalls, string expected) =>
        Assert.Equal(expected, TrayFlyoutText.BlockRate(blocks, hookCalls));

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(-3, 0.0)]
    [InlineData(250, 0.5)]
    [InlineData(750, 0.75)]
    public void The_hook_calls_bar_eases_toward_full(int hookCalls, double expected) =>
        Assert.Equal(expected, TrayFlyoutText.ActivityProgress(hookCalls), precision: 6);

    [Theory]
    [InlineData(0, 100, 0.0)]
    [InlineData(1, 1000, 0.06)]                     // a block always shows at least 6 %
    [InlineData(5, 100, 0.4)]                       // eight times the rate
    [InlineData(50, 100, 1.0)]                      // capped at full
    [InlineData(5, 0, 0.5)]                         // no hook calls: n / 10
    [InlineData(25, 0, 1.0)]
    public void The_blocks_bar_is_eight_times_the_rate(int blocks, int hookCalls, double expected) =>
        Assert.Equal(expected, TrayFlyoutText.BlockProgress(blocks, hookCalls), precision: 6);

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(10, 0.5)]
    [InlineData(90, 0.9)]
    public void The_findings_bar_eases_toward_full(int findings, double expected) =>
        Assert.Equal(expected, TrayFlyoutText.FindingsProgress(findings), precision: 6);

    [Theory]
    [InlineData(AuditSeverity.Critical, "Critical")]
    [InlineData(AuditSeverity.High, "High")]
    [InlineData(AuditSeverity.Medium, "Medium")]
    [InlineData(AuditSeverity.Low, "Low")]
    public void A_finding_s_tone_is_its_severity(AuditSeverity severity, string tone)
    {
        Assert.Equal(tone, TrayFlyoutText.SeverityTone(severity));
        Assert.Equal(tone, TrayFlyoutText.SeverityWord(severity));
    }

    [Theory]
    [InlineData("running", "Ok")]
    [InlineData("RUNNING", "Ok")]
    [InlineData("starting", "Warn")]
    [InlineData("degraded", "Warn")]
    [InlineData("error", "Bad")]
    [InlineData("stopped", "Bad")]
    [InlineData("disabled", "Neutral")]
    [InlineData("", "Neutral")]
    [InlineData(null, "Neutral")]
    public void A_connector_dot_follows_the_state_text_the_overview_panel_reads(string? state, string tone) =>
        Assert.Equal(tone, TrayFlyoutText.ConnectorTone(state));

    [Fact]
    public void A_meter_row_keeps_the_fill_and_the_rest_adding_to_one_and_shows_a_sliver_for_a_non_zero_count()
    {
        var row = new FlyoutMetricRow("Blocks", "Bad", "Audit", new RelayCommand(() => { }));

        // Nothing yet: an empty bar.
        Assert.Equal(new GridLength(0, GridUnitType.Star), row.Fill);
        Assert.Equal(new GridLength(1, GridUnitType.Star), row.Rest);

        // A count of zero is an empty bar whatever the easing says; a count of one is never invisible.
        row.SetProgress(0.5, count: 0);
        Assert.Equal(0, row.Fill.Value);
        row.SetProgress(0.0, count: 1);
        Assert.Equal(0.02, row.Fill.Value, precision: 6);

        row.SetProgress(0.4, count: 7);
        Assert.Equal(0.4, row.Fill.Value, precision: 6);
        Assert.Equal(1.0, row.Fill.Value + row.Rest.Value, precision: 6);

        // Out-of-range easing is clamped, not drawn past the track.
        row.SetProgress(3.0, count: 7);
        Assert.Equal(1.0, row.Fill.Value, precision: 6);
        Assert.Equal(0.0, row.Rest.Value, precision: 6);
    }

    [Fact]
    public void A_meter_row_describes_itself_for_a_screen_reader_with_its_number_its_detail_and_where_it_goes()
    {
        var row = new FlyoutMetricRow("Hook Calls", "Accent", "Logs", new RelayCommand(() => { }))
        {
            Value = "46",
            Detail = "latest 500 audit events",
        };

        row.Describe();

        Assert.Equal("Hook Calls 46, latest 500 audit events. Opens Logs.", row.AutomationName);
    }
}
