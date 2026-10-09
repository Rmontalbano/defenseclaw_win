using DefenseClaw.Core.Cli;

namespace DefenseClaw.Tests;

/// <summary>
/// The "next:" hint of an Activity entry (CUST-264), held to DefenseClaw 0.8.10's own table. Every expected value below was produced by running
/// the installed TUI's <c>suggested_next_action</c> and <c>_derive_command_label</c> (read from <c>defenseclaw/tui/command_line.py</c> and
/// <c>app.py</c> as text and evaluated on their own, nothing of DefenseClaw imported or started) over the same inputs, so a difference between
/// this port and the TUI is a failing row here.
/// </summary>
public sealed class CommandNextActionTests
{
    private const string Review = "review output and rerun when fixed";
    private const string Credentials = "open Credentials or run keys check";
    private const string Readiness = "open readiness or rerun doctor";

    // ------------------------------------------------------------------ the table (suggested_next_action)

    [Theory]
    // doctor
    [InlineData("defenseclaw doctor", 0, "review readiness")]
    [InlineData("defenseclaw doctor", 1, Readiness)]
    [InlineData("defenseclaw doctor", 2, Readiness)]
    [InlineData("doctor", 0, "review readiness")]
    // keys
    [InlineData("keys list", 0, "rerun readiness")]
    [InlineData("keys list", 1, Credentials)]
    [InlineData("defenseclaw keys check", 0, "rerun readiness")]
    [InlineData("defenseclaw keys check", 2, Credentials)]
    // setup
    [InlineData("defenseclaw setup guardrail", 0, "rerun readiness")]
    [InlineData("defenseclaw setup guardrail", 1, Review)]
    [InlineData("defenseclaw setup local-observability", 0, "rerun readiness")]
    [InlineData("defenseclaw setup local-observability", 2, Review)]
    // restart
    [InlineData("defenseclaw-gateway restart", 0, "refresh gateway health")]
    [InlineData("defenseclaw-gateway restart", 1, Review)]
    // nothing to say after a success, the generic nudge after a failure
    [InlineData("defenseclaw skill list", 0, "")]
    [InlineData("defenseclaw skill list", 1, Review)]
    [InlineData("defenseclaw skill block", 0, "")]
    [InlineData("defenseclaw agent discovery", 0, "")]
    [InlineData("defenseclaw registry sync", 0, "")]
    [InlineData("defenseclaw guardrail enable", 0, "")]
    [InlineData("defenseclaw init", 0, "")]
    [InlineData("defenseclaw-gateway watchdog start", 0, "")]
    [InlineData("defenseclaw-gateway watchdog start", 1, Review)]
    // the match is on the lower-cased, trimmed text
    [InlineData("KEYS LIST", 0, "rerun readiness")]
    [InlineData("KEYS LIST", 1, Credentials)]
    [InlineData("  Doctor  ", 0, "review readiness")]
    [InlineData("  Doctor  ", 1, Readiness)]
    // the first rule that matches wins: keys, then doctor, then setup, then restart
    [InlineData("keys doctor", 0, "rerun readiness")]
    [InlineData("doctor keys", 0, "rerun readiness")]
    [InlineData("doctor keys", 1, Credentials)]
    [InlineData("doctor setup", 0, "review readiness")]
    [InlineData("setup restart", 0, "rerun readiness")]
    [InlineData("restart doctor", 0, "review readiness")]
    [InlineData("restart doctor", 1, Readiness)]
    // an empty label is still a failure when the exit code says so
    [InlineData("", 0, "")]
    [InlineData("", 1, Review)]
    public void The_hint_is_the_TUIs_for_the_same_command_and_exit_code(string command, int exitCode, string expected) =>
        Assert.Equal(expected, CommandNextAction.Suggest(command, exitCode));

    [Fact]
    public void Any_non_zero_exit_code_is_a_failure_and_a_missing_command_is_an_empty_one()
    {
        Assert.Equal(Review, CommandNextAction.Suggest("defenseclaw skill list", -1));
        Assert.Equal(Review, CommandNextAction.Suggest("defenseclaw skill list", 255));
        Assert.Equal(Review, CommandNextAction.Suggest(null!, 3));
        Assert.Equal(string.Empty, CommandNextAction.Suggest(null!, 0));
    }

    // ------------------------------------------------------------------ the label (_derive_command_label)

    [Theory]
    [InlineData("defenseclaw", new[] { "doctor" }, "defenseclaw doctor")]
    [InlineData("defenseclaw", new[] { "doctor", "--fix" }, "defenseclaw doctor")]
    [InlineData("defenseclaw", new[] { "skill", "block", "--", "pdf-tools" }, "defenseclaw skill block")]
    [InlineData("defenseclaw", new[] { "setup", "local-observability", "up" }, "defenseclaw setup local-observability")]
    [InlineData("defenseclaw", new[] { "--help" }, "defenseclaw")]
    [InlineData("defenseclaw", new string[0], "defenseclaw")]
    [InlineData("defenseclaw", new[] { "agent", "discovery", "scan" }, "defenseclaw agent discovery")]
    [InlineData("defenseclaw", new[] { "--json", "keys", "list" }, "defenseclaw keys list")]
    [InlineData("defenseclaw-gateway", new[] { "restart" }, "defenseclaw-gateway restart")]
    public void The_label_is_the_tool_and_the_first_two_words_that_are_not_options(string executable, string[] argv, string expected) =>
        Assert.Equal(expected, CommandNextAction.LabelFor(executable, argv));

    [Fact]
    public void The_tool_is_named_without_its_folder_or_extension()
    {
        // The TUI keeps the file name whole ("defenseclaw-gateway.exe watchdog start"); nothing it matches on is in the extension.
        Assert.Equal(
            "defenseclaw-gateway watchdog start",
            CommandNextAction.LabelFor(@"C:\Programs\DefenseClaw\bin\defenseclaw-gateway.exe", new[] { "watchdog", "start" }));
        Assert.Equal("defenseclaw doctor", CommandNextAction.LabelFor(@"C:\Programs\DefenseClaw\bin\defenseclaw.exe", new[] { "doctor" }));
        Assert.Equal("command doctor", CommandNextAction.LabelFor(string.Empty, new[] { "doctor" }));
    }

    [Fact]
    public void A_name_after_the_terminator_is_not_a_word_of_the_command()
    {
        // The one difference from the TUI: its label for `skill -- doctor` is "defenseclaw skill doctor", which would say "review readiness" of a
        // skill that happens to be called doctor. What follows `--` is a name that came from outside.
        Assert.Equal("defenseclaw skill", CommandNextAction.LabelFor("defenseclaw", new[] { "skill", "--", "doctor" }));
        Assert.Equal(string.Empty, CommandNextAction.For("defenseclaw", new[] { "skill", "block", "--", "doctor" }, 0));
        Assert.Equal(string.Empty, CommandNextAction.For("defenseclaw", new[] { "skill", "block", "--", "keys" }, 0));
        Assert.Equal(string.Empty, CommandNextAction.For("defenseclaw", new[] { "skill", "block", "--", "restart" }, 0));
    }

    [Fact]
    public void An_option_value_does_not_make_a_hint_either_only_the_first_two_words_count()
    {
        // `mcp set doctor-helper --command ...` is about an MCP server: only "mcp" and "set" are words of the command.
        Assert.Equal(string.Empty, CommandNextAction.For("defenseclaw", new[] { "mcp", "set", "doctor-helper", "--command", "x" }, 0));
        Assert.Equal(Review, CommandNextAction.For("defenseclaw", new[] { "mcp", "set", "doctor-helper", "--command", "x" }, 1));
    }

    [Theory]
    [InlineData("defenseclaw", new[] { "doctor" }, 0, "review readiness")]
    [InlineData("defenseclaw", new[] { "doctor" }, 1, Readiness)]
    [InlineData("defenseclaw", new[] { "keys", "list" }, 0, "rerun readiness")]
    [InlineData("defenseclaw", new[] { "keys", "check" }, 1, Credentials)]
    [InlineData("defenseclaw", new[] { "setup", "guardrail", "--yes" }, 0, "rerun readiness")]
    [InlineData("defenseclaw-gateway", new[] { "restart" }, 0, "refresh gateway health")]
    [InlineData("defenseclaw", new[] { "skill", "list" }, 0, "")]
    [InlineData("defenseclaw", new[] { "skill", "list" }, 1, Review)]
    public void The_hint_of_a_command_is_the_hint_of_its_label(string executable, string[] argv, int exitCode, string expected) =>
        Assert.Equal(expected, CommandNextAction.For(executable, argv, exitCode));
}
