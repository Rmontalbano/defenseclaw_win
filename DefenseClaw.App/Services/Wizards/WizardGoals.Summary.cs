using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Services.Wizards;

public static partial class WizardGoals
{
    /// <summary>
    /// One line on the goal page that says where things stand today (the TUI's <c>wizard_state_summary</c>): "Main: anthropic/claude-sonnet-4-5 ·
    /// Judge: not set". Read from config.yaml, which the wizard is about to change; empty for a wizard with nothing useful to say, or a config that
    /// could not be read.
    /// </summary>
    public static string StateSummary(WizardDefinition definition, ConfigDocument config, bool configReadable)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(config);

        if (!configReadable)
        {
            return string.Empty;
        }

        var yaml = WizardBaseline.ConfigYaml.Parse(config.RawText);
        string Value(params string[] path) => yaml.Get(path)?.Trim() is { Length: > 0 } text ? text : string.Empty;

        switch (definition.Target)
        {
            case "llm":
            {
                var provider = Value("llm", "provider");
                var model = Value("llm", "model");
                var main = provider.Length > 0 && model.Length > 0 ? $"{provider}/{model}" : model.Length > 0 ? model : provider.Length > 0 ? provider : "not set";
                var judge = Value("guardrail", "judge", "llm", "model");
                if (judge.Length == 0)
                {
                    judge = Value("guardrail", "judge", "model");
                }

                return $"Main: {main}  ·  Judge: {(judge.Length > 0 ? judge : "not set")}";
            }

            case "guardrail":
            {
                var enabled = Value("guardrail", "enabled");
                var on = string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase) ? "on" : "off";
                var mode = Value("guardrail", "mode");
                var strategy = Value("guardrail", "detection_strategy");
                var roster = GuardrailScopeContext.From(config, mode).Describe();
                return $"Guardrail: {on}  ·  Mode: {(mode.Length > 0 ? mode : "observe")}  ·  Strategy: {(strategy.Length > 0 ? strategy : "regex_only")}  ·  " +
                       $"Connectors: {(roster.Length > 0 ? roster : "none")}";
            }
        }

        // A hook connector: its own entry under guardrail.connectors, if it has one.
        var name = GuardrailScopeContext.Normalize(definition.Target);
        var entry = config.Config.Guardrail.Connectors
            .Where(pair => string.Equals(GuardrailScopeContext.Normalize(pair.Key), name, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .FirstOrDefault();

        if (!definition.AllFields.Any(f => string.Equals(f.Flag, "--fail-mode", StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        return entry is null
            ? $"{definition.Title} is not in config.yaml yet, so it starts from the CLI's defaults."
            : $"{definition.Title}: mode {(string.IsNullOrWhiteSpace(entry.Mode) ? "observe" : entry.Mode.Trim())}  ·  " +
              $"hook fail mode {(string.IsNullOrWhiteSpace(entry.HookFailMode) ? "not set" : entry.HookFailMode.Trim())}";
    }
}
