using System.Text.RegularExpressions;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Logs panel's side of CUST-261: the filter box accepts <c>connector:codex</c>, the one token the 0.8.10 TUI's Logs panel knows
/// (<c>split_connector_token</c>), through the same parser (<see cref="SearchQuery"/>) the Alerts and Audit panels use. Every other <c>field:value</c> is
/// plain text here - a log line has no severity or run column to narrow - and is searched for as written, so <c>severity:high</c> finds a line that says so.
/// <para>
/// <b>Connector.</b> A line passes when it belongs to the connector: an event of the Verdicts or Events stream by its connector, a log-file line by the
/// connector in its <c>[component:connector]</c> or, as the TUI looks, a <c>connector=codex</c> / <c>connector: codex</c> written in the line. The name is
/// matched whole (ignoring case), like the connector chip, so <c>codex</c> does not find <c>codex-cli</c>. A line with no connector is not in any
/// connector's view. The free words left are the substring search the box always was.
/// </para>
/// </summary>
public sealed partial class LogsPanelViewModel
{
    private string? _searchFor;
    private SearchQuery _search = SearchQuery.Empty;
    private string[] _connectors = Array.Empty<string>();
    private Regex[] _connectorMentions = Array.Empty<Regex>();

    /// <summary>The filter box, parsed; kept until the text changes (every line of a 5,000-line buffer asks).</summary>
    private SearchQuery ActiveSearch
    {
        get
        {
            var text = FilterText;
            if (!string.Equals(text, _searchFor, StringComparison.Ordinal))
            {
                _search = SearchQuery.Parse(text, SearchField.Connector);
                _connectors = _search.ValuesOf(SearchField.Connector).ToArray();
                _connectorMentions = _connectors.Select(Mention).ToArray();
                _searchFor = text;
            }

            return _search;
        }
    }

    /// <summary>True when <paramref name="entry"/> passes the filter box: each connector token and the free text, as described on the type.</summary>
    private bool PassesSearch(LogEntry entry)
    {
        var search = ActiveSearch;
        if (search.IsEmpty)
        {
            return true;
        }

        for (var i = 0; i < _connectors.Length; i++)
        {
            if (SearchQuery.SameConnector(entry.Connector, _connectors[i]))
            {
                continue;
            }

            // A line of a log file may say it in its text instead; an event of a stream says it in its connector, which is what the check above was.
            if (entry.IsStructured || !Says(_connectorMentions[i], entry.Raw))
            {
                return false;
            }
        }

        var free = search.FreeText;
        return free.Length == 0
            || entry.Raw.Contains(free, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(free, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the line matches the pattern; a line the pattern could not be tried on in time (a pathological one) does not.</summary>
    private static bool Says(Regex mention, string line)
    {
        try
        {
            return mention.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The line says <c>connector=codex</c> or <c>connector: codex</c> (the name whole, quotes allowed), as the TUI's connector pattern reads it.</summary>
    private static Regex Mention(string connector) =>
        new(
            @"\bconnector\s*[=:]\s*""?" + Regex.Escape(connector.Trim()) + @"""?(?![\w.-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(500));
}
