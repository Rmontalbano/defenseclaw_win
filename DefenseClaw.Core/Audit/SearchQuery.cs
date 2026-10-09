using System.Text;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// A field a search box can name with <c>field:value</c> (CUST-261). The 0.8.10 TUI's Audit panel takes <c>action</c>, <c>actor</c>, <c>connector</c>,
/// <c>details</c>, <c>id</c>, <c>run</c> / <c>run_id</c>, <c>severity</c>, <c>target</c> and <c>type</c> (<c>_matches_search_query</c>), and its Alerts and
/// Logs panels the one token <c>connector</c> (<c>split_connector_token</c>); the correlation ids an alert carries are the other three.
/// </summary>
public enum SearchField
{
    /// <summary><c>connector:codex</c> - the connector the row is attributed to, by name (not a part of it, like the connector chip).</summary>
    Connector,

    /// <summary><c>severity:high</c> - the stored severity, or the bucket a spelling like WARN is coloured as.</summary>
    Severity,

    /// <summary><c>run:</c> / <c>run_id:</c> - a run (agent session) id, or a part of one.</summary>
    Run,

    /// <summary><c>id:</c> - an event id, or a part of one.</summary>
    Id,

    /// <summary><c>actor:</c> - who recorded the event.</summary>
    Actor,

    /// <summary><c>type:</c> - what the row is about: the kind of thing its action names (skill, mcp, plugin, tool, scan, credential, alert, config; the TUI's
    /// TYPE column) or the bucket it was recorded in (this app's Type column).</summary>
    Type,

    /// <summary><c>target:</c> - the target of the event.</summary>
    Target,

    /// <summary><c>action:</c> - the action of the event.</summary>
    Action,

    /// <summary><c>details:</c> - the details text of the event.</summary>
    Details,

    /// <summary><c>trace:</c> / <c>trace_id:</c> - a trace id, or a part of one.</summary>
    Trace,

    /// <summary><c>request:</c> / <c>request_id:</c> - a request id, or a part of one.</summary>
    Request,

    /// <summary><c>session:</c> / <c>session_id:</c> - a session id, or a part of one.</summary>
    Session,
}

/// <summary>One <c>field:value</c> of a search: <see cref="Value"/> is as typed (quotes removed), never empty.</summary>
public readonly record struct SearchToken(SearchField Field, string Value);

/// <summary>
/// What an operator typed in a search box, split into the <c>field:value</c> tokens a panel can filter on exactly and the free text that is left
/// (CUST-261). One parser for Alerts, Audit and Logs, so the same words narrow the same rows in all three; each panel names the fields it
/// understands and everything else stays free text, as the TUI's panels do (its Logs and Alerts panels know only <c>connector:</c>).
/// <para>
/// <b>Grammar.</b> Words are separated by white space. A word is <c>field:value</c> when what precedes its first colon is a field the caller
/// recognises (<c>Connector:Codex</c> as well as <c>connector:codex</c>); the value is the rest of the word. A <c>"</c> opens a quoted stretch that runs
/// to the next <c>"</c> (or to the end of the text, while a closing quote is still being typed) and may hold spaces:
/// <c>target:"my skill"</c>. A word that starts with a quote is a quoted phrase of free text (<c>"rm -rf"</c>). Anything else - a word with no colon, a
/// <c>foo:bar</c> whose <c>foo</c> is no field, a <c>run:</c> in a panel that does not know it - is free text, verbatim. A token with no value
/// (<c>connector:</c>, while the name is still being typed) narrows nothing and is dropped, as the TUI's empty match is.
/// </para>
/// <para>
/// <b>Free text is one phrase.</b> <see cref="FreeText"/> is the free words in order, joined by a space, and a panel matches it as a single substring (as
/// each did before tokens existed; the TUI matches each word on its own). With nothing recognised and nothing quoted it is the typed text trimmed - exactly -
/// so a search with no tokens in it behaves as it always did. Values keep the case they were typed in; matching ignores it.
/// </para>
/// </summary>
public sealed class SearchQuery
{
    private static readonly Dictionary<string, SearchField> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connector"] = SearchField.Connector,
        ["severity"] = SearchField.Severity,
        ["run"] = SearchField.Run,
        ["run_id"] = SearchField.Run,
        ["id"] = SearchField.Id,
        ["actor"] = SearchField.Actor,
        ["type"] = SearchField.Type,
        ["target"] = SearchField.Target,
        ["action"] = SearchField.Action,
        ["details"] = SearchField.Details,
        ["trace"] = SearchField.Trace,
        ["trace_id"] = SearchField.Trace,
        ["request"] = SearchField.Request,
        ["request_id"] = SearchField.Request,
        ["session"] = SearchField.Session,
        ["session_id"] = SearchField.Session,
    };

    /// <summary>A search with nothing in it: no tokens, no text.</summary>
    public static SearchQuery Empty { get; } = new(Array.Empty<SearchToken>(), string.Empty);

    private SearchQuery(IReadOnlyList<SearchToken> tokens, string freeText)
    {
        Tokens = tokens;
        FreeText = freeText;
    }

    /// <summary>The <c>field:value</c> tokens, in the order they were typed. Empty values are not among them.</summary>
    public IReadOnlyList<SearchToken> Tokens { get; }

    /// <summary>What is left once the tokens are taken out: the free words as one phrase; empty when there are none.</summary>
    public string FreeText { get; }

    /// <summary>True when there is nothing to filter on.</summary>
    public bool IsEmpty => Tokens.Count == 0 && FreeText.Length == 0;

    /// <summary>The name a field is typed with (<c>run</c>, <c>trace</c>, ...); its aliases (<c>run_id</c>) are accepted as well.</summary>
    public static string NameOf(SearchField field) => field switch
    {
        SearchField.Connector => "connector",
        SearchField.Severity => "severity",
        SearchField.Run => "run",
        SearchField.Id => "id",
        SearchField.Actor => "actor",
        SearchField.Type => "type",
        SearchField.Target => "target",
        SearchField.Action => "action",
        SearchField.Details => "details",
        SearchField.Trace => "trace",
        SearchField.Request => "request",
        SearchField.Session => "session",
        _ => field.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Parses <paramref name="text"/>. <paramref name="recognised"/> names the fields the caller filters on; with none given every field is
    /// recognised. Never throws, whatever the text.
    /// </summary>
    public static SearchQuery Parse(string? text, params SearchField[] recognised)
    {
        var source = text ?? string.Empty;
        if (source.Trim().Length == 0)
        {
            return Empty;
        }

        var tokens = new List<SearchToken>();
        var free = new List<string>();
        var rewritten = false;
        var at = 0;

        while (at < source.Length)
        {
            if (char.IsWhiteSpace(source[at]))
            {
                at++;
                continue;
            }

            var start = at;
            var unquoted = new StringBuilder();
            while (at < source.Length && !char.IsWhiteSpace(source[at]))
            {
                if (source[at] != '"')
                {
                    _ = unquoted.Append(source[at]);
                    at++;
                    continue;
                }

                at++;
                while (at < source.Length && source[at] != '"')
                {
                    _ = unquoted.Append(source[at]);
                    at++;
                }

                if (at < source.Length)
                {
                    at++;
                }
            }

            var raw = source[start..at];
            if (TryField(raw, recognised, out var field, out var nameLength))
            {
                rewritten = true;
                var value = unquoted.ToString()[(nameLength + 1)..];
                if (value.Length > 0)
                {
                    tokens.Add(new SearchToken(field, value));
                }

                continue;
            }

            if (raw[0] == '"')
            {
                // A quoted phrase: the quotes were the operator's way of keeping its words together, not part of what is searched for.
                rewritten = true;
                var phrase = unquoted.ToString().Trim();
                if (phrase.Length > 0)
                {
                    free.Add(phrase);
                }

                continue;
            }

            // Not a field of this panel's (or no field at all): kept as it was typed, quotes and all.
            free.Add(raw);
        }

        if (tokens.Count == 0 && !rewritten)
        {
            return new SearchQuery(Array.Empty<SearchToken>(), source.Trim());
        }

        return new SearchQuery(tokens, string.Join(' ', free));
    }

    /// <summary>The value of the last token for <paramref name="field"/> (the TUI's <c>connector:</c> takes the last one too), or null when there is none.</summary>
    public string? ValueOf(SearchField field)
    {
        string? value = null;
        foreach (var token in Tokens)
        {
            if (token.Field == field)
            {
                value = token.Value;
            }
        }

        return value;
    }

    /// <summary>Every value typed for <paramref name="field"/>, in order.</summary>
    public IEnumerable<string> ValuesOf(SearchField field)
    {
        foreach (var token in Tokens)
        {
            if (token.Field == field)
            {
                yield return token.Value;
            }
        }
    }

    /// <summary>
    /// <paramref name="text"/> as it has to be typed to be searched for as plain text: unchanged when it parses back as itself, in quotes when it would
    /// otherwise read as a token (a target of <c>type:abc</c>, say). What "Show same target" puts in the box.
    /// </summary>
    public static string AsFreeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parsed = Parse(text);
        return parsed.Tokens.Count == 0 && string.Equals(parsed.FreeText, text.Trim(), StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal)
            ? text
            : "\"" + text + "\"";
    }

    /// <summary>
    /// Whether a row passes this search. <paramref name="valuesOf"/> gives the values a row has for a field (a row may have several: a target and its
    /// reference); <paramref name="haystack"/> is everything its free text is looked for in. Every token must match one value of its field: a
    /// connector exactly (a row with none never matches, like the connector chip), any other field as a case-insensitive part of it. The free text
    /// must be a part of at least one haystack value. A search with nothing in it passes every row.
    /// </summary>
    public bool Matches(Func<SearchField, IEnumerable<string?>> valuesOf, IEnumerable<string?> haystack)
    {
        ArgumentNullException.ThrowIfNull(valuesOf);
        ArgumentNullException.ThrowIfNull(haystack);

        foreach (var token in Tokens)
        {
            var wanted = token.Value.Trim();
            var found = false;
            foreach (var value in valuesOf(token.Field))
            {
                if (token.Field == SearchField.Connector ? SameConnector(value, wanted) : Contains(value, wanted))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        if (FreeText.Length == 0)
        {
            return true;
        }

        foreach (var value in haystack)
        {
            if (Contains(value, FreeText))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="connector"/> is <paramref name="wanted"/>, ignoring case and padding; a blank connector is nobody's.</summary>
    public static bool SameConnector(string? connector, string wanted) =>
        !string.IsNullOrWhiteSpace(connector) && string.Equals(connector.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="haystack"/> holds <paramref name="needle"/>, ignoring case.</summary>
    public static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public override string ToString()
    {
        var parts = Tokens.Select(t => NameOf(t.Field) + ":" + (t.Value.Any(char.IsWhiteSpace) ? "\"" + t.Value + "\"" : t.Value));
        if (FreeText.Length > 0)
        {
            parts = parts.Append(FreeText);
        }

        return string.Join(' ', parts);
    }

    /// <summary>Whether the word starts with <c>name:</c> for a field the caller recognises; <paramref name="nameLength"/> is the name's length.</summary>
    private static bool TryField(string word, SearchField[] recognised, out SearchField field, out int nameLength)
    {
        field = default;
        nameLength = 0;

        var colon = word.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || word.AsSpan(0, colon).IndexOf('"') >= 0)
        {
            return false;
        }

        if (!Names.TryGetValue(word[..colon], out field))
        {
            return false;
        }

        if (recognised.Length > 0 && Array.IndexOf(recognised, field) < 0)
        {
            return false;
        }

        nameLength = colon;
        return true;
    }
}
