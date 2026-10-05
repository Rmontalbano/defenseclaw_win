using System.Text.Json;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// One row of <c>defenseclaw keys list --json</c> (0.8.10 <c>cmd_keys._status_to_dict</c>). The command only ever prints
/// the NAME of a variable and whether it is set, never a value - a value appears only under <c>--show-values</c>, which this
/// app never passes, and which <see cref="CredentialRow"/> has no member for.
/// </summary>
/// <param name="EnvName">The variable the CLI resolved (an alias of <paramref name="CanonicalEnvName"/> may be the one in use).</param>
/// <param name="CanonicalEnvName">The registry's name for the credential; empty when the CLI did not say.</param>
/// <param name="Feature">What uses it: "Cisco AI Defense", "LLM judge", …</param>
/// <param name="Requirement"><c>required</c>, <c>optional</c> or <c>not_used</c> (lower-cased, as the TUI reads it).</param>
/// <param name="Source"><c>env</c>, <c>dotenv</c> or <c>unset</c>.</param>
/// <param name="IsSet">True when the variable has a value.</param>
/// <param name="Description">What the credential is for, in the registry's words.</param>
public sealed record CredentialRow(
    string EnvName,
    string CanonicalEnvName,
    string Feature,
    string Requirement,
    string Source,
    bool IsSet,
    string Description)
{
    public const string Required = "required";

    /// <summary>The TUI's <c>missing_credential_rows</c>: required by the current config and not set.</summary>
    public bool IsMissingRequired => string.Equals(Requirement, Required, StringComparison.OrdinalIgnoreCase) && !IsSet;
}

/// <summary>
/// Reads <c>keys list --json</c> the way the TUI's <c>parse_credential_rows</c> does, so the two agree on the
/// missing-required count. The CLI can print a banner or a warning line before the array (the TUI cuts at the first
/// line that starts <c>[</c>); this is stricter about it: every candidate start is tried in order, so a banner that
/// itself begins with <c>[</c> (<c>[warn] …</c>) does not hide the real array.
/// </summary>
public static class CredentialListParser
{
    /// <summary>Parses <paramref name="text"/>. Empty output is no rows, not an error (the TUI's behaviour).</summary>
    /// <param name="text">The command's stdout.</param>
    /// <param name="rows">The rows; empty when it could not be read.</param>
    /// <param name="error">Why it could not be read; empty on success.</param>
    public static bool TryParse(string? text, out IReadOnlyList<CredentialRow> rows, out string error)
    {
        rows = Array.Empty<CredentialRow>();
        error = string.Empty;

        var stripped = (text ?? string.Empty).Trim();
        if (stripped.Length == 0)
        {
            return true;
        }

        string? lastError = null;
        foreach (var candidate in Candidates(stripped))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    lastError = "credential JSON must be a list";
                    continue;
                }

                var parsed = new List<CredentialRow>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        parsed.Add(FromObject(element));
                    }
                }

                rows = parsed;
                return true;
            }
            catch (JsonException ex)
            {
                lastError = ex.Message;
            }
        }

        error = lastError ?? "credential JSON could not be read";
        return false;
    }

    private static IEnumerable<string> Candidates(string stripped)
    {
        if (stripped.StartsWith('['))
        {
            yield return stripped;
        }

        var from = 0;
        while (true)
        {
            var index = stripped.IndexOf("\n[", from, StringComparison.Ordinal);
            if (index < 0)
            {
                yield break;
            }

            yield return stripped[(index + 1)..].Trim();
            from = index + 1;
        }
    }

    private static CredentialRow FromObject(JsonElement element) => new(
        Text(element, "env_name", "EnvName"),
        Text(element, "canonical_env_name", "CanonicalEnvName"),
        Text(element, "feature", "Feature"),
        Text(element, "requirement", "Requirement").Trim().ToLowerInvariant(),
        Text(element, "source", "Source").Trim(),
        Flag(element, "set", "Set"),
        Text(element, "description", "Description"));

    private static string Text(JsonElement element, string name, string pascalName)
    {
        foreach (var key in new[] { name, pascalName })
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
            }
        }

        return string.Empty;
    }

    private static bool Flag(JsonElement element, string name, string pascalName)
    {
        // The TUI reads `set` when the key exists (even if false) and only then falls back to `Set`.
        var key = element.TryGetProperty(name, out _) ? name : pascalName;
        if (!element.TryGetProperty(key, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
            JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
            _ => false,
        };
    }
}
