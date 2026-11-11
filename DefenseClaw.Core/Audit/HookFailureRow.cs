using System.Globalization;
using System.Text;

namespace DefenseClaw.Core.Audit;

/// <summary>What a hook did on its own while the gateway did not answer, as an audit row written from <c>logs\hook-failures.jsonl</c> says it.</summary>
public enum HookFailureKind
{
    /// <summary>A pre-execution event: the hook refused the tool call itself, and the agent got that refusal (fail mode closed).</summary>
    CallRefused,

    /// <summary>A post-execution event: the tool had already run, and the hook refused its result (fail mode closed).</summary>
    ResultRefused,

    /// <summary>Another named event (a prompt, a session start): the hook refused that event, not a tool call (fail mode closed).</summary>
    EventRefused,

    /// <summary>A line that names no event (a shell hook before 1.0.7): the hook refused something, and the row does not claim it as the call's verdict.</summary>
    UnnamedEventRefused,

    /// <summary>The call ran without a gateway check (fail mode open).</summary>
    RanUnchecked,
}

/// <summary>One hook-failure row, read from its own text.</summary>
/// <param name="Kind">What the hook did.</param>
/// <param name="Event">The hook event the row names (<c>PreToolUse</c> ...); empty for <see cref="HookFailureKind.RanUnchecked"/> and an unnamed event.</param>
/// <param name="Reason">The hook's own reason, unquoted (at most 300 characters, as the gateway cut it); empty when the row holds none.</param>
public sealed record HookFailureRefusal(HookFailureKind Kind, string Event, string Reason)
{
    /// <summary>True when the hook refused something (fail mode closed); false for a call that ran unchecked.</summary>
    public bool IsRefusal => Kind != HookFailureKind.RanUnchecked;

    /// <summary>The row's title in place of the action word: says it was the hook, not a policy.</summary>
    public string Headline => Kind switch
    {
        HookFailureKind.CallRefused => "Hook blocked the call itself: the gateway did not answer",
        HookFailureKind.ResultRefused => "Hook blocked the tool's result itself: the gateway did not answer",
        HookFailureKind.EventRefused => $"Hook blocked its {Event} event itself: the gateway did not answer",
        HookFailureKind.UnnamedEventRefused => "Hook blocked a hook event itself: the gateway did not answer",
        _ => "Ran unchecked during a gateway outage",
    };

    /// <summary>Why, in plain words, for the detail pane.</summary>
    public string Explanation => Kind switch
    {
        HookFailureKind.RanUnchecked =>
            "The gateway was down or did not answer, and this connector's hook_fail_mode is open, so the hook let the call run without a gateway check. " +
            "No policy saw it.",
        HookFailureKind.ResultRefused =>
            "The tool had already run. The gateway was down or did not answer, and this connector's hook_fail_mode is closed, so the hook refused the " +
            "tool's result on its own. No gateway policy decided it, so there is no rule to look for.",
        _ =>
            "The gateway was down or did not answer, and this connector's hook_fail_mode is closed, so the hook refused on its own. No gateway policy " +
            "decided it, so there is no rule to look for.",
    } + " The hook logged the call in " + HookFailureRow.FailuresLog + ".";
}

/// <summary>
/// The audit rows the gateway writes from <c>logs\hook-failures.jsonl</c> (<c>hookFailureRow</c>, internal/gateway/hook_failopen_report.go at tags 1.0.7
/// and 1.0.8): action <c>guardrail-block</c>, severity MEDIUM, target <c>connector:event</c> for a call the hook refused itself while the gateway did not
/// answer (fail mode closed, new in 1.0.7), and action <c>guardrail-warn</c>, severity HIGH, target the connector, for a call that ran unchecked (1.0.6
/// already wrote these). Both are filed under bucket <c>guardrail.evaluation</c> with event names <c>legacy.audit.guardrail.block</c> /
/// <c>legacy.audit.guardrail.warn</c> (zz_generated_telemetry_producers.go), so neither the alert queue nor the runtime's <c>alerts --json</c> list holds
/// them; the Logs and Audit panels do. Every details text ends <c>; see logs/hook-failures.jsonl</c>, which is what tells such a row from a policy's.
/// </summary>
public static class HookFailureRow
{
    /// <summary>The file the rows come from, as the details text names it.</summary>
    public const string FailuresFileName = "hook-failures.jsonl";

    /// <summary>Where that file is, as the app shows it.</summary>
    public const string FailuresLog = @"logs\" + FailuresFileName;

    /// <summary>The words the Source field shows for these rows.</summary>
    public const string SourceLabel = "Hook (gateway unreachable)";

    /// <summary>The end of every such details text.</summary>
    internal const string Tail = "; see logs/" + FailuresFileName;

    /// <summary>The text a SQL <c>LIKE</c> matches a refusal's details on (with <see cref="Tail"/>): every refusal says <c>fail-closed</c>, a policy block never does.</summary>
    internal const string RefusalMark = "(fail-closed";

    private const string BlockAction = "guardrail-block";
    private const string WarnAction = "guardrail-warn";
    private const string UncheckedPrefix = "unchecked hook call during gateway outage: ";
    private const string CallPrefix = "hook blocked the call itself (fail-closed, ";
    private const string ResultPrefix = "the tool had already run; the hook blocked its result (fail-closed, ";
    private const string EventPrefix = "hook blocked its ";
    private const string EventSuffix = " event itself (fail-closed), not a tool call: ";
    private const string UnnamedPrefix = "hook blocked a hook event itself (fail-closed;";
    private const int ReasonLimit = 300;

    /// <summary>
    /// The SQL condition, over <c>action</c> and <c>details</c> of <c>audit_events</c>, that is true for a refusal row: what a count of policy blocks leaves
    /// out. Constant text; no row an older runtime writes matches it.
    /// </summary>
    public const string RefusalSql =
        "(action = '" + BlockAction + "' AND COALESCE(details, '') LIKE '%" + RefusalMark + "%' AND COALESCE(details, '') LIKE '%" + Tail + "')";

    /// <summary>Reads a row. Null for any row that is not one of these; never throws.</summary>
    /// <param name="action">The row's action.</param>
    /// <param name="details">The row's details text.</param>
    public static HookFailureRefusal? Parse(string? action, string? details)
    {
        var verb = action?.Trim() ?? string.Empty;
        var isBlock = string.Equals(verb, BlockAction, StringComparison.OrdinalIgnoreCase);
        var isWarn = string.Equals(verb, WarnAction, StringComparison.OrdinalIgnoreCase);
        if ((!isBlock && !isWarn) || string.IsNullOrEmpty(details))
        {
            return null;
        }

        var text = details.Trim();
        if (!text.EndsWith(Tail, StringComparison.Ordinal))
        {
            return null;
        }

        if (isWarn)
        {
            return text.StartsWith(UncheckedPrefix, StringComparison.Ordinal)
                ? new HookFailureRefusal(HookFailureKind.RanUnchecked, string.Empty, ReasonOf(text))
                : null;
        }

        if (text.StartsWith(CallPrefix, StringComparison.Ordinal))
        {
            return new HookFailureRefusal(HookFailureKind.CallRefused, EventUntil(text, CallPrefix.Length, ')'), ReasonOf(text));
        }

        if (text.StartsWith(ResultPrefix, StringComparison.Ordinal))
        {
            return new HookFailureRefusal(HookFailureKind.ResultRefused, EventUntil(text, ResultPrefix.Length, ')'), ReasonOf(text));
        }

        if (text.StartsWith(UnnamedPrefix, StringComparison.Ordinal))
        {
            return new HookFailureRefusal(HookFailureKind.UnnamedEventRefused, string.Empty, ReasonOf(text));
        }

        if (text.StartsWith(EventPrefix, StringComparison.Ordinal))
        {
            var end = text.IndexOf(EventSuffix, EventPrefix.Length, StringComparison.Ordinal);
            if (end > EventPrefix.Length)
            {
                var name = text[EventPrefix.Length..end];
                if (name.Length <= 64 && !name.Contains(' ', StringComparison.Ordinal))
                {
                    return new HookFailureRefusal(HookFailureKind.EventRefused, name, ReasonOf(text));
                }
            }
        }

        return null;
    }

    /// <summary>True for a row the hook refused itself (fail mode closed): a block no policy decided.</summary>
    public static bool IsRefusal(string? action, string? details) => Parse(action, details) is { IsRefusal: true };

    /// <summary>The event name the prefix opens, up to <paramref name="stop"/>; empty when it is not a plain word.</summary>
    private static string EventUntil(string text, int start, char stop)
    {
        var end = text.IndexOf(stop, start);
        if (end <= start || end - start > 64)
        {
            return string.Empty;
        }

        var name = text[start..end];
        return name.Contains(' ', StringComparison.Ordinal) ? string.Empty : name;
    }

    /// <summary>The Go-quoted reason before the tail: the first double quote opens it (no prefix holds one).</summary>
    private static string ReasonOf(string text)
    {
        var open = text.IndexOf('"', StringComparison.Ordinal);
        var tail = text.Length - Tail.Length;
        if (open < 0 || open >= tail)
        {
            return string.Empty;
        }

        var value = GoUnquote(text, open + 1, tail);
        return value.Length <= ReasonLimit ? value : value[..ReasonLimit];
    }

    /// <summary>The body of a Go <c>strconv.Quote</c> literal from <paramref name="start"/> to its closing quote (or <paramref name="end"/>).</summary>
    private static string GoUnquote(string text, int start, int end)
    {
        var value = new StringBuilder();
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (c == '"')
            {
                break;
            }

            if (c != '\\' || i + 1 >= end)
            {
                value.Append(c);
                i++;
                continue;
            }

            var e = text[i + 1];
            i += 2;
            switch (e)
            {
                case 'n': value.Append('\n'); break;
                case 't': value.Append('\t'); break;
                case 'r': value.Append('\r'); break;
                case 'a': value.Append('\a'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'v': value.Append('\v'); break;
                case 'x' when Hex(text, i, 2, end) is { } x: value.Append((char)x); i += 2; break;
                case 'u' when Hex(text, i, 4, end) is { } u: value.Append((char)u); i += 4; break;
                case 'U' when Hex(text, i, 8, end) is { } big && big <= 0x10FFFF:
                    value.Append(char.ConvertFromUtf32(big is >= 0xD800 and <= 0xDFFF ? 0xFFFD : big));
                    i += 8;
                    break;
                default: value.Append(e); break;
            }
        }

        // The reason is shown on one line.
        return value.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static int? Hex(string text, int start, int length, int end) =>
        start + length <= end && int.TryParse(text.AsSpan(start, length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
