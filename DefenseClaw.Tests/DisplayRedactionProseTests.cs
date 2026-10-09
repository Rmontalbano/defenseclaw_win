using DefenseClaw.Core.Logs;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="DisplayRedaction.Prose"/> (CUST-309): the sentences the runtime writes for a person keep their ordinary words, and everything that
/// is shaped like a credential still goes. The command-line mode (<see cref="DisplayRedaction.Text"/>) is untouched and tested in
/// <see cref="DisplayRedactionTests"/>.
/// </summary>
public class DisplayRedactionProseTests
{
    [Theory]
    [InlineData("the gateway needs an elevated token to read the Security channel")]
    [InlineData("an ETW session needs an elevated token, and this process is not elevated")]
    [InlineData("elevated token AND Advanced Audit Policy")]
    [InlineData("OpenProcess on another user's process needs an elevated token")]
    [InlineData("the token expired, so the secret store was not read")]
    [InlineData("a password manager is needed here")]
    public void An_ordinary_sentence_that_mentions_a_credential_word_is_left_whole(string sentence)
    {
        Assert.Equal(sentence, DisplayRedaction.Prose(sentence));
    }

    [Theory]
    [InlineData("the gateway needs an elevated token to read the Security channel", "the gateway needs an elevated token [redacted] read the Security channel")]
    [InlineData("--api-key synthetic-synthetic", "--api-key [redacted]")]
    public void The_command_line_mode_still_masks_the_word_after_a_credential_name(string text, string masked)
    {
        // The reason Text and Prose are two functions: a command line really does carry its value after a space.
        Assert.Equal(masked, DisplayRedaction.Text(text));
    }

    [Theory]
    [InlineData("could not open https://hooks.example.test/x?token=synthetic-synthetic", "synthetic-synthetic")]
    [InlineData("start failed: password=synthetic-synthetic", "synthetic-synthetic")]
    [InlineData("start failed: password: synthetic-synthetic", "synthetic-synthetic")]
    [InlineData("api_key=synthetic-synthetic was rejected", "synthetic-synthetic")]
    [InlineData("{\"secret\": \"synthetic-synthetic\"}", "synthetic-synthetic")]
    [InlineData("sent Authorization: synthetic-synthetic upstream", "synthetic-synthetic")]
    public void Anything_shaped_like_a_credential_assignment_is_still_masked_in_a_sentence(string sentence, string secret)
    {
        var shown = DisplayRedaction.Prose(sentence);

        Assert.DoesNotContain(secret, shown, StringComparison.Ordinal);
        Assert.Contains(DisplayRedaction.Mask, shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bearer_credential_is_masked_in_a_sentence_too()
    {
        Assert.DoesNotContain("synthetic-synthetic", DisplayRedaction.Prose("rejected bearer synthetic-synthetic"), StringComparison.Ordinal);
    }

    [Fact]
    public void Prose_masks_before_it_cuts_exactly_like_text_does()
    {
        var shown = DisplayRedaction.Prose(new string('x', 90) + " password=synthetic-synthetic", limit: 100);

        Assert.True(shown.Length <= 100);
        Assert.DoesNotContain("synthetic", shown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_is_nothing_out(string? input)
    {
        Assert.Equal(string.Empty, DisplayRedaction.Prose(input));
    }
}
