using System.Text.Json;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Policy;

/// <summary>
/// The output of <c>defenseclaw guardrail cascade explain --json</c> (runtime 1.0.10), read into plain lines in the runtime's own terms. The shape is
/// <c>{"cascade": {connector, valid, invalid{error, fallback, weaker[]}, ignored[{path, reason}], rules{mode, pack, tier, block_at, alert_at, hilt},
/// system_one{runs, protocol}, judge{runs, max_action, no_verdict}, unknown{setting, action}, lanes[{name, mode, default, shadow_only}],
/// policy{active, lowest, highest}, critical_floor, hook_fail_mode}}</c> (policy_cascade.go and cascade_explain.go at 1.0.10, read through
/// cmd_guardrail.py@1.0.10:6751-6788).
/// <para>
/// <b>Bounded.</b> Input over <see cref="MaxInputChars"/> is refused, every string is cut to <see cref="MaxStringChars"/> and made fit to draw, and at
/// most <see cref="MaxEntries"/> lanes, ignored values and weaker settings are read. The text is only drawn, never executed. Any shape problem (not JSON,
/// no <c>cascade</c> object, no boolean <c>valid</c>, a section of the wrong type, a word that is not a string) makes <see cref="Parse"/> return null,
/// and the caller says the output could not be read and keeps the config.yaml lines.
/// </para>
/// </summary>
public sealed class GuardrailCascadeExplain
{
    /// <summary>Input longer than this (256 KiB) is refused.</summary>
    public const int MaxInputChars = 256 * 1024;

    /// <summary>Every string is cut to this many characters.</summary>
    public const int MaxStringChars = 200;

    /// <summary>The most lanes, ignored values and weaker settings read.</summary>
    public const int MaxEntries = 64;

    private GuardrailCascadeExplain(bool valid, string connector, IReadOnlyList<string> lines)
    {
        Valid = valid;
        Connector = connector;
        Lines = lines;
    }

    /// <summary>The cascade is valid; false when the gateway fell back to an earlier cascade or the rule pack defaults.</summary>
    public bool Valid { get; }

    /// <summary>The connector the explanation is for; empty for the global scope.</summary>
    public string Connector { get; }

    /// <summary>The lines to draw, in the runtime's terms.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The explanation in <paramref name="json"/>; null when it is too long, not JSON or not the shape above.</summary>
    public static GuardrailCascadeExplain? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxInputChars)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return Read(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static GuardrailCascadeExplain? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cascade", out var cascade) || cascade.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!cascade.TryGetProperty("valid", out var validElement) || validElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        var valid = validElement.GetBoolean();
        var connector = Str(cascade, "connector");
        var lines = new List<string> { "Scope: " + (connector.Length > 0 ? connector : "global default") };

        var invalid = Section(cascade, "invalid");
        if (invalid is { } bad)
        {
            var fallback = Str(bad, "fallback");
            var decides = fallback switch
            {
                "last_known_good" => "the last-known-good cascade",
                "pack_defaults" => "the rule pack defaults",
                "" => "an earlier configuration",
                _ => fallback,
            };
            var error = Str(bad, "error");
            lines.Add($"Invalid: {(error.Length > 0 ? error : "the cascade cannot be applied")}; the gateway decides with {decides}");
            var weaker = Strings(bad, "weaker");
            if (weaker.Count > 0)
            {
                lines.Add("This is weaker than what the configuration says: " + string.Join("; ", weaker));
            }
        }
        else if (!valid)
        {
            lines.Add("Invalid: the cascade cannot be applied");
        }

        var rules = Section(cascade, "rules") ?? throw new FormatException("rules");
        lines.Add("Rules: " + RulesText(rules));

        if (Section(cascade, "system_one") is { } systemOne)
        {
            lines.Add("System One: " + SystemOneText(systemOne));
        }

        if (Section(cascade, "judge") is { } judge)
        {
            lines.AddRange(JudgeLines(judge));
        }

        if (Section(cascade, "unknown") is { } unknown)
        {
            var action = Str(unknown, "action");
            var setting = Str(unknown, "setting");
            if (action.Length > 0 || setting.Length > 0)
            {
                lines.Add($"Unknown tier: {Or(action, "?")} ({Or(setting, "?")})");
            }
        }

        if (Section(cascade, "policy") is { } policy)
        {
            lines.Add("Rego policy: " + PolicyText(policy));
        }

        var floor = Str(cascade, "critical_floor");
        if (floor.Length > 0)
        {
            lines.Add("CRITICAL floor: " + floor);
        }

        var lanes = LanesText(cascade);
        if (lanes.Length > 0)
        {
            lines.Add("Lanes: " + lanes);
        }

        var failMode = Str(cascade, "hook_fail_mode");
        if (failMode.Length > 0)
        {
            lines.Add("Hook fail mode: " + failMode);
        }

        foreach (var entry in Entries(cascade, "ignored"))
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("ignored");
            }

            var path = Str(entry, "path");
            var reason = Str(entry, "reason");
            if (path.Length > 0 || reason.Length > 0)
            {
                lines.Add($"Not applied: {Or(path, "?")}: {Or(reason, "no reason given")}");
            }
        }

        return new GuardrailCascadeExplain(valid, connector, lines);
    }

    private static string RulesText(JsonElement rules)
    {
        var parts = new List<string>();
        var pack = Str(rules, "pack");
        var mode = Str(rules, "mode");
        var blockAt = Str(rules, "block_at");
        var alertAt = Str(rules, "alert_at");
        if (pack.Length > 0)
        {
            parts.Add(pack + " pack");
        }

        if (mode.Length > 0)
        {
            parts.Add(mode + " mode");
        }

        if (blockAt.Length > 0)
        {
            parts.Add("block at " + blockAt);
        }

        if (alertAt.Length > 0)
        {
            parts.Add("alert at " + alertAt);
        }

        if (Flag(rules, "hilt") == true)
        {
            parts.Add("asks a human (hilt)");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "not reported";
    }

    private static string SystemOneText(JsonElement systemOne)
    {
        var runs = Str(systemOne, "runs");
        var text = runs switch
        {
            "off" => "off",
            "with_judge" => "runs with the judge",
            "alone" => "runs alone",
            "" => "not reported",
            _ => runs,
        };
        var protocol = Str(systemOne, "protocol");
        return runs is "off" or "" || protocol.Length == 0 ? text : $"{text} (protocol {protocol})";
    }

    private static IEnumerable<string> JudgeLines(JsonElement judge)
    {
        var runs = Str(judge, "runs");
        var judgeText = runs switch
        {
            "off" => "off",
            "escalation" => "on escalation",
            "always" => "always",
            "" => "not reported",
            _ => runs,
        };
        yield return "LLM judge: " + judgeText;

        var max = Str(judge, "max_action");
        var noVerdict = Str(judge, "no_verdict");
        if (max.Length > 0 || noVerdict.Length > 0)
        {
            yield return $"  can at most: {Or(max, "?")}; no verdict: {Or(noVerdict, "?")}";
        }
    }

    private static string PolicyText(JsonElement policy)
    {
        if (Flag(policy, "active") != true)
        {
            return "off";
        }

        var lowest = Str(policy, "lowest");
        var highest = Str(policy, "highest");
        return lowest.Length > 0 || highest.Length > 0 ? $"on (lowest {Or(lowest, "?")}, highest {Or(highest, "?")})" : "on";
    }

    private static string LanesText(JsonElement cascade)
    {
        var parts = new List<string>();
        foreach (var lane in Entries(cascade, "lanes"))
        {
            if (lane.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("lanes");
            }

            var name = Str(lane, "name");
            if (name.Length == 0)
            {
                continue;
            }

            var mode = Str(lane, "mode");
            var text = $"{name} {Or(mode, "?")}";
            if (Str(lane, "shadow_only").Length > 0)
            {
                text += " (shadow only)";
            }

            parts.Add(text);
        }

        return string.Join(", ", parts);
    }

    // ---- JSON helpers: strict about types, bounded in size ----------------------------------------------------------------------

    /// <summary>The object at <paramref name="name"/>; null when absent or null; a shape problem when it is anything else.</summary>
    private static JsonElement? Section(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Object ? value : throw new FormatException(name);
    }

    /// <summary>The string at <paramref name="name"/>, cut and made fit to draw; empty when absent or null; a shape problem when it is not a string.</summary>
    private static string Str(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException(name);
        }

        var visible = DisplayNames.Visible((value.GetString() ?? string.Empty).Trim());
        return visible.Length <= MaxStringChars ? visible : visible[..MaxStringChars];
    }

    private static bool? Flag(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new FormatException(name);
    }

    /// <summary>The first <see cref="MaxEntries"/> items of the array at <paramref name="name"/>; none when absent or null; a shape problem when it is not an array.</summary>
    private static List<JsonElement> Entries(JsonElement parent, string name)
    {
        var items = new List<JsonElement>();
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return items;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException(name);
        }

        foreach (var item in value.EnumerateArray())
        {
            if (items.Count == MaxEntries)
            {
                break;
            }

            items.Add(item);
        }

        return items;
    }

    private static List<string> Strings(JsonElement parent, string name)
    {
        var list = new List<string>();
        foreach (var item in Entries(parent, name))
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new FormatException(name);
            }

            var visible = DisplayNames.Visible((item.GetString() ?? string.Empty).Trim());
            list.Add(visible.Length <= MaxStringChars ? visible : visible[..MaxStringChars]);
        }

        return list;
    }

    private static string Or(string text, string fallback) => text.Length > 0 ? text : fallback;

    /// <summary>The CLI's own failure, as one bounded line: <c>Could not run defenseclaw guardrail cascade explain: ...</c>.</summary>
    public static string ErrorLine(string? reason)
    {
        var first = (reason ?? string.Empty).Split('\n', 2)[0].Trim();
        var visible = DisplayNames.Visible(first);
        if (visible.Length > MaxStringChars)
        {
            visible = visible[..MaxStringChars];
        }

        return "Could not run defenseclaw guardrail cascade explain: " + (visible.Length > 0 ? visible : "no reason given");
    }

    /// <summary>The line shown when the command answered but its output was not the shape this app reads.</summary>
    public const string UnreadableLine = "The output of defenseclaw guardrail cascade explain could not be read; the lines above are from config.yaml.";
}
