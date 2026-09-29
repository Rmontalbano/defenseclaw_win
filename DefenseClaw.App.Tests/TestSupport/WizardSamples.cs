using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Click help screens shaped like the real 0.8.10 ones, and the same pipeline <c>WizardCatalog</c> runs on
/// them (parse, curate, Windows policy, secret routes) — minus the CLI probe, which is what makes it testable.
/// The screens are held as LF whatever the source file was checked out with (see <see cref="LineEndings"/>);
/// <see cref="LineEndings.With"/> turns one into the CRLF text the real CLI pipes.
/// </summary>
internal static class WizardSamples
{
    public static readonly string ClaudeCodeHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup claude-code [OPTIONS]

          Configure DefenseClaw hooks for Claude Code.

          Writes the hook entries into Claude Code's settings.json and records the
          connector policy in config.yaml. Run with --yes for unattended setup.

        Options:
          --mode [observe|action]         Hook policy mode.  [default: observe]
          --fail-mode [open|closed]       What the hook does when the gateway is
                                          unreachable.
          --human-approval / --no-human-approval
                                          Ask for human approval on findings.
          --hilt-min-severity [LOW|MEDIUM|HIGH|CRITICAL]
                                          Lowest severity that needs approval.
          --block-message TEXT            Message shown to the agent when a call is
                                          blocked (pass "" to clear).
          --rule-pack TEXT                Rule pack for this connector.
          --rule-pack-dir DIRECTORY       Rule pack directory (empty = inherit).
          --enable-judge / --no-enable-judge
                                          Per-connector LLM judge override.
          --judge-hook-connectors TEXT    Connectors whose hooks call the judge
                                          (repeatable).
          --replace / --no-replace        Replace hooks already installed by other
                                          tools.  [default: no-replace]
          --workspace TEXT                Workspace-scoped settings file.
          --with-local-stack / --no-local-stack
                                          Also bring up the local Docker stack.
                                          [default: no-local-stack]
          --restart / --no-restart        Restart the gateway to pick up the change.
                                          [default: restart]
          -y, --yes                       Do not prompt.
          --help                          Show this message and exit.
        """);

    public static readonly string LlmHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup llm [OPTIONS]

          Configure the unified LLM provider block.

        Options:
          --provider TEXT       Provider name.
          --model TEXT          Model id.
          --api-key TEXT        API key (stored by name, never on the command line).
          --api-key-env TEXT    Environment variable that holds the API key.
          --base-url TEXT       Provider base URL.
          --timeout INTEGER     Request timeout in seconds.
          --max-retries INTEGER
                                Retries on failure.
          --region TEXT         Cloud region.
          --non-interactive     Never prompt.
          --help                Show this message and exit.
        """);

    public static readonly string ObservabilityHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup observability [OPTIONS] COMMAND [ARGS]...

          Manage telemetry destinations.

        Options:
          --help  Show this message and exit.

        Commands:
          add   Add a destination from a preset.
          list  List configured destinations.
        """);

    public static readonly string ObservabilityAddHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup observability add [OPTIONS] <preset>

          Add a destination from a preset.

        Options:
          --token TEXT   Destination token.
          --name TEXT    Destination name.
          --help         Show this message and exit.
        """);

    /// <summary>The curated + filtered + annotated definition the catalog would build for claude-code.</summary>
    public static WizardDefinition ClaudeCode()
    {
        var help = SetupHelpParser.Parse(ClaudeCodeHelp);
        var (steps, curated) = WizardStepFactory.Build("claude-code", help);
        steps = WizardWindowsPolicy.Filter("claude-code", steps);
        steps = SecretRoutes.Annotate("claude-code", steps);

        return new WizardDefinition
        {
            Target = "claude-code",
            Title = "Claude Code",
            Group = WizardGroups.Connectors,
            Description = help.Summary,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            PlatformNote = help.PlatformNote,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    public static WizardDefinition Llm()
    {
        var help = SetupHelpParser.Parse(LlmHelp);
        var (steps, curated) = WizardStepFactory.Build("llm", help);
        steps = WizardWindowsPolicy.Filter("llm", steps);
        steps = SecretRoutes.Annotate("llm", steps);

        return new WizardDefinition
        {
            Target = "llm",
            Title = "LLM providers",
            Group = WizardGroups.Credentials,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    /// <summary>A wizard's answers as the window starts them: every field at its default.</summary>
    public static WizardValues StartingValues(WizardDefinition definition)
    {
        var values = new WizardValues();
        foreach (var field in definition.AllFields)
        {
            values[field.Id] = field.DefaultValue;
        }

        return values;
    }

    public static ConfigDocument Config(string yaml) => ConfigStore.Parse(yaml);

    public static WizardField Find(WizardDefinition definition, string flag) =>
        definition.AllFields.Single(f => f.Flag == flag);
}
