namespace DefenseClaw.Core.Audit;

/// <summary>
/// The Audit panel's "Actionable only" page (CUST-262): the keyset page of <see cref="AuditReader.QueryAsync"/> with the low-signal rows of
/// <see cref="ActionableRule"/> left out - and enough of the window read to fill the page anyway.
/// <para>
/// <b>Why not a WHERE clause.</b> The TUI's rule reads words in <c>details</c>, an unindexed scan of the whole window (1.4 s for 24 hours and
/// 17 s for 7 days on a real database, the same cost as the panel's text search), and its hook rows carry <c>would_block=false</c>, which the
/// rule reads as "block" - so no cheap SQL says the same thing. The rule is applied to the rows of the ordinary, indexed page instead.
/// </para>
/// <para>
/// <b>Why it reads more than one page.</b> On a real window (the newest 20,000 rows of a developer's database) about 86% of the rows are low-signal, they
/// come in runs (the newest 1,000 rows held none that matter, the first 1,200 held a hundred), and a page of 100 can hold none. So a call reads raw pages
/// of <see cref="AuditQuery.Limit"/> rows, one after the other from the cursor, until <see cref="AuditQuery.Limit"/> actionable rows are in hand, the
/// window ends, or <see cref="ScanPages"/> pages have been read - four times the 500 rows the TUI filters, which on that database would have shown an
/// empty list. Each read is the ordinary page (index seek, stops at its limit: a few milliseconds), and a dense window stops after one or two of them.
/// The reader's unchanged-snapshot memo holds four pages, so a repeat over a database that did not change is free when the scan was that short, and
/// re-reads the pages of a longer one.
/// </para>
/// <para>
/// <b>The cursor is the last row <em>read</em>, shown or not</b>, so "Load more" continues exactly where this call stopped and no row is skipped or
/// repeated. <see cref="AuditPage.HasMore"/> is true when rows remain after it, which is not a promise that an actionable one does: a page
/// that ends the scan with none left over simply comes back empty and without a cursor. <see cref="AuditPage.HiddenRows"/> names the rows that were read
/// and left out.
/// </para>
/// </summary>
public static class ActionableAuditPaging
{
    /// <summary>The most raw pages one call reads: twenty, which is 2,000 rows at the panel's page size and four times what the TUI filters.</summary>
    public const int ScanPages = 20;

    /// <summary>
    /// The next page of actionable events of <paramref name="query"/> (its <see cref="AuditQuery.After"/> is where it starts; its
    /// <see cref="AuditQuery.Limit"/> is how many it wants). Never blocks the caller's thread; a cancelled token ends the read in progress.
    /// </summary>
    public static async Task<AuditPage> QueryActionableAsync(this AuditReader reader, AuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);

        var want = query.EffectiveLimit;
        var budget = ScanPages * want;
        var kept = new List<AuditEvent>(want);
        var hidden = new List<AuditCursor>();
        var scanned = 0;
        var cursor = query.After;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await reader.QueryAsync(query with { After = cursor, Limit = want }, cancellationToken).ConfigureAwait(false);
            var events = page.Events;
            for (var i = 0; i < events.Count; i++)
            {
                var row = events[i];
                scanned++;
                if (ActionableRule.IsActionable(row))
                {
                    kept.Add(row);
                }
                else
                {
                    hidden.Add(row.Cursor);
                }

                if (kept.Count >= want)
                {
                    // The page is full: it ends at this row, and whatever follows it (the rest of this raw page, and the pages after) is for the next call.
                    var more = i < events.Count - 1 || page.HasMore;
                    return new AuditPage(kept, more ? row.Cursor : null, more) { HiddenRows = hidden };
                }
            }

            if (!page.HasMore)
            {
                return new AuditPage(kept, null, false) { HiddenRows = hidden };
            }

            cursor = page.NextCursor;
            if (scanned >= budget)
            {
                return new AuditPage(kept, cursor, true) { HiddenRows = hidden };
            }
        }
    }
}
