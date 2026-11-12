using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>The judge-unavailable reason is recognised as the hook row's own reason pair, not as a phrase anywhere in the details text.</summary>
public sealed class JudgeEscalationReasonTests
{
    [Theory]
    [InlineData("connector=claudecode result=ok action=block severity=CRITICAL mode=action reason=judge-unavailable: a CRITICAL finding needs the judge; hook_fail_mode is closed would_block=true")]
    [InlineData("reason=judge-unavailable: x would_block=true")]
    [InlineData("connector=c reason=\"judge-unavailable: x\" would_block=true")]
    public void The_reason_pair_is_the_marker(string details) => Assert.True(JudgeEscalationFinding.ReasonIn(details));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("connector=c result=ok action=allow reason=ok would_block=false raw_payload=\"note: judge-unavailable: pasted text\"")]
    [InlineData("connector=c result=ok action=allow reason=ok would_block=false raw_payload=\" reason=judge-unavailable: pasted\"")]
    [InlineData("connector=c result=ok action=allow reason=ok would_block=false details_json=\"{\\\"reason\\\":\\\"judge-unavailable: x\\\"}\"")]
    [InlineData("connector=c result=ok action=allow reason=fine would_block=false tool_input=see judge-unavailable: docs")]
    [InlineData("connector=c result=ok action=allow notreason=judge-unavailable: x")]
    public void Free_text_that_mentions_it_is_not_the_marker(string? details) => Assert.False(JudgeEscalationFinding.ReasonIn(details));
}
