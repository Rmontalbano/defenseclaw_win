using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The line 1.0.5's <c>tool block|allow|unblock</c> prints after waiting for the gateway (CUST-400): whether the running gateway enforces the change yet.
/// The sentences are copied from cmd_tool.py (<c>_echo_gateway_enforcement_checked</c>).
/// </summary>
public sealed class GatewayEnforcementNoticeTests
{
    private const string BlockedLine = "[tool] 'delete_file' added to block list (covers connector=codex)";
    private const string EnforcedLine = "The running gateway enforces it now (policy digest 0123456789ab).";
    private const string PendingLine =
        "Pending: the running gateway has not applied it yet. Hook calls keep the previous verdict until it reloads config.yaml, usually within a few seconds.";

    private static CliInvocation Run(params (CliStream Stream, string Text)[] lines)
    {
        var invocation = InvocationFactory.Create(argv: new[] { "tool", "block", "--", "delete_file" });
        foreach (var (stream, text) in lines)
        {
            InvocationFactory.Append(invocation, text, stream);
        }

        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_enforced_sentence_is_found_with_either_line_ending_and_its_digest_is_not_echoed(string eol)
    {
        var stdout = BlockedLine + eol + EnforcedLine + eol;

        Assert.Equal(GatewayEnforcement.Enforced, GatewayEnforcementNotice.Parse(stdout, out var text));
        Assert.Equal("The running gateway enforces it now.", text);
        Assert.DoesNotContain("digest", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_pending_sentence_is_found_with_either_line_ending_and_keeps_the_clis_words(string eol)
    {
        var stdout = BlockedLine + eol + PendingLine + eol;

        Assert.Equal(GatewayEnforcement.Pending, GatewayEnforcementNotice.Parse(stdout, out var text));
        Assert.Equal(PendingLine, text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[tool] 'delete_file' added to block list (unscoped fallback)\n")]
    [InlineData("The gateway enforces it now.\n")]
    public void A_runtime_that_prints_neither_sentence_reports_nothing(string? stdout)
    {
        Assert.Equal(GatewayEnforcement.NotReported, GatewayEnforcementNotice.Parse(stdout, out var text));
        Assert.Empty(text);
    }

    [Fact]
    public void When_a_command_prints_both_the_last_one_wins()
    {
        Assert.Equal(GatewayEnforcement.Enforced, GatewayEnforcementNotice.Parse(PendingLine + "\n" + EnforcedLine, out _));
        Assert.Equal(GatewayEnforcement.Pending, GatewayEnforcementNotice.Parse(EnforcedLine + "\r\n" + PendingLine, out _));
    }

    [Fact]
    public void Only_standard_output_counts()
    {
        var invocation = Run((CliStream.StandardOutput, BlockedLine), (CliStream.StandardError, EnforcedLine));

        Assert.Equal(GatewayEnforcement.NotReported, GatewayEnforcementNotice.Parse(invocation, out _));
    }

    // ------------------------------------------------------------------ the result bar

    [Fact]
    public void An_enforced_change_is_a_success_that_says_so()
    {
        var invocation = Run((CliStream.StandardOutput, BlockedLine), (CliStream.StandardOutput, EnforcedLine));

        var (title, message, severity) = GatewayEnforcementNotice.Done("Blocked “delete_file”.", invocation);

        Assert.Equal("Done", title);
        Assert.Equal(InfoBarSeverity.Success, severity);
        Assert.Equal("Blocked “delete_file”. The running gateway enforces it now. Recorded in Activity.", message);
    }

    [Fact]
    public void A_pending_change_is_a_warning_that_says_hook_calls_keep_the_old_verdict()
    {
        var invocation = Run((CliStream.StandardOutput, BlockedLine), (CliStream.StandardOutput, PendingLine));

        var (title, message, severity) = GatewayEnforcementNotice.Done("Allowed “search”.", invocation);

        Assert.Equal("Saved, not enforced yet", title);
        Assert.Equal(InfoBarSeverity.Warning, severity);
        Assert.StartsWith("Allowed “search”. Pending: the running gateway has not applied it yet.", message, StringComparison.Ordinal);
        Assert.Contains("keep the previous verdict", message, StringComparison.Ordinal);
        Assert.EndsWith(" Recorded in Activity.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_earlier_runtime_gets_the_fixed_text_unchanged()
    {
        var invocation = Run((CliStream.StandardOutput, BlockedLine));

        var (title, message, severity) = GatewayEnforcementNotice.Done("Blocked “delete_file”.", invocation);

        Assert.Equal("Done", title);
        Assert.Equal(InfoBarSeverity.Success, severity);
        Assert.Equal("Blocked “delete_file”. Recorded in Activity.", message);
    }
}
