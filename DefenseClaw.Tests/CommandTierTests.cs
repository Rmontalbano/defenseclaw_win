using DefenseClaw.Core.Cli;

namespace DefenseClaw.Tests;

public sealed class CommandTierTests
{
    [Theory]
    [InlineData(CommandTier.ReadOnly, "skill", "list", "--json")]
    [InlineData(CommandTier.ReadOnly, "status")]
    [InlineData(CommandTier.ReadOnly, "config", "validate")]
    [InlineData(CommandTier.ReadOnly, "doctor")]
    [InlineData(CommandTier.ReadOnly, "setup", "llm", "--help")]
    [InlineData(CommandTier.ReadOnly, "--version-json")]
    [InlineData(CommandTier.StateChanging, "tool", "block", "curl", "--connector", "claudecode")]
    [InlineData(CommandTier.StateChanging, "agent", "discovery", "scan")]
    [InlineData(CommandTier.StateChanging, "start")]
    [InlineData(CommandTier.StateChanging, "doctor", "--fix")]
    [InlineData(CommandTier.StateChanging, "registry", "sync", "list")]
    [InlineData(CommandTier.Destructive, "skill", "quarantine", "--", "--help")]
    [InlineData(CommandTier.Destructive, "mcp", "remove", "--", "--dry-run")]
    [InlineData(CommandTier.StateChanging, "skill", "block", "--", "--version")]
    [InlineData(CommandTier.StateChanging, "doctor", "--fix", "--", "x")]
    [InlineData(CommandTier.ReadOnly, "doctor", "--", "--fix")]
    [InlineData(CommandTier.StateChanging, "observability", "destination", "test")]
    [InlineData(CommandTier.StateChanging, "some", "unknown", "verb")]
    [InlineData(CommandTier.ReadOnly, "registry", "entries", "src")]
    [InlineData(CommandTier.ReadOnly, "agent", "discovery", "status", "--json")]
    [InlineData(CommandTier.ReadOnly, "agent", "processes")]
    [InlineData(CommandTier.ReadOnly, "observability", "plan", "--format", "json")]
    [InlineData(CommandTier.ReadOnly, "alerts", "dismiss", "--severity", "CRITICAL", "--dry-run")]
    [InlineData(CommandTier.StateChanging, "alerts", "acknowledge", "--severity", "HIGH")]
    [InlineData(CommandTier.Destructive, "plugin", "remove", "foo")]
    [InlineData(CommandTier.Destructive, "skill", "quarantine", "bar", "--connector", "claudecode")]
    [InlineData(CommandTier.Destructive, "mcp", "unset", "server")]
    [InlineData(CommandTier.Destructive, "registry", "remove", "src", "--non-interactive")]
    [InlineData(CommandTier.Destructive, "alerts", "dismiss", "--severity", "all")]

    // A read-only flag is a flag only when it stands alone. As the value of an option it says nothing (D3-08).
    [InlineData(CommandTier.StateChanging, "upgrade", "--version", "0.9.0")]
    [InlineData(CommandTier.StateChanging, "upgrade", "--version", "0.9.0", "--yes")]
    [InlineData(CommandTier.ReadOnly, "upgrade", "--version")]
    [InlineData(CommandTier.ReadOnly, "upgrade", "--help")]
    [InlineData(CommandTier.StateChanging, "skill", "block", "--reason", "--help")]
    [InlineData(CommandTier.StateChanging, "setup", "webhook", "add", "--name", "--dry-run")]
    [InlineData(CommandTier.StateChanging, "setup", "llm", "--model", "--version")]
    [InlineData(CommandTier.ReadOnly, "uninstall", "--yes", "--dry-run")]
    [InlineData(CommandTier.ReadOnly, "doctor", "--fix", "--dry-run")]
    [InlineData(CommandTier.ReadOnly, "alerts", "dismiss", "--severity=CRITICAL", "--dry-run")]

    // Secret values on the screen are reviewed like a change (D3-08).
    [InlineData(CommandTier.StateChanging, "config", "show", "--reveal")]
    [InlineData(CommandTier.StateChanging, "keys", "list", "--show-values")]
    [InlineData(CommandTier.StateChanging, "setup", "splunk", "--show-credentials")]
    [InlineData(CommandTier.ReadOnly, "config", "show", "--reveal", "--help")]
    [InlineData(CommandTier.ReadOnly, "config", "show", "--format", "yaml")]

    // The verb may be the fourth token (D3-08).
    [InlineData(CommandTier.Destructive, "setup", "splunk", "dashboards", "destroy")]
    [InlineData(CommandTier.ReadOnly, "agent", "confidence", "policy", "show")]
    public void Classifies_by_the_command_path(CommandTier expected, params string[] argv) =>
        Assert.Equal(expected, CommandTiers.Classify(argv));

    [Fact]
    public void Flag_values_and_deep_targets_cannot_change_the_tier()
    {
        // "list" as a flag value, and "remove" as a fifth positional, are not verbs.
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(["tool", "block", "x", "--reason", "list"]));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(["skill", "allow", "a", "b", "c", "remove", "--yes"]));
    }

    [Theory]
    [InlineData(new[] { "config", "show", "--reveal" }, true)]
    [InlineData(new[] { "keys", "list", "--show-values" }, true)]
    [InlineData(new[] { "setup", "splunk", "--show-credentials" }, true)]
    [InlineData(new[] { "config", "show" }, false)]
    [InlineData(new[] { "skill", "block", "--", "--reveal" }, false)]
    public void Asking_for_secret_values_is_recognised_before_the_terminator_only(string[] argv, bool expected) =>
        Assert.Equal(expected, CommandTiers.PrintsSecrets(argv));

    [Theory]
    [InlineData(new[] { "--dry-run" }, 0, true)]
    [InlineData(new[] { "a", "--dry-run" }, 1, true)]
    [InlineData(new[] { "--name", "--dry-run" }, 1, false)]
    [InlineData(new[] { "--name=x", "--dry-run" }, 1, true)]
    [InlineData(new[] { "--yes", "--dry-run" }, 1, true)]
    [InlineData(new[] { "--no-restart", "--show" }, 1, true)]
    [InlineData(new[] { "--version", "1.0", "--yes" }, 0, false)]
    [InlineData(new[] { "--dry-run", "--json" }, 0, true)]
    public void A_flag_is_standalone_unless_it_is_an_option_value_or_takes_one(string[] tokens, int index, bool expected) =>
        Assert.Equal(expected, CommandTiers.IsStandaloneFlag(tokens, index));

    [Fact]
    public void An_empty_argv_is_treated_as_state_changing() =>
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(Array.Empty<string>()));
}
