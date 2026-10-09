using System.Text.RegularExpressions;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Setup;

/// <summary>
/// What the Setup editors add to <see cref="EndpointDisplay"/> (CUST-272), which is the one place an address is reduced to what may be shown: the
/// host, and the port when there is one, and nothing else, failing closed to <see cref="EndpointDisplay.Unreadable"/>. A destination or webhook URL is
/// where credentials end up when an operator pastes one from a vendor's console (<c>user:password@host</c>, a token in the query, in the path of a chat
/// webhook, in the fragment). The CLI already cuts what it prints (<c>redact_endpoint_for_display</c> keeps the path of an observability endpoint;
/// <c>redact_webhook_url</c> replaces the path of a webhook with <c>***</c>), and <c>setup webhook test</c> prints the address whole, so the editors
/// reduce every address themselves, whatever the CLI printed. Two things are particular to them:
/// <list type="bullet">
///   <item><see cref="ForDestination"/>: a local destination has a path where a remote one has an address, and a path is not an endpoint.</item>
///   <item><see cref="ScrubLine"/>: the runner's per-line output filter. <see cref="EndpointDisplay.ScrubText"/> trims and cuts what it is given, which
///     would change the stored lines of a JSON document and of an indented report; a filter has to leave a line the way it found it, minus its secrets.</item>
/// </list>
/// </summary>
public static partial class EndpointHost
{
    private const int MaxPathLength = 260;
    private const int RegexTimeoutMilliseconds = 1000;

    /// <summary>A URL inside a line of text: a scheme, <c>://</c>, then everything up to a space or a quote. The pattern <see cref="EndpointDisplay"/> reads a sentence with.</summary>
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]{1,15}://[^\s""'<>`\\]+", RegexOptions.CultureInvariant, RegexTimeoutMilliseconds)]
    private static partial Regex UrlInText();

    /// <summary>
    /// The address a destination row shows. A local destination (a database, a file, the console) has a path where a remote one has an
    /// address, and a path is not an endpoint: it is shown as it is, on one line. Everything else - a kind this does not know too - is read as
    /// an address and cut to <see cref="EndpointDisplay.Host"/>.
    /// </summary>
    /// <param name="kind">The destination's kind (<c>otlp</c>, <c>http_jsonl</c>, <c>splunk_hec</c>, <c>sqlite</c>, ...).</param>
    /// <param name="target">What the CLI printed as its target.</param>
    public static string ForDestination(string? kind, string? target)
    {
        var text = target?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return ObservabilityKinds.IsLocal(kind) && !text.Contains("://", StringComparison.Ordinal)
            ? DisplayNames.Visible(text.Length > MaxPathLength ? text[..MaxPathLength] + "…" : text)
            : EndpointDisplay.Host(text);
    }

    /// <summary>
    /// One line of a command's output made fit to store and show: every URL in it is cut to <c>scheme://host[:port]</c> (<see cref="EndpointDisplay.Host"/>),
    /// then what looks like a credential is masked (<see cref="DisplayRedaction.Prose"/>: <c>token=...</c>, a bearer value, a key with a known prefix,
    /// an <c>Authorization</c> header). The line is otherwise as it was, indentation included. This is what <c>CliRunOptions.OutputLineFilter</c> is given
    /// for the setup editors' commands, so an address the CLI prints whole - <c>setup webhook test</c> does - never reaches Activity, the review's
    /// result or the clipboard.
    /// </summary>
    public static string ScrubLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        string reduced;
        try
        {
            reduced = UrlInText().Replace(line, static match =>
            {
                var schemeEnd = match.Value.IndexOf("://", StringComparison.Ordinal);
                var host = EndpointDisplay.Host(match.Value);
                return host == EndpointDisplay.Unreadable ? EndpointDisplay.Unreadable : match.Value[..schemeEnd] + "://" + host;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return DisplayRedaction.Mask;
        }

        return DisplayRedaction.Prose(reduced, int.MaxValue);
    }
}
