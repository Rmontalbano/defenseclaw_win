using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// The checks for the config keys 1.0.8 added to the editor's reach: <c>guardrail.judge.extra_body</c> (the gateway's <c>JudgeConfig.ValidateExtraBody</c>,
/// <c>internal/config/config.go@1.0.8:2494-2529</c>, plus the schema's <c>maxProperties: 32</c>, <c>schemas/config/v8/defenseclaw-config.schema.json@1.0.8:1626-1631</c>)
/// and the base URL the <c>openai-compatible</c> and <c>custom-openai</c> providers need (<c>internal/gateway/provider.go@1.0.8:482-489</c>).
/// Nothing here runs unless the runtime has the matching capability: the caller (<see cref="ConfigFieldFindings"/>) is given the profile.
/// </summary>
public static partial class ConfigFieldValidator
{
    /// <summary>The dotted path of the judge's extra request fields.</summary>
    public const string ExtraBodyPath = "guardrail.judge.extra_body";

    /// <summary>The most fields <c>extra_body</c> may hold (the schema's <c>maxProperties</c>).</summary>
    public const int ExtraBodyMaxFields = 32;

    /// <summary>Names the judge owns, compared after lower-casing and dropping <c>_</c>, <c>-</c> and <c>.</c> (<c>ValidateExtraBody</c>'s first switch).</summary>
    private static readonly HashSet<string> ExtraBodyReserved = new(StringComparer.Ordinal)
    {
        "model", "messages", "tools", "toolchoice", "stream", "streamoptions",
        "maxtokens", "maxcompletiontokens", "maxoutputtokens", "responseformat",
        "temperature", "topp", "frequencypenalty", "presencepenalty", "stop",
        "fallbacks", "provider", "baseurl", "url", "endpoint",
    };

    /// <summary>A normalised name that contains any of these is refused as credential-like (<c>ValidateExtraBody</c>'s second test).</summary>
    private static readonly string[] ExtraBodyCredentialMarkers =
    {
        "auth", "credential", "token", "secret", "password", "header", "key", "url", "endpoint", "certificate",
    };

    /// <summary>
    /// Checks one field name of <c>guardrail.judge.extra_body</c> in the gateway's words: an error for an empty name, a character outside ASCII
    /// letters, digits, <c>_</c>, <c>-</c> and <c>.</c>, a name the judge owns, or one that looks like a credential, URL or header.
    /// </summary>
    public static FieldValidation ValidateExtraBodyKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length == 0 || key.Any(static c => !IsExtraBodyKeyCharacter(c)))
        {
            return Error($"{ExtraBodyPath}: invalid key \"{key}\" (use ASCII letters, digits, _, - or .)");
        }

        var normalized = key.ToLowerInvariant()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);

        return ExtraBodyReserved.Contains(normalized) || ExtraBodyCredentialMarkers.Any(m => normalized.Contains(m, StringComparison.Ordinal))
            ? Error($"{ExtraBodyPath}: key \"{key}\" is reserved")
            : FieldValidation.Ok;
    }

    /// <summary>
    /// Checks the names of <c>extra_body</c>'s fields: more than <see cref="ExtraBodyMaxFields"/> is an error, and so is the first name (in sorted
    /// order, as the gateway reports) that <see cref="ValidateExtraBodyKey"/> refuses.
    /// </summary>
    public static FieldValidation ValidateExtraBody(IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count > ExtraBodyMaxFields)
        {
            return Error($"{ExtraBodyPath}: at most {ExtraBodyMaxFields} fields are allowed (it has {keys.Count})");
        }

        foreach (var key in keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            var result = ValidateExtraBodyKey(key);
            if (!result.IsOk)
            {
                return result;
            }
        }

        return FieldValidation.Ok;
    }

    /// <summary>
    /// Checks the YAML node the key holds: nothing (absent, empty or null) is fine, a mapping is checked by its field names
    /// (<see cref="ValidateExtraBody"/>), and anything else (a text, a number, a list) is an error because the schema wants an object.
    /// </summary>
    public static FieldValidation ValidateExtraBodyNode(YamlNode? node)
    {
        switch (node)
        {
            case null:
                return FieldValidation.Ok;

            case YamlScalarNode scalar when string.IsNullOrWhiteSpace(scalar.Value) || scalar.Value is "null" or "~":
                return FieldValidation.Ok;

            case YamlMappingNode map:
                return ValidateExtraBody(map.Children.Select(static pair => (pair.Key as YamlScalarNode)?.Value ?? string.Empty).ToArray());

            default:
                return Error($"{ExtraBodyPath}: must be a mapping of field names to values");
        }
    }

    /// <summary>
    /// A warning for a block whose provider is <c>openai-compatible</c> or <c>custom-openai</c> and that has no base URL of its own nor one to inherit: the
    /// gateway answers <c>provider "x" requires base_url</c> and the provider is not built. Any other provider, or a blank one, is fine.
    /// </summary>
    /// <param name="provider">The block's <c>provider</c>.</param>
    /// <param name="baseUrl">The block's own <c>base_url</c>.</param>
    /// <param name="inheritedBaseUrl">The top-level <c>llm.base_url</c> the block falls back to (<c>Config.ResolveLLM</c>), or null when the block is the top-level one.</param>
    public static FieldValidation ValidateCompatibleProvider(string? provider, string? baseUrl, string? inheritedBaseUrl = null)
    {
        var name = (provider ?? string.Empty).Trim();
        if (!ConfigFieldCatalog.OpenAiCompatibleProviderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return FieldValidation.Ok;
        }

        return HasBaseUrl(baseUrl) || HasBaseUrl(inheritedBaseUrl)
            ? FieldValidation.Ok
            : Warning($"provider \"{name}\" requires base_url: set one in this block or in the top-level llm block, or the gateway cannot use this provider");
    }

    private static bool HasBaseUrl(string? value) => (value ?? string.Empty).Trim().TrimEnd('/').Length > 0;

    private static bool IsExtraBodyKeyCharacter(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.';
}
