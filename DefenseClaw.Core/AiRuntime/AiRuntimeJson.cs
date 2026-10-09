using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.AiRuntime;

/// <summary>
/// Field readers shared by the runtime-plane readers. Every read is lenient (a field of the wrong kind is its zero value) and every
/// string that reaches the screen has passed through here, so there is one place that decides what "safe to show" means.
/// </summary>
internal static class AiRuntimeJson
{
    /// <summary>Longest identifier shown whole (process, user, agent, host, id).</summary>
    public const int NameLimit = 256;

    /// <summary>Longest reason, detail or title kept, after masking.</summary>
    public const int TextLimit = 1024;

    /// <summary>The string member, or empty when it is missing or not a string.</summary>
    public static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>An identifier: trimmed, cut, and shown on one line with its control and format characters spelled out.</summary>
    public static string Name(JsonElement element, string name)
    {
        var text = Str(element, name).Trim();
        if (text.Length > NameLimit)
        {
            text = text[..NameLimit];
        }

        return DisplayNames.Visible(text);
    }

    /// <summary>The strings of an array member, cleaned, at most <paramref name="max"/> of them.</summary>
    public static IReadOnlyList<string> Names(JsonElement element, string name, int max)
    {
        var list = new List<string>();
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && list.Count < max && Clean(item.GetString(), NameLimit) is { Length: > 0 } text)
                {
                    list.Add(text);
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Free text: credentials masked, then cut, then on one line. Masking comes first, so a credential that straddles the limit is
    /// never half shown; the one-line step is for text that came from a host (a command line, a process name inside a detail).
    /// </summary>
    public static string Clean(string? text, int limit) =>
        DisplayNames.Visible(DisplayRedaction.Text(text, limit).Trim());

    /// <summary>
    /// <see cref="Clean"/> for a sentence the runtime wrote for a person - a plane's reason, a degraded line, a "how to grant it". The same
    /// masking except for <c>token to</c>: see <see cref="DisplayRedaction.Prose"/>. Reasons are the text this panel exists to show whole, and
    /// "an elevated token to read the Security channel" must not come out with a word blanked.
    /// </summary>
    public static string Prose(string? text, int limit) =>
        DisplayNames.Visible(DisplayRedaction.Prose(text, limit).Trim());

    public static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>True, false, or null when the member is missing or not a boolean (JSON null included).</summary>
    public static bool? TriState(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    public static int Int(JsonElement element, string name) =>
        (int)Math.Clamp(Long(element, name), int.MinValue, int.MaxValue);

    public static long Long(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        if (value.TryGetInt64(out var whole))
        {
            return whole;
        }

        return value.TryGetDouble(out var real) && double.IsFinite(real)
            ? (long)Math.Clamp(real, long.MinValue, long.MaxValue)
            : 0;
    }

    public static double Double(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var real) && double.IsFinite(real)
            ? real
            : 0;

    /// <summary>An RFC 3339 time as UTC, or null when it is empty or not a time.</summary>
    public static DateTimeOffset? ParseTime(string text) =>
        text.Length > 0 &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
