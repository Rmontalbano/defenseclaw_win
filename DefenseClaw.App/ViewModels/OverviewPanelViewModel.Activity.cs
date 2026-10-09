using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>One number of the Activity card's summary row: a caption over a value, the Mac's <c>summaryItem</c>.</summary>
public sealed record SummaryItem(string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// The Activity card (CUST-209): the last 24 hours as numbers and as an hourly bar chart of hook decisions, allowed against blocked, read from
/// <c>audit.db</c> by <see cref="HourlyActivityReader"/> (an indexed, read-only, cancellable query of a fraction of a second). The summary row
/// adds what <c>status --json</c> reports (skill and MCP list counts, total scans), so it fills in a moment after the chart. Everything here
/// runs with the panel's other slow reads, while the panel is on screen, and stops when it leaves.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private readonly HourlyActivityReader _hourlyReader;
    private HourlyActivity? _hourly;

    /// <summary>The chart's bars, oldest hour first; empty until the first read, and on a database that cannot be read.</summary>
    [ObservableProperty]
    private IReadOnlyList<HourlyBucket> _hourlyBuckets = Array.Empty<HourlyBucket>();

    /// <summary>What the chart says about itself when there is nothing to draw: no database, no index, nothing in 24 hours, a failed read.</summary>
    [ObservableProperty]
    private string _activityNote = "Reading audit.db…";

    /// <summary>The one sentence a screen reader gets for the chart: totals and the busiest hour.</summary>
    [ObservableProperty]
    private string _hourlyAutomationSummary = string.Empty;

    [ObservableProperty]
    private bool _hasHourlyData;

    public ObservableCollection<SummaryItem> ActivitySummary { get; } = new();

    /// <summary>
    /// Reads the day's hourly decisions. A failure is shown on the chart and does not touch the rest of the panel; a cancelled read (the panel
    /// left the screen) leaves everything as it was.
    /// </summary>
    internal async Task RefreshHourlyAsync(CancellationToken cancellationToken)
    {
        HourlyActivity hourly;
        try
        {
            hourly = await _hourlyReader.ReadAsync(Services.ReaderTimeouts.Hourly, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // A locked or half-written database degrades the chart, not the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or TimeoutException or InvalidOperationException)
#pragma warning restore CA1031
        {
            _hourly = null;
            HourlyBuckets = Array.Empty<HourlyBucket>();
            HasHourlyData = false;
            HourlyAutomationSummary = string.Empty;
            ActivityNote = ex is TimeoutException ? "The hourly query did not answer in time." : "The hourly activity could not be read from audit.db.";
            RenderActivitySummary();
            return;
        }

        _hourly = hourly;
        switch (hourly.Status)
        {
            case HourlyActivityStatus.NoDatabase:
                HourlyBuckets = Array.Empty<HourlyBucket>();
                HasHourlyData = false;
                HourlyAutomationSummary = string.Empty;
                ActivityNote = "No audit database yet. It appears after the first event.";
                break;

            case HourlyActivityStatus.NoIndex:
                HourlyBuckets = Array.Empty<HourlyBucket>();
                HasHourlyData = false;
                HourlyAutomationSummary = string.Empty;
                ActivityNote = $"The hourly chart needs the {HourlyActivityReader.IndexName} index, which this audit.db does not have; reading it without would scan every row.";
                break;

            default:
                HourlyBuckets = hourly.Hours;
                HasHourlyData = hourly.Total > 0;
                HourlyAutomationSummary = Summarize(hourly);
                ActivityNote = hourly.Total > 0
                    ? "Hook decisions per hour (connector-hook events): allowed and blocked. The scale is the busiest hour."
                    : "No hook decisions in the last 24 hours.";
                break;
        }

        RenderActivitySummary();
    }

    /// <summary>Rebuilds the summary row from the last hourly read and the last <c>status --json</c>. No I/O.</summary>
    internal void RenderActivitySummary()
    {
        var items = new List<SummaryItem>();

        items.Add(_hourly is { Status: HourlyActivityStatus.Ok } h
            ? new SummaryItem("Hook decisions", $"{Count(h.Allowed)} allowed · {Count(h.Blocked)} blocked")
            : new SummaryItem("Hook decisions", "—"));

        items.Add(new SummaryItem("Skills", _status.BlockedSkills is { } bs && _status.AllowedSkills is { } als ? $"{Count(bs)} blocked · {Count(als)} allowed" : "—"));
        items.Add(new SummaryItem("MCPs", _status.BlockedMcps is { } bm && _status.AllowedMcps is { } alm ? $"{Count(bm)} blocked · {Count(alm)} allowed" : "—"));
        items.Add(new SummaryItem("Total scans", _status.TotalScans is { } scans ? Count(scans) : "—"));

        SyncByEquality(ActivitySummary, items, static item => item.Label);
    }

    /// <summary>"Hook decisions per hour, last 24 hours: 15,200 allowed, 6 blocked. Busiest hour 11 PM with 3,388."</summary>
    internal static string Summarize(HourlyActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var text = $"Hook decisions per hour, last {activity.Hours.Count.ToString(CultureInfo.CurrentCulture)} hours: " +
                   $"{Count(activity.Allowed)} allowed, {Count(activity.Blocked)} blocked.";
        if (activity.Peak > 0)
        {
            var busiest = activity.Hours.MaxBy(static b => b.Total);
            text += $" Busiest hour {busiest.HourStart.ToLocalTime().ToString("h tt", CultureInfo.CurrentCulture)} with {Count(busiest.Total)}.";
        }

        return text;
    }
}
