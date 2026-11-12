using System;
using System.Collections.Generic;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// What the form says about a config the runtime's newer keys make wrong, read from the whole document rather than from one field: a
/// <c>guardrail.judge.extra_body</c> the gateway would refuse, an <c>openai-compatible</c> or <c>custom-openai</c> provider with no base URL, and a
/// <c>openshell.middleware.enabled</c> that does nothing. The form edits a mapping as separate fields, and a provider's base URL is a sibling key, so
/// neither can be judged from one field's value; the builder puts these lines with its other warnings. Each check runs only when the profile says the
/// runtime has the key (<see cref="ConfigChoiceProfile.JudgeExtraBody"/>, <see cref="ConfigChoiceProfile.OpenAiCompatibleProviders"/>,
/// <see cref="ConfigChoiceProfile.OpenShellMiddlewareRetired"/>); for any other profile the answer is an empty list.
/// </summary>
public static class ConfigFieldFindings
{
    /// <summary>The LLM blocks that carry a <c>provider</c> and a <c>base_url</c>; every one but <c>llm</c> falls back to <c>llm.base_url</c> (<c>Config.ResolveLLM</c>, <c>config.go@1.0.8:1266-1308</c>).</summary>
    private static readonly string[][] LlmBlocks =
    {
        new[] { "guardrail", "llm" },
        new[] { "guardrail", "judge", "llm" },
        new[] { "scanners", "skill_scanner", "llm" },
        new[] { "scanners", "mcp_scanner", "llm" },
        new[] { "scanners", "plugin_llm" },
    };

    /// <summary>The warning lines for <paramref name="root"/> (the parsed <c>config show --source</c> mapping), in a fixed order.</summary>
    public static IReadOnlyList<string> For(YamlMappingNode root, ConfigChoiceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(profile);

        var lines = new List<string>();

        if (profile.JudgeExtraBody &&
            ConfigFieldValidator.ValidateExtraBodyNode(Find(root, "guardrail", "judge", "extra_body")) is { IsOk: false } extra)
        {
            lines.Add(extra.Message + ".");
        }

        if (profile.OpenAiCompatibleProviders)
        {
            var inherited = Text(Find(root, "llm", "base_url"));
            AddProviderWarning(lines, "llm", Text(Find(root, "llm", "provider")), Text(Find(root, "llm", "base_url")), null);
            foreach (var block in LlmBlocks)
            {
                AddProviderWarning(lines, string.Join('.', block), Text(Find(root, [.. block, "provider"])), Text(Find(root, [.. block, "base_url"])), inherited);
            }
        }

        // openshell.middleware.enabled (retired in 1.0.8) is not a finding: whatever its value, the form shows it read-only with the
        // neutral "Retired in 1.0.8" note (ConfigFieldCatalog), and a warning here would also turn off the catalogue layout.

        return lines;
    }

    private static void AddProviderWarning(List<string> lines, string block, string? provider, string? baseUrl, string? inherited)
    {
        if (ConfigFieldValidator.ValidateCompatibleProvider(provider, baseUrl, inherited) is { IsOk: false } result)
        {
            lines.Add($"{block}: {result.Message}.");
        }
    }

    private static string? Text(YamlNode? node) => (node as YamlScalarNode)?.Value;

    /// <summary>The node at <paramref name="path"/> below <paramref name="root"/>, or null when any step is missing or is not a mapping.</summary>
    private static YamlNode? Find(YamlMappingNode root, params string[] path)
    {
        YamlNode current = root;
        foreach (var segment in path)
        {
            YamlNode? next = null;
            if (current is YamlMappingNode map)
            {
                foreach (var pair in map.Children)
                {
                    if (pair.Key is YamlScalarNode key && string.Equals(key.Value, segment, StringComparison.Ordinal))
                    {
                        next = pair.Value;
                        break;
                    }
                }
            }

            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}
