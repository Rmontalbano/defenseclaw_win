using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// What an audit event is about, from its action - the TUI's <c>_target_type_from_action</c> (CUST-261): the key of an event's "Current state" lookup and the
/// second reading of the <c>type:</c> search token. The first rule that matches wins, so <c>scan-skill</c> is a skill event.
/// </summary>
public sealed class AuditTargetTypeTests
{
    [Theory]
    [InlineData("skill-block", "skill")]
    [InlineData("skill-unblock", "skill")]
    [InlineData("scan-skill", "skill")]
    [InlineData("SKILL-ALLOW", "skill")]
    [InlineData("mcp-set", "mcp")]
    [InlineData("mcp-unset-noop", "mcp")]
    [InlineData("plugin-install", "plugin")]
    [InlineData("plugin-skill", "skill")]
    [InlineData("tool_invocation", "tool")]
    [InlineData("tool.invocation.completed", "tool")]
    [InlineData("mcp-tool-call", "mcp")]
    [InlineData("scan", "scan")]
    [InlineData("scan-finding", "scan")]
    [InlineData("finding.observed", "scan")]
    [InlineData("key-rotation", "credential")]
    [InlineData("token-refresh", "credential")]
    [InlineData("credential-set", "credential")]
    [InlineData("secret-read", "credential")]
    [InlineData("alert-dismiss", "alert")]
    [InlineData("config.change.applied", "config")]
    [InlineData("setup-run", "config")]
    [InlineData("init", "config")]
    [InlineData("connector-hook", "")]
    [InlineData("hook_decision", "")]
    [InlineData("install-blocked", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_first_rule_that_matches_decides(string? action, string expected) => Assert.Equal(expected, AuditTargetType.FromAction(action));

    [Theory]
    [InlineData("skill", true)]
    [InlineData("mcp", true)]
    [InlineData("plugin", true)]
    [InlineData("tool", true)]
    [InlineData("scan", false)]
    [InlineData("credential", false)]
    [InlineData("alert", false)]
    [InlineData("config", false)]
    [InlineData("", false)]
    [InlineData("Skill", false)]
    [InlineData(null, false)]
    public void Only_what_the_Govern_panels_manage_has_a_current_state(string? type, bool governed) => Assert.Equal(governed, AuditTargetType.IsGoverned(type));
}
