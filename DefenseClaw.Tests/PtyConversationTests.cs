using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.Tests;

/// <summary>
/// What is said to a command in a pseudo-console (<see cref="PtyConversation"/>) with no process and no console: which prompt arms an answer,
/// when the keystrokes may be handed out, that a value comes out once and only at its own prompt, and that no line of the transcript carries it.
/// All values and names are synthetic.
/// </summary>
public sealed class PtyConversationTests
{
    private const string Esc = "\u001b";
    private const string Name = "EXAMPLE_JUDGE_KEY";
    private const string Value = "synthetic-test-value";

    private static PtyAnswer Answer(string name = Name, string value = Value) => new(SecretPtyRunner.PromptFor(name), new SecretValue(value));

    private static PtyConversation Conversation(params PtyAnswer[] answers) => new(answers);

    [Theory]
    [InlineData("  EXAMPLE_JUDGE_KEY:", true)]
    [InlineData("  EXAMPLE_JUDGE_KEY: ", true)]
    [InlineData("EXAMPLE_JUDGE_KEY:", true)]
    [InlineData("    EXAMPLE_JUDGE_KEY:", true)]
    [InlineData("Enter EXAMPLE_JUDGE_KEY:", true)]
    [InlineData("  OTHER_EXAMPLE_JUDGE_KEY:", false)]
    [InlineData("  EXAMPLE_JUDGE_KEY", false)]
    [InlineData("  EXAMPLE_JUDGE_KEY: already set", false)]
    [InlineData("  EXAMPLE_JUDGE_KEY=", false)]
    [InlineData("  example_judge_key:", false)]
    [InlineData("  Saved EXAMPLE_JUDGE_KEY = x to y", false)]
    public void Only_the_prompt_for_the_variable_arms_the_answer(string cursorLine, bool armed)
    {
        var conversation = Conversation(Answer());

        _ = conversation.Feed(cursorLine);

        Assert.Equal(armed, conversation.PromptShowing);
    }

    [Fact]
    public void The_value_is_typed_once_after_the_output_has_gone_quiet_and_with_an_enter()
    {
        var conversation = Conversation(Answer());
        _ = conversation.Feed("  Judge: Key for the judge\r\n  EXAMPLE_JUDGE_KEY: ");

        Assert.True(conversation.PromptShowing);
        Assert.Null(conversation.TakeInput(quiet: false));      // the child may still be writing
        Assert.Equal(Value + "\r", conversation.TakeInput(quiet: true));
        Assert.Null(conversation.TakeInput(quiet: true));       // never twice
        Assert.False(conversation.PromptShowing);
        Assert.Equal(1, conversation.Typed);
        Assert.True(conversation.AllTyped);

        // The prompt drawn again (a repaint) does not arm an answer that was used.
        _ = conversation.Feed(Esc + "[H  EXAMPLE_JUDGE_KEY: ");
        Assert.False(conversation.PromptShowing);
        Assert.Null(conversation.TakeInput(quiet: true));
    }

    [Fact]
    public void A_line_that_merely_ends_in_the_name_at_a_chunk_boundary_is_not_answered_when_it_goes_on()
    {
        var conversation = Conversation(Answer());

        _ = conversation.Feed("Remember to set EXAMPLE_JUDGE_KEY:");
        Assert.True(conversation.PromptShowing); // it looks like a prompt for as long as nothing follows...
        Assert.Null(conversation.TakeInput(quiet: false)); // ...which is why nothing is typed until the output is quiet

        _ = conversation.Feed(" before you start\r\n");
        Assert.False(conversation.PromptShowing);
        Assert.Null(conversation.TakeInput(quiet: true));
        Assert.Equal(0, conversation.Typed);
    }

    [Fact]
    public void The_prompt_split_over_chunks_and_sequences_is_found()
    {
        var conversation = Conversation(Answer());

        _ = conversation.Feed("  EXAMPLE_JU");
        Assert.False(conversation.PromptShowing);
        _ = conversation.Feed(Esc + "]0;title\a" + Esc + "[?25h");
        _ = conversation.Feed("DGE_KEY");
        Assert.False(conversation.PromptShowing);
        _ = conversation.Feed(": ");

        Assert.True(conversation.PromptShowing);
    }

    [Fact]
    public void A_prompt_nobody_has_an_answer_for_is_not_answered()
    {
        var conversation = Conversation(Answer());

        _ = conversation.Feed("Overwrite the stored value? [y/N]: ");

        Assert.False(conversation.PromptShowing);
        Assert.Null(conversation.TakeInput(quiet: true));
    }

    [Fact]
    public void Several_values_are_each_typed_at_their_own_prompt_in_the_order_the_prompts_come()
    {
        var conversation = Conversation(Answer("FIRST_EXAMPLE_KEY", "synthetic-first"), Answer("SECOND_EXAMPLE_KEY", "synthetic-second"));

        _ = conversation.Feed("    SECOND_EXAMPLE_KEY: ");
        Assert.Equal("synthetic-second\r", conversation.TakeInput(quiet: true));

        _ = conversation.Feed("\r\n    FIRST_EXAMPLE_KEY: ");
        Assert.Equal("synthetic-first\r", conversation.TakeInput(quiet: true));

        Assert.True(conversation.AllTyped);
        Assert.Equal(2, conversation.Typed);
        Assert.Equal(2, conversation.Total);
    }

    [Fact]
    public void No_line_carries_the_value_or_the_clis_preview_of_it()
    {
        const string preview = "synt…alue"; // four characters, an ellipsis, four characters: what the CLI prints after it saved the value
        var conversation = Conversation(Answer());

        var lines = new List<string>();
        lines.AddRange(conversation.Feed("  EXAMPLE_JUDGE_KEY: "));
        _ = conversation.TakeInput(quiet: true);
        lines.AddRange(conversation.Feed(
            "\r\n  OK Saved EXAMPLE_JUDGE_KEY = " + preview + " to C:\\Data\\.defenseclaw/.env\r\n" +
            "  echo: " + Value + "\r\n" +
            "  bound to https://example.test/" + Value + "\r\n"));
        lines.AddRange(conversation.Finish());

        Assert.DoesNotContain(lines, l => l.Contains(Value, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(preview, StringComparison.Ordinal));
        Assert.Contains("  OK Saved EXAMPLE_JUDGE_KEY = ***REDACTED*** to C:\\Data\\.defenseclaw/.env", lines);
        Assert.Contains("  echo: ***REDACTED***", lines);
        Assert.Contains("  bound to https://example.test/***REDACTED***", lines);
    }

    [Fact]
    public void A_value_the_console_echoes_while_it_is_typed_is_scrubbed_from_the_line_it_ends_up_on()
    {
        var conversation = Conversation(Answer());

        // A console in line mode draws what is typed after the prompt, in pieces.
        var lines = new List<string>();
        lines.AddRange(conversation.Feed("  EXAMPLE_JUDGE_KEY: "));
        _ = conversation.TakeInput(quiet: true);
        foreach (var piece in new[] { "synthet", "ic-test", "-value" })
        {
            lines.AddRange(conversation.Feed(piece));
        }

        lines.AddRange(conversation.Feed("\r\n"));
        lines.AddRange(conversation.Finish());

        Assert.Equal(new[] { "  EXAMPLE_JUDGE_KEY: ***REDACTED***" }, lines);
    }

    [Theory]
    [InlineData("synthetic-test-value", "synt…alue")]
    [InlineData("123456789", "1234…6789")]
    [InlineData("12345678", "")]   // eight or fewer: the CLI prints ****, which reveals nothing
    [InlineData("a", "")]
    public void The_preview_is_the_one_the_cli_builds(string value, string preview)
    {
        Assert.Equal(preview, PtyConversation.PreviewOf(value));
    }

    [Fact]
    public void A_short_value_has_no_preview_to_hide_and_is_still_scrubbed()
    {
        var conversation = Conversation(Answer(value: "abc"));
        _ = conversation.Feed("  EXAMPLE_JUDGE_KEY: ");
        _ = conversation.TakeInput(quiet: true);

        var lines = conversation.Feed("\r\n  Saved EXAMPLE_JUDGE_KEY = **** to x abc\r\n");

        Assert.Contains("  Saved EXAMPLE_JUDGE_KEY = **** to x ***REDACTED***", lines);
    }

    [Fact]
    public void Blank_lines_stay_between_text_and_are_held_back_at_either_end()
    {
        var conversation = Conversation(Answer());

        var lines = new List<string>();
        lines.AddRange(conversation.Feed("\r\n\r\nfirst\r\n\r\nsecond\r\n\r\n\r\n"));
        lines.AddRange(conversation.Finish());

        Assert.Equal(new[] { "first", string.Empty, "second" }, lines);
    }

    [Fact]
    public void The_queries_the_console_was_never_answered_are_kept_for_a_failures_explanation()
    {
        var conversation = Conversation(Answer());

        _ = conversation.Feed(Esc + "[6n");

        Assert.Single(conversation.Queries);
    }

    [Fact]
    public void A_conversation_without_an_answer_is_just_a_reader()
    {
        var conversation = Conversation();

        var lines = conversation.Feed("hello\r\n");

        Assert.Equal(new[] { "hello" }, lines);
        Assert.True(conversation.AllTyped);
        Assert.Null(conversation.TakeInput(quiet: true));
    }
}
