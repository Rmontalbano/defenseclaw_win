using System.Text.Json;

namespace DefenseClaw.Core.Setup;

/// <summary>A row of a setup list that is not what the CLI documents (a missing name, a state that is not a boolean): the read fails, no row is guessed.</summary>
internal sealed class SetupListFormatException : Exception
{
    public SetupListFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reading what <c>defenseclaw setup &lt;noun&gt; list --json</c> and <c>show --json</c> print. The CLI can write a warning line before the
/// document, so every line that starts a JSON array (or object) is tried in turn, as <see cref="Cli.CredentialListParser"/> does; unlike it, an
/// empty output is <b>not</b> an empty list. These commands always print a document (<c>[]</c> for no rows), so nothing printed means the
/// command did not do its job, and a list that could not be read must never look like one with nothing in it.
/// </summary>
internal static class SetupJson
{
    /// <summary>Reads a JSON array of objects; <paramref name="read"/> turns each object into a row or throws <see cref="SetupListFormatException"/>.</summary>
    public static bool TryReadRows<T>(
        string? text,
        string what,
        Func<JsonElement, T> read,
        out IReadOnlyList<T> rows,
        out string error)
    {
        rows = Array.Empty<T>();
        error = string.Empty;

        var stripped = (text ?? string.Empty).Trim();
        if (stripped.Length == 0)
        {
            error = $"{what}: the command printed nothing, so the list is unknown (an empty list prints []).";
            return false;
        }

        string? lastError = null;
        foreach (var candidate in Candidates(stripped, '['))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    lastError = $"{what}: expected a list";
                    continue;
                }

                if (TryReadAll(document.RootElement, what, read, out var parsed, out var rowError))
                {
                    rows = parsed;
                    return true;
                }

                lastError = rowError;
            }
            catch (JsonException ex)
            {
                lastError = $"{what}: {ex.Message}";
            }
        }

        error = lastError ?? $"{what}: the output could not be read";
        return false;
    }

    private static bool TryReadAll<T>(JsonElement array, string what, Func<JsonElement, T> read, out List<T> parsed, out string error)
    {
        parsed = new List<T>();
        error = string.Empty;
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"{what}: entry {index} is not an object";
                return false;
            }

            try
            {
                parsed.Add(read(element));
            }
            catch (SetupListFormatException ex)
            {
                error = $"{what}: entry {index}: {ex.Message}";
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads one JSON object (what <c>setup webhook show --json</c> prints).</summary>
    public static bool TryReadObject<T>(string? text, string what, Func<JsonElement, T> read, out T? value, out string error)
        where T : class
    {
        value = null;
        error = string.Empty;

        var stripped = (text ?? string.Empty).Trim();
        if (stripped.Length == 0)
        {
            error = $"{what}: the command printed nothing";
            return false;
        }

        string? lastError = null;
        foreach (var candidate in Candidates(stripped, '{'))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    lastError = $"{what}: expected an object";
                    continue;
                }

                value = read(document.RootElement);
                return true;
            }
            catch (JsonException ex)
            {
                lastError = $"{what}: {ex.Message}";
            }
            catch (SetupListFormatException ex)
            {
                lastError = $"{what}: {ex.Message}";
            }
        }

        error = lastError ?? $"{what}: the output could not be read";
        return false;
    }

    private static IEnumerable<string> Candidates(string stripped, char opener)
    {
        if (stripped[0] == opener)
        {
            yield return stripped;
        }

        var marker = "\n" + opener;
        var from = 0;
        while (true)
        {
            var index = stripped.IndexOf(marker, from, StringComparison.Ordinal);
            if (index < 0)
            {
                yield break;
            }

            yield return stripped[(index + 1)..].Trim();
            from = index + 1;
        }
    }

    /// <summary>A string member, or empty when it is missing, null or not text.</summary>
    public static string StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>A string member that has to be there and say something.</summary>
    public static string RequiredText(JsonElement element, string name)
    {
        var value = StringOf(element, name).Trim();
        return value.Length > 0 ? value : throw new SetupListFormatException($"no \"{name}\"");
    }

    /// <summary>
    /// A name that has to be there, exactly as the CLI printed it: not trimmed, because a name is what a later command is given, and
    /// " a" is not "a". Whether it is fit to put on a command line is <see cref="SetupResourceArgv.IsSafeName"/>'s question.
    /// </summary>
    public static string RequiredName(JsonElement element, string name)
    {
        var value = StringOf(element, name);
        return value.Trim().Length > 0 ? value : throw new SetupListFormatException($"no \"{name}\"");
    }

    /// <summary>A boolean member that has to be there.</summary>
    public static bool RequiredFlag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new SetupListFormatException($"\"{name}\" is not true or false");

    /// <summary>A boolean member that may be missing (false), and is false when it is anything but a boolean true.</summary>
    public static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A whole-number member, or <paramref name="fallback"/> when it is missing or not a number.</summary>
    public static int Number(JsonElement element, string name, int fallback = 0) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    /// <summary>A whole-number member that may be null or missing (no value).</summary>
    public static int? OptionalNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    /// <summary>The strings of an array member; anything that is not text is left out.</summary>
    public static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static item => item.Length > 0)
            .ToArray();
    }
}
