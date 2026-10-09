using System.Globalization;
using System.Text;

namespace DefenseClaw.Core.Audit;

/// <summary>Filter + pagination spec for <see cref="AuditReader"/>.</summary>
public sealed record AuditQuery
{
    /// <summary>Guard rail so a runaway page size cannot pull the whole table into memory.</summary>
    public const int MaxLimit = 5000;

    /// <summary>
    /// <see cref="PayloadLimitBytes"/> for a query that wants the whole of every value, whatever its size: an export, which writes a
    /// file the operator asked for and must not silently drop a long <c>details</c> from it.
    /// </summary>
    public const long NoPayloadLimit = PayloadCap.Unlimited;

    /// <summary>Restrict to a single bucket, e.g. <c>guardrail.evaluation</c>.</summary>
    public string? Bucket { get; init; }

    /// <summary>Restrict to any of several buckets. Combined with <see cref="Bucket"/> via OR.</summary>
    public IReadOnlyList<string>? Buckets { get; init; }

    /// <summary>Drop anything below this level.</summary>
    public AuditSeverity? MinimumSeverity { get; init; }

    /// <summary>Exact connector match. Use <see cref="IncludeNullConnector"/> for platform rows.</summary>
    public string? Connector { get; init; }

    /// <summary>Keep rows whose connector is NULL alongside a <see cref="Connector"/> match.</summary>
    public bool IncludeNullConnector { get; init; }

    /// <summary>Case-insensitive substring match on <c>action</c>.</summary>
    public string? ActionContains { get; init; }

    /// <summary>Exact <c>run_id</c> match (<c>idx_audit_run_id</c>): every event of one run.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Case-insensitive substrings, any of which must appear in <c>action</c> or <c>details</c> (the Mac's preset
    /// <c>actionLike</c>). Combined with the other filters by AND.
    /// </summary>
    public IReadOnlyList<string>? ActionAnyOf { get; init; }

    /// <summary>Case-insensitive substring match across details/event_name/tool_name/target.</summary>
    public string? SearchText { get; init; }

    /// <summary>Inclusive lower bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Page size. Clamped to [1, <see cref="MaxLimit"/>].</summary>
    public int Limit { get; init; } = 100;

    /// <summary>Keyset anchor: return rows strictly after this one in sort order.</summary>
    public AuditCursor? After { get; init; }

    /// <summary>Default is newest-first, which is what every panel wants.</summary>
    public bool Ascending { get; init; }

    /// <summary>
    /// The most bytes of <c>details</c> and of <c>structured_json</c> loaded for each row; a value over it is left in the database and
    /// the row says so (<see cref="AuditEvent.Oversized"/>). Null is the reader's own limit
    /// (<see cref="AuditReader.DefaultPayloadLimitBytes"/>); <see cref="NoPayloadLimit"/> loads everything. A query with its own limit is
    /// not remembered by the reader's unchanged-snapshot memo (it is the unusual, big one).
    /// </summary>
    public long? PayloadLimitBytes { get; init; }

    public int EffectiveLimit => Math.Clamp(Limit, 1, MaxLimit);

    /// <summary>
    /// A text that is the same for two queries that select the same rows in the same order, for the reader's unchanged-snapshot memo
    /// (a record compares its lists by reference, so two queries built separately would never be equal). Everything that changes the rows
    /// or their order is in it; the payload limit is not (a query that has one is not remembered).
    /// </summary>
    internal string PageKey() => Key(includePaging: true);

    /// <summary>The same for the total (<see cref="AuditReader.CountAsync"/>), which does not depend on the page size, the cursor or the direction.</summary>
    internal string CountKey() => Key(includePaging: false);

    private string Key(bool includePaging)
    {
        const char Field = '\u001f';
        const char Item = '\u001e';

        var key = new StringBuilder(160);
        key.Append(Bucket).Append(Field);
        AppendList(key, Buckets);
        key.Append(Field).Append(MinimumSeverity is { } severity ? ((int)severity).ToString(CultureInfo.InvariantCulture) : string.Empty)
           .Append(Field).Append(Connector)
           .Append(Field).Append(IncludeNullConnector ? '1' : '0')
           .Append(Field).Append(ActionContains)
           .Append(Field).Append(RunId).Append(Field);
        AppendList(key, ActionAnyOf);
        key.Append(Field).Append(SearchText)
           .Append(Field).Append(From?.UtcTicks.ToString(CultureInfo.InvariantCulture))
           .Append(Field).Append(To?.UtcTicks.ToString(CultureInfo.InvariantCulture));

        if (includePaging)
        {
            key.Append(Field).Append(EffectiveLimit.ToString(CultureInfo.InvariantCulture))
               .Append(Field).Append(After?.ToToken())
               .Append(Field).Append(Ascending ? '1' : '0');
        }

        return key.ToString();

        static void AppendList(StringBuilder into, IReadOnlyList<string>? values)
        {
            if (values is null)
            {
                return;
            }

            foreach (var value in values)
            {
                into.Append(value).Append(Item);
            }
        }
    }
}

/// <summary>One page of results plus the cursor needed to fetch the next.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, AuditCursor? NextCursor, bool HasMore)
{
    /// <summary>How many of the events have a value that was too large to load (<see cref="AuditEvent.Oversized"/>).</summary>
    public int OversizedCount { get; } = Events.Count(static e => e.IsOversized);

    /// <summary>
    /// True when the page has events and every one of them has a value that was too large to load. Such a page is never "no events":
    /// each row is listed, with the reason, and a screen says how many there are.
    /// </summary>
    public bool AllOversized => Events.Count > 0 && OversizedCount == Events.Count;

    /// <summary>
    /// The rows that were read and then left out of <see cref="Events"/> by a filter applied after the query - the low-signal rows
    /// <see cref="ActionableAuditPaging"/> skips - as the cursors that name them, so a caller can tell one it has already counted from a new one. Empty for
    /// an ordinary page, whose every row is in <see cref="Events"/>.
    /// </summary>
    public IReadOnlyList<AuditCursor> HiddenRows { get; init; } = Array.Empty<AuditCursor>();

    /// <summary>How many rows were read and left out (see <see cref="HiddenRows"/>).</summary>
    public int Hidden => HiddenRows.Count;
}
