using System.Text.Json;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// Why the rules decided a hook call because the LLM judge gave no verdict, as the hook's own audit row says it (upstream #1395). The gateway puts
/// <c>judge_fallback=&lt;reason&gt;</c> in the hook audit envelope's <c>extra</c> map (<c>hookJudgeFallbackExtraKey</c>, internal/gateway/agent_hook.go and
/// hook_decision_metrics_v8.go at tag 1.0.8), which lands twice on the <c>connector-hook</c> row: as <c>$.extra.judge_fallback</c> in <c>structured_json</c>, and
/// as a <c>judge_fallback=</c> pair in the details text's legacy tail (<c>renderHookAuditLegacyDetails</c>, hook_audit_envelope.go). 1.0.6 wrote it too;
/// 1.0.7 counted fallbacks in <c>/health</c> without writing it on the row; 1.0.8 writes it again. A row without it shows nothing.
/// </summary>
public static partial class HookJudgeFallback
{
    /// <summary>The envelope key.</summary>
    public const string Key = "judge_fallback";

    /// <summary>The label of the field the detail panes show.</summary>
    public const string FieldName = "Judge fallback";

    /// <summary>
    /// The reason word (<c>timeout</c>, <c>error</c>, <c>late</c>, <c>busy</c>, <c>budget</c>, or another the gateway may add), lower case; empty when the row
    /// has none. The structured envelope is preferred; the details text is read when there is no envelope (the Logs stream reads no <c>structured_json</c>).
    /// </summary>
    /// <param name="structured">The row's parsed <c>structured_json</c>, top-level keys; null when not read.</param>
    /// <param name="details">The row's details text; null when not read.</param>
    public static string Read(IReadOnlyDictionary<string, JsonElement>? structured, string? details)
    {
        if (structured is not null && structured.TryGetValue("extra", out var extra) && extra.ValueKind == JsonValueKind.Object &&
            extra.TryGetProperty(Key, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return Clean(value.GetString());
        }

        if (string.IsNullOrEmpty(details))
        {
            return string.Empty;
        }

        var match = DetailsPair().Match(LegacyPart(details));
        return match.Success ? Clean(match.Groups[1].Value) : string.Empty;
    }

    /// <summary>The reason in a few words, for the field's value (<c>timed out</c>).</summary>
    public static string Short(string reason) => reason switch
    {
        "timeout" => "timed out",
        "error" => "judge call failed",
        "late" => "answer came too late",
        "busy" => "judge at capacity",
        "budget" => "hook judge budget spent",
        _ => reason,
    };

    /// <summary>The reason in plain words, for the detail pane: what happened and that the rules decided.</summary>
    public static string Describe(string reason) => reason switch
    {
        "timeout" => "The judge did not answer in time (a wait for a free judge slot, guardrail.judge.max_parallel, counts too), so the rules decided this call.",
        "error" => "The judge call failed (a provider error, or an empty or unreadable answer), so the rules decided this call.",
        "late" => "The hook could not wait longer for the judge, so the rules decided this call; the judge's verdict is recorded when it arrives.",
        "busy" => "The hook-lane judge was at capacity, so the rules decided this call.",
        "budget" => "The hook's earlier judge calls used up its time budget, so the rules decided this call.",
        _ => $"The judge gave no verdict ({reason}), so the rules decided this call.",
    };

    /// <summary>The <c>key=value</c> part of a hook details text, before the quoted envelope copy (<c>details_json=</c>).</summary>
    internal static string LegacyPart(string details)
    {
        var json = details.IndexOf(" details_json=", StringComparison.Ordinal);
        return json < 0 ? details : details[..json];
    }

    /// <summary>A reason word kept as the gateway's: lower-case letters, digits and <c>_</c> / <c>-</c>, at most 32; anything else is dropped.</summary>
    private static string Clean(string? value)
    {
        var word = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return word.Length is > 0 and <= 32 && Word().IsMatch(word) ? word : string.Empty;
    }

    [GeneratedRegex(@"(?:^|\s)judge_fallback=(\S+)(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DetailsPair();

    [GeneratedRegex(@"^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
