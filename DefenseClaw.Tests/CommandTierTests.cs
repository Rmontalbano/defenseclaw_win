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
    public void Classifies_by_the_command_path(CommandTier expected, params string[] argv) =>
        Assert.Equal(expected, CommandTiers.Classify(argv));

    [Fact]
    public void Flag_values_and_deep_targets_cannot_change_the_tier()
    {
        // "list" as a flag value, and "remove" as a fourth positional, are not verbs.
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(["tool", "block", "x", "--reason", "list"]));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(["skill", "allow", "a", "remove", "--yes"]));
    }

    [Fact]
    public void An_empty_argv_is_treated_as_state_changing() =>
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(Array.Empty<string>()));
}
