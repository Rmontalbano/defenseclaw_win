namespace DefenseClaw.Core.Audit;

/// <summary>
/// The Audit panel's search box as an <see cref="AuditQuery"/> (CUST-261): the tokens of a <see cref="SearchQuery"/> become filters the database
/// applies, the free text becomes the query's existing <see cref="AuditQuery.SearchText"/>, and nothing typed is ever put into SQL - the reader gets a
/// column from its own closed list and the value as a bound parameter (see <see cref="AuditFieldFilter"/>).
/// <para>
/// <b>Every token is SQL here</b>, so the page, the live refresh (which reads the list's own query), the total and the export all honour a search with
/// no code of their own: a token the database cannot express would need the rows read and filtered in memory, and a window that is mostly noise would
/// then show a page that is mostly empty. A part-of-the-value match is a LIKE over the window, the same cost as the free-text search; the one exact match
/// is <c>connector:</c>, which is also the query's own indexed <see cref="AuditQuery.Connector"/> when nothing else holds it.
/// </para>
/// <para>
/// <b>Connector.</b> Connector names are lower-case ids, and the TUI lower-cases the whole search, so <c>connector:Codex</c> asks for <c>codex</c>. The
/// first one becomes <see cref="AuditQuery.Connector"/> (the reader then chooses between walking the retention index and seeking
/// <c>idx_audit_connector</c> for it, as it does for the connector filter); one the query already has (the filter bar's, the shared scope) or a second
/// one is ANDed on as an exact filter, so two different connectors match nothing, as they should.
/// </para>
/// </summary>
public static class AuditQuerySearch
{
    /// <summary>
    /// <paramref name="query"/> narrowed by <paramref name="search"/>. A search with nothing in it returns the query as it was; the free text replaces
    /// <see cref="AuditQuery.SearchText"/> when there is any; the filters are added to those the query already has.
    /// </summary>
    public static AuditQuery WithSearch(this AuditQuery query, SearchQuery search)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(search);

        if (search.IsEmpty)
        {
            return query;
        }

        var filters = new List<AuditFieldFilter>(query.FieldFilters ?? Array.Empty<AuditFieldFilter>());
        var connector = query.Connector;
        var connectorFree = string.IsNullOrWhiteSpace(connector);

        foreach (var token in search.Tokens)
        {
            var value = token.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (token.Field == SearchField.Connector)
            {
                var name = value.ToLowerInvariant();
                if (connectorFree)
                {
                    connector = name;
                    connectorFree = false;
                }
                else
                {
                    filters.Add(new AuditFieldFilter(AuditField.Connector, name, AuditFieldMatch.Exact));
                }

                continue;
            }

            filters.Add(new AuditFieldFilter(FieldOf(token.Field), value));
        }

        return query with
        {
            Connector = connector,
            FieldFilters = filters.Count == 0 ? null : filters,
            SearchText = search.FreeText.Length > 0 ? search.FreeText : query.SearchText,
        };
    }

    private static AuditField FieldOf(SearchField field) => field switch
    {
        SearchField.Connector => AuditField.Connector,
        SearchField.Severity => AuditField.Severity,
        SearchField.Run => AuditField.Run,
        SearchField.Id => AuditField.Id,
        SearchField.Actor => AuditField.Actor,
        SearchField.Type => AuditField.Type,
        SearchField.Target => AuditField.Target,
        SearchField.Action => AuditField.Action,
        SearchField.Details => AuditField.Details,
        SearchField.Trace => AuditField.Trace,
        SearchField.Request => AuditField.Request,
        SearchField.Session => AuditField.Session,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown search field."),
    };
}
