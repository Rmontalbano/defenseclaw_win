using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.Tests;

/// <summary>
/// The never-set-up Cowork line (CUST-444): the app's <c>Not set up</c> label, then the CLI's sentence without its own leading <c>not protected: </c>
/// (the label says it already). A sentence without that prefix is shown unchanged; other reason codes keep the CLI's sentence as written. Made-up states.
/// </summary>
public class CoworkNotSetUpTextTests
{
    private const string CliSentence = "not protected: Claude Desktop is installed, but Cowork has no DefenseClaw plugin";

    private static CoworkProtection State(string code, string detail, bool isProtected = false) =>
        new(isProtected, detail, "defenseclaw setup cowork", code, null, null, null);

    [Fact]
    public void The_clis_not_protected_prefix_is_dropped_after_the_label()
    {
        Assert.Equal(
            "Not set up: Claude Desktop is installed, but Cowork has no DefenseClaw plugin",
            State(CoworkReasonCodes.PluginNotBuilt, CliSentence).NotSetUpText);
    }

    [Theory]
    [InlineData("Claude Desktop is installed, but no plugin", "Not set up: Claude Desktop is installed, but no plugin")]
    [InlineData("Not protected: capitalised differently", "Not set up: Not protected: capitalised differently")]
    [InlineData("not protected yet: a different lead-in", "Not set up: not protected yet: a different lead-in")]
    [InlineData("", "Not set up")]
    public void A_sentence_without_the_exact_prefix_follows_the_label_unchanged(string detail, string expected)
    {
        Assert.Equal(expected, State(CoworkReasonCodes.PluginNotBuilt, detail).NotSetUpText);
    }

    [Fact]
    public void The_setup_card_detail_carries_the_label_for_a_cowork_that_was_never_set_up()
    {
        var card = SetupStateLines.Cowork(State(CoworkReasonCodes.PluginNotBuilt, CliSentence));

        Assert.NotNull(card);
        Assert.Equal("Not set up", card.Text);
        Assert.Equal("Not set up: Claude Desktop is installed, but Cowork has no DefenseClaw plugin", card.Detail);
    }

    [Theory]
    [InlineData(CoworkReasonCodes.NoEvents, "not protected yet: no Cowork event has reached DefenseClaw")]
    [InlineData(CoworkReasonCodes.ClaudeCodeOff, "not protected: the Claude Code connector is off")]
    [InlineData(CoworkReasonCodes.GatewayDown, "not protected while the sidecar is not answering")]
    public void Other_reason_codes_keep_the_clis_sentence_on_the_card(string code, string detail)
    {
        var card = SetupStateLines.Cowork(State(code, detail));

        Assert.NotNull(card);
        Assert.Equal(detail, card.Detail);
    }
}
