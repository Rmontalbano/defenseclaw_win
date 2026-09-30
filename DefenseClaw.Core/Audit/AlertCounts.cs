using System.Globalization;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// One row of the alert queue (<see cref="AlertQueueReader"/>): what the status chips, the tray and the Alerts panel
/// need to list it, and nothing more — the heavy columns (<c>details</c>, <c>structured_json</c>) are never read.
/// </summary>
/// <param name="Id">The <c>audit_events.id</c>, the key an acknowledgement refers to. Empty for the rare row with a NULL id.</param>
/// <param name="Severity">Always <see cref="AuditSeverity.Critical"/>, <see cref="AuditSeverity.High"/>, <see cref="AuditSeverity.Medium"/> or <see cref="AuditSeverity.Low"/>: the queue holds nothing below.</param>
/// <param name="Action">The event's <c>action</c> (for a scanner finding, <c>scan-finding</c>): the row's kind.</param>
/// <param name="Target">What the event is about, capped at <see cref="AlertQueueReader.TargetLimit"/> characters; null when the row has none.</param>
/// <param name="Connector">The connector the event is attributed to (<c>claudecode</c>, <c>codex</c>, …); null for a platform-scoped row.</param>
/// <param name="Timestamp">When it happened (UTC); <see cref="DateTimeOffset.MinValue"/> when the stored text could not be read.</param>
public sealed record AlertQueueItem(
    string Id,
    AuditSeverity Severity,
    string Action,
    string? Target,
    string? Connector,
    DateTimeOffset Timestamp);

/// <summary>How many queue rows there are at each of the four severities that can be in it.</summary>
public readonly record struct SeverityTally(int Critical, int High, int Medium, int Low)
{
    public static SeverityTally Zero => default;

    public int Total => Critical + High + Medium + Low;

    /// <summary>The count at <paramref name="severity"/>; zero for a level the queue never holds (INFO, WARN, unknown).</summary>
    public int this[AuditSeverity severity] => severity switch
    {
        AuditSeverity.Critical => Critical,
        AuditSeverity.High => High,
        AuditSeverity.Medium => Medium,
        AuditSeverity.Low => Low,
        _ => 0,
    };

    /// <summary>This tally with one more row at <paramref name="severity"/>; a level the queue never holds changes nothing.</summary>
    public SeverityTally With(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical => this with { Critical = Critical + 1 },
        AuditSeverity.High => this with { High = High + 1 },
        AuditSeverity.Medium => this with { Medium = Medium + 1 },
        AuditSeverity.Low => this with { Low = Low + 1 },
        _ => this,
    };

    public static SeverityTally operator +(SeverityTally left, SeverityTally right) =>
        new(left.Critical + right.Critical, left.High + right.High, left.Medium + right.Medium, left.Low + right.Low);
}

/// <summary>Where an <see cref="AlertCounts"/> came from.</summary>
public enum AlertCountsSource
{
    /// <summary>The audit database's alert queue (<see cref="AlertQueueReader"/>): the full window of <see cref="AlertQueueReader.DefaultWindowLimit"/> rows.</summary>
    Database,

    /// <summary>The gateway's <c>/alerts</c> list, for a database that predates the v8 schema: only the newest page, so <see cref="AlertCounts.HasMore"/> is usually set.</summary>
    Gateway,
}

/// <summary>
/// The Mac app's one definition of "unacknowledged findings", counted: the severity tallies behind the sidebar
/// badge, the status-strip chip and the tray, plus the newest few rows to list.
/// <para>
/// <b>What is counted.</b> The queue window — the newest <see cref="AlertQueueReader.DefaultWindowLimit"/> rows that are
/// findings, at LOW or above, not dismissed and not acknowledged (see <see cref="AlertQueueReader"/>). So a busy install
/// reads "500", not its true backlog; <see cref="HasMore"/> says the window was full. The tallies (<see cref="Tally"/>,
/// <see cref="Total"/>) cover the whole window; <see cref="Newest"/> is only its first rows.
/// </para>
/// <para>
/// <b>Scoped to a connector</b> with <see cref="TallyFor"/>, which takes the predicate <c>ConnectorScope.Allows</c> is:
/// the per-connector breakdown is kept for exactly that, so a scoped badge needs no second query. Immutable and cheap
/// to keep; <see cref="SameAs"/> is the "did anything a badge shows change" test.
/// </para>
/// </summary>
public sealed class AlertCounts
{
    /// <summary>How many rows <see cref="Newest"/> holds unless the reader was asked for another number.</summary>
    public const int DefaultNewestLimit = 50;

    private readonly IReadOnlyDictionary<string, SeverityTally> _byConnector;

    /// <summary>Nothing queued: what a missing or empty database, and the state before the first read, are.</summary>
    public static AlertCounts Empty { get; } = new(Array.Empty<AlertQueueItem>(), hasMore: false);

    /// <param name="window">The queue window, newest first. Rows below LOW are ignored.</param>
    /// <param name="hasMore">True when more findings than <paramref name="window"/> holds are waiting.</param>
    /// <param name="newestLimit">How many of the newest rows to keep in <see cref="Newest"/>.</param>
    /// <param name="source">Where the rows came from.</param>
    public AlertCounts(
        IReadOnlyList<AlertQueueItem> window,
        bool hasMore,
        int newestLimit = DefaultNewestLimit,
        AlertCountsSource source = AlertCountsSource.Database)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfNegative(newestLimit);

        var tally = SeverityTally.Zero;
        var byConnector = new Dictionary<string, SeverityTally>(StringComparer.OrdinalIgnoreCase);
        var newest = new List<AlertQueueItem>(Math.Min(newestLimit, window.Count));

        foreach (var item in window)
        {
            if (item.Severity < AuditSeverity.Low || item.Severity == AuditSeverity.Warn)
            {
                continue;
            }

            tally = tally.With(item.Severity);

            var key = item.Connector?.Trim() ?? string.Empty;
            byConnector[key] = (byConnector.TryGetValue(key, out var existing) ? existing : SeverityTally.Zero).With(item.Severity);

            if (newest.Count < newestLimit)
            {
                newest.Add(item);
            }
        }

        Tally = tally;
        _byConnector = byConnector;
        Newest = newest;
        HasMore = hasMore;
        Source = source;
    }

    /// <summary>Per-severity counts over the whole window.</summary>
    public SeverityTally Tally { get; }

    public int Critical => Tally.Critical;

    public int High => Tally.High;

    public int Medium => Tally.Medium;

    public int Low => Tally.Low;

    /// <summary>Everything in the window: the number the sidebar badge shows.</summary>
    public int Total => Tally.Total;

    /// <summary>True when the window was full, i.e. the real backlog is larger than <see cref="Total"/> ("500+").</summary>
    public bool HasMore { get; }

    /// <summary>The newest rows, newest first, at most the limit the reader was given.</summary>
    public IReadOnlyList<AlertQueueItem> Newest { get; }

    public AlertCountsSource Source { get; }

    /// <summary>
    /// The window's counts per connector, for a connector-scoped badge. The key is the connector as stored (compared
    /// without case); rows with no connector are under the empty string.
    /// </summary>
    public IReadOnlyDictionary<string, SeverityTally> ByConnector => _byConnector;

    /// <summary>
    /// The counts of the rows whose connector <paramref name="allows"/> — pass <c>ConnectorScope.Allows</c>. A row with no
    /// connector is offered as <c>null</c>, which an explicit scope refuses, as it does on every other screen.
    /// </summary>
    public SeverityTally TallyFor(Func<string?, bool> allows)
    {
        ArgumentNullException.ThrowIfNull(allows);

        var tally = SeverityTally.Zero;
        foreach (var (connector, counts) in _byConnector)
        {
            if (allows(connector.Length == 0 ? null : connector))
            {
                tally += counts;
            }
        }

        return tally;
    }

    /// <summary>
    /// True when a badge built from <paramref name="other"/> would show what one built from this does: the same tallies
    /// (overall and per connector), the same "more than this" flag and source, and the same newest row — the last so that
    /// a window already at its cap, where a new finding displaces an old one and the counts stay put, still counts as a change.
    /// </summary>
    public bool SameAs(AlertCounts? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Tally != other.Tally ||
            HasMore != other.HasMore ||
            Source != other.Source ||
            _byConnector.Count != other._byConnector.Count ||
            !string.Equals(Newest.Count == 0 ? null : Newest[0].Id, other.Newest.Count == 0 ? null : other.Newest[0].Id, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (connector, counts) in _byConnector)
        {
            if (!other._byConnector.TryGetValue(connector, out var theirs) || theirs != counts)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The fallback for a database that has no <c>bucket</c> column: builds the counts from the gateway's <c>/alerts</c>
    /// list (already fetched by the monitor). Applies the queue's own rules to it — LOW or above, nothing dismissed — and
    /// marks <see cref="HasMore"/> when the list was as long as the page asked for, because then there may be more.
    /// </summary>
    /// <param name="alerts">The list, newest first as the gateway returns it.</param>
    /// <param name="requestedLimit">The page size the list was requested with.</param>
    /// <param name="newestLimit">How many of the newest rows to keep.</param>
    public static AlertCounts FromGateway(IReadOnlyList<GatewayAlert> alerts, int requestedLimit, int newestLimit = DefaultNewestLimit)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        var window = new List<AlertQueueItem>(alerts.Count);
        foreach (var alert in alerts)
        {
            if (alert.Action is { } action && action.StartsWith("dismiss", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var severity = AuditSeverityExtensions.Parse(alert.Severity);
            if (severity is not (AuditSeverity.Critical or AuditSeverity.High or AuditSeverity.Medium or AuditSeverity.Low))
            {
                continue;
            }

            window.Add(new AlertQueueItem(
                alert.Id,
                severity,
                alert.Action ?? string.Empty,
                alert.Target,
                ConnectorOf(alert),
                alert.Timestamp));
        }

        // The gateway lists newest first already; a stable sort keeps that order for rows that share a timestamp.
        var ordered = window.OrderByDescending(static item => item.Timestamp).ToList();
        return new AlertCounts(ordered, hasMore: alerts.Count >= requestedLimit, newestLimit, AlertCountsSource.Gateway);
    }

    /// <summary>The connector a gateway alert names, from its top-level <c>connector</c> member if it has one.</summary>
    private static string? ConnectorOf(GatewayAlert alert)
    {
        if (alert.AdditionalData is not null &&
            alert.AdditionalData.TryGetValue("connector", out var element) &&
            element.ValueKind == System.Text.Json.JsonValueKind.String &&
            element.GetString() is { } text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        return null;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Total}{(HasMore ? "+" : string.Empty)} unacknowledged (C{Critical} H{High} M{Medium} L{Low}, {Source})");
}
