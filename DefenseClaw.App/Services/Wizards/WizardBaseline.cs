using System.Globalization;
using DefenseClaw.Core.Config;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Pre-fills a wizard from the configuration it is about to change, so "Execute" without touching a
/// page is a no-op instead of a reset (S3).
/// <para>
/// <b>The bug this closes.</b> Every connector wizard used to start from the CLI's static defaults and
/// send them all: <c>--mode observe --no-local-stack --restart --yes</c>. For a connector that is in
/// <i>action</i> mode that is a silent downgrade to observe plus a gateway bounce. The pages now start
/// from <c>guardrail.connectors.&lt;name&gt;</c> in config.yaml, and only fields whose answer differs
/// from <see cref="WizardField.BaselineValue"/> reach argv (see <c>WizardDefinition.BuildArgvDefault</c>).
/// </para>
/// <para>
/// <b>The one field that needs care is <c>--mode</c>.</b> Its Click default is <c>observe</c> and it is
/// applied when the flag is omitted, so its baseline stays "observe" (what omission means) while its
/// starting answer is the stored mode. An action-mode connector therefore always sends
/// <c>--mode action</c>; an observe-mode one sends nothing.
/// </para>
/// <para>
/// Only <i>known-good</i> values are applied: a stored value that is not one of the flag's choices, or
/// not a number for a numeric flag, is left alone rather than forced into a control that cannot show it.
/// </para>
/// </summary>
public static class WizardBaseline
{
    /// <summary>What a configuration says about one flag, and whether that value is also "what omission leaves".</summary>
    private sealed record Fact(string Value, bool IsBaseline);

    public static WizardDefinition Apply(WizardDefinition definition, ConfigDocument config, bool configReadable)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(config);

        if (definition.Steps.Count == 0)
        {
            return definition;
        }

        var fields = definition.AllFields.Where(f => f.Flag is { Length: > 0 }).ToArray();
        var isHookConnector = fields.Any(f => f.Flag == "--mode") && fields.Any(f => f.Flag == "--fail-mode");

        if (isHookConnector)
        {
            return ApplyHookConnector(definition, config, configReadable);
        }

        if (!configReadable)
        {
            return definition;
        }

        var yaml = ConfigYaml.Parse(config.RawText);
        return definition.Target switch
        {
            "guardrail" => Replace(definition, GuardrailFacts(yaml), "config.yaml → guardrail"),
            "llm" => Replace(definition, LlmFacts(yaml), "config.yaml → llm"),
            _ => definition,
        };
    }

    // ---------------------------------------------------------------- hook connectors

    private static WizardDefinition ApplyHookConnector(WizardDefinition definition, ConfigDocument config, bool configReadable)
    {
        if (!configReadable)
        {
            return definition.With(
                definition.Steps,
                string.Empty,
                "config.yaml could not be read, so this wizard cannot see the connector's current mode or fail-mode. " +
                "The CLI resets --mode to observe when it is not given, which would downgrade a connector in action mode. " +
                "Fix the config first (Setup → Config editor), or set every field deliberately.");
        }

        var key = Normalize(definition.Target);
        var match = config.Config.Guardrail.Connectors
            .Where(pair => Normalize(pair.Key) == key)
            .Select(pair => (Name: pair.Key, Settings: pair.Value))
            .FirstOrDefault();

        if (match.Name is null)
        {
            return definition.With(
                definition.Steps,
                "This connector is not in config.yaml yet, so every field starts at the CLI's default.",
                string.Empty);
        }

        var yaml = ConfigYaml.Parse(config.RawText);
        var section = new[] { "guardrail", "connectors", match.Name };
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);

        if (NormalizeMode(match.Settings.Mode) is { } mode)
        {
            // Not a baseline: omitting --mode means "observe", whatever is stored.
            facts["--mode"] = new Fact(mode, IsBaseline: false);
        }

        if (NormalizeFailMode(match.Settings.HookFailMode) is { } failMode)
        {
            facts["--fail-mode"] = new Fact(failMode, IsBaseline: true);
        }

        facts["--block-message"] = new Fact(match.Settings.BlockMessage ?? string.Empty, IsBaseline: true);
        facts["--rule-pack-dir"] = new Fact(match.Settings.RulePackDir ?? string.Empty, IsBaseline: true);

        if (yaml.Get(section.Append("hilt").Append("enabled").ToArray()) is { } hilt && TryBool(hilt) is { } hiltOn)
        {
            facts["--human-approval"] = new Fact(hiltOn ? ToggleValues.On : ToggleValues.Off, IsBaseline: true);
        }

        if (yaml.Get(section.Append("hilt").Append("min_severity").ToArray()) is { Length: > 0 } severity)
        {
            facts["--hilt-min-severity"] = new Fact(severity, IsBaseline: true);
        }

        var source = "config.yaml → guardrail.connectors." + match.Name;
        var note = "Pre-filled from " + source + ". Only fields you change are sent. --mode is sent whenever it is not the CLI's " +
                   "default (observe), because leaving it off would reset the connector to observe.";

        return Replace(definition, facts, source, note);
    }

    // ---------------------------------------------------------------- other targets

    private static Dictionary<string, Fact> GuardrailFacts(ConfigYaml yaml)
    {
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        Add(facts, "--connector", yaml.Get("guardrail", "connector"));
        Add(facts, "--scanner-mode", yaml.Get("guardrail", "scanner_mode"));
        Add(facts, "--mode", yaml.Get("guardrail", "mode"));
        Add(facts, "--port", yaml.Get("guardrail", "port"));
        Add(facts, "--block-message", yaml.Get("guardrail", "block_message"));
        Add(facts, "--detection-strategy", yaml.Get("guardrail", "detection_strategy"));
        Add(facts, "--hilt-min-severity", yaml.Get("guardrail", "hilt", "min_severity"));

        if (yaml.Get("guardrail", "hilt", "enabled") is { } hilt && TryBool(hilt) is { } on)
        {
            facts["--human-approval"] = new Fact(on ? ToggleValues.On : ToggleValues.Off, IsBaseline: true);
        }

        return facts;
    }

    private static Dictionary<string, Fact> LlmFacts(ConfigYaml yaml)
    {
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        Add(facts, "--provider", yaml.Get("llm", "provider"));
        Add(facts, "--model", yaml.Get("llm", "model"));
        Add(facts, "--api-key-env", yaml.Get("llm", "api_key_env"));
        Add(facts, "--base-url", yaml.Get("llm", "base_url"));
        Add(facts, "--timeout", yaml.Get("llm", "timeout"));
        Add(facts, "--max-retries", yaml.Get("llm", "max_retries"));
        Add(facts, "--region", yaml.Get("llm", "region"));
        return facts;
    }

    private static void Add(Dictionary<string, Fact> facts, string flag, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts[flag] = new Fact(value.Trim(), IsBaseline: true);
        }
    }

    // ---------------------------------------------------------------- applying facts

    private static WizardDefinition Replace(WizardDefinition definition, Dictionary<string, Fact> facts, string source, string? note = null)
    {
        if (facts.Count == 0)
        {
            return definition;
        }

        var applied = 0;
        var steps = new List<WizardStep>(definition.Steps.Count);

        foreach (var step in definition.Steps)
        {
            var fields = new List<WizardField>(step.Fields.Count);
            foreach (var field in step.Fields)
            {
                if (field.Flag is { Length: > 0 } flag && !field.IsPositional && facts.TryGetValue(flag, out var fact) &&
                    Coerce(field, fact.Value) is { } value)
                {
                    fields.Add(field.WithAnswers(value, fact.IsBaseline ? value : field.BaselineValue, source));
                    applied++;
                }
                else
                {
                    fields.Add(field);
                }
            }

            steps.Add(new WizardStep
            {
                Id = step.Id,
                Title = step.Title,
                Subtitle = step.Subtitle,
                Fields = fields,
                VisibleWhenFieldId = step.VisibleWhenFieldId,
                VisibleWhenValues = step.VisibleWhenValues,
                Guide = step.Guide,
            });
        }

        if (applied == 0)
        {
            return definition;
        }

        return definition.With(
            steps,
            note ?? $"Pre-filled from {source}. Only fields you change are sent.",
            string.Empty);
    }

    /// <summary>
    /// Fits a stored value to what the control can show, or returns null when it cannot (a choice the
    /// flag does not list, a non-number for a numeric flag). A value that does not fit is left alone.
    /// </summary>
    private static string? Coerce(WizardField field, string value)
    {
        switch (field.Kind)
        {
            case WizardFieldKind.Choice:
                return field.Choices
                    .FirstOrDefault(c => c.Value.Length > 0 && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))
                    ?.Value;

            case WizardFieldKind.Toggle:
                return TryBool(value) is { } on ? (on ? ToggleValues.On : ToggleValues.Off) : null;

            case WizardFieldKind.Integer:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? value : null;

            case WizardFieldKind.Number:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value : null;

            case WizardFieldKind.Text:
            case WizardFieldKind.Path:
            case WizardFieldKind.EnvVarName:
                return value;

            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- normalisation

    /// <summary><c>claude-code</c> (the CLI target) and <c>claudecode</c> (the config key) are one connector.</summary>
    private static string Normalize(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// config.yaml says <c>observe</c> or <c>action</c>; the model's own doc comment also mentions
    /// <c>enforce</c>, so that maps to <c>action</c> rather than being dropped.
    /// </summary>
    private static string? NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "observe" => "observe",
        "action" or "enforce" or "enforcement" => "action",
        _ => null,
    };

    private static string? NormalizeFailMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "open" => "open",
        "closed" => "closed",
        _ => null,
    };

    private static bool? TryBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

    // ---------------------------------------------------------------- a tiny read-only YAML walker

    /// <summary>
    /// Read-only lookup into config.yaml by key path. The typed model only carries a few connector
    /// fields; this reads the rest (HILT, the <c>llm:</c> block) without widening a Core type that
    /// belongs to another group. Never throws: an unreadable document is an empty one.
    /// </summary>
    private sealed class ConfigYaml
    {
        private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

        private readonly IDictionary<object, object>? _root;

        private ConfigYaml(IDictionary<object, object>? root)
        {
            _root = root;
        }

        public static ConfigYaml Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ConfigYaml(null);
            }

            try
            {
                return new ConfigYaml(Deserializer.Deserialize<object?>(text) as IDictionary<object, object>);
            }
            catch (YamlException)
            {
                return new ConfigYaml(null);
            }
        }

        /// <summary>The scalar at <paramref name="path"/> as text, or null when absent or not a scalar.</summary>
        public string? Get(params string[] path)
        {
            object? node = _root;
            foreach (var segment in path)
            {
                if (node is not IDictionary<object, object> map)
                {
                    return null;
                }

                object? next = null;
                foreach (var pair in map)
                {
                    if (string.Equals(Convert.ToString(pair.Key, CultureInfo.InvariantCulture), segment, StringComparison.OrdinalIgnoreCase))
                    {
                        next = pair.Value;
                        break;
                    }
                }

                node = next;
            }

            return node is null or IDictionary<object, object> or System.Collections.IEnumerable and not string
                ? null
                : Convert.ToString(node, CultureInfo.InvariantCulture);
        }
    }
}
