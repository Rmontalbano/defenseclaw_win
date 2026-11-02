using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// The hook checks the rules decided because the LLM judge gave no verdict in time (CUST-426, runtime 1.0.6, upstream #1395). The gateway counts them since the
/// judge started and reports them in <c>/health</c> <c>guardrail.details</c>: <c>judge_fallbacks</c> (the total), <c>judge_fallbacks_by_connector</c> and
/// <c>judge_last_fallback</c> (<c>timeout (codex)</c>), none of them present while there has been no fallback (internal/gateway/judge_health.go
/// <c>addFallbackDetailsLocked</c>, tag 1.0.6). A runtime before 1.0.6 sends none of it, which reads as "no fallbacks". Read tolerantly: a field of an
/// unexpected shape is dropped, never an error; the words are cut and made fit to draw.
/// </summary>
/// <param name="Total">How many hook checks the rules decided; always above zero.</param>
/// <param name="ByConnector">The count per connector name, ordered by name as the CLI prints them; empty when the gateway names none.</param>
/// <param name="Last">The gateway's words for the last one (<c>timeout (codex)</c>); empty when it sends none.</param>
public sealed record JudgeFallbackHealth(long Total, IReadOnlyList<KeyValuePair<string, long>> ByConnector, string Last)
{
    /// <summary>What <c>guardrail.judge.max_parallel</c> is suggested for (cmd_status.py <c>JUDGE_MAX_PARALLEL_HINT</c>).</summary>
    public const string MaxParallelHint = "a slow judge model: set guardrail.judge.max_parallel";

    private const int MaxConnectors = 33;
    private const int MaxNameChars = 40;
    private const int MaxLastChars = 160;

    /// <summary>
    /// The fallbacks the guardrail block reports, or null when it reports none: no block, no details, no count, a count that is not a whole number above zero.
    /// </summary>
    public static JudgeFallbackHealth? From(ServiceState? guardrail)
    {
        if (guardrail?.Details is not { ValueKind: JsonValueKind.Object } details ||
            !details.TryGetProperty("judge_fallbacks", out var count) || Whole(count) is not { } total || total <= 0)
        {
            return null;
        }

        var byConnector = new List<KeyValuePair<string, long>>();
        if (details.TryGetProperty("judge_fallbacks_by_connector", out var listed) && listed.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in listed.EnumerateObject())
            {
                if (byConnector.Count < MaxConnectors && Whole(entry.Value) is { } n && n >= 0 && Cut(entry.Name, MaxNameChars) is { Length: > 0 } name)
                {
                    byConnector.Add(new KeyValuePair<string, long>(name, n));
                }
            }

            byConnector.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        }

        var last = details.TryGetProperty("judge_last_fallback", out var text) && text.ValueKind == JsonValueKind.String ? Cut(text.GetString(), MaxLastChars) : string.Empty;
        return new JudgeFallbackHealth(total, byConnector, last);
    }

    /// <summary>
    /// The CLI's sentence (cmd_status.py <c>judge_fallback_summary</c>): <c>the rules decided 12 hook check(s) because the judge gave no verdict in time
    /// (claudecode 3, codex 9); last: timeout (codex)</c>.
    /// </summary>
    public string Summary
    {
        get
        {
            var text = $"the rules decided {Total.ToString(CultureInfo.InvariantCulture)} hook check(s) because the judge gave no verdict in time";
            if (ByConnector.Count > 0)
            {
                text += " (" + string.Join(", ", ByConnector.Select(static pair => pair.Key + " " + pair.Value.ToString(CultureInfo.InvariantCulture))) + ")";
            }

            return Last.Length > 0 ? text + "; last: " + Last : text;
        }
    }

    /// <summary>A whole number from a JSON number (Go writes an integer; a float with no fraction is accepted too); null for anything else.</summary>
    private static long? Whole(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetInt64(out var integer))
        {
            return integer;
        }

        return value.TryGetDouble(out var real) && real == Math.Floor(real) && Math.Abs(real) < long.MaxValue ? (long)real : null;
    }

    private static string Cut(string? text, int limit)
    {
        var visible = DisplayNames.Visible((text ?? string.Empty).Trim());
        return visible.Length <= limit ? visible : visible[..limit];
    }
}
