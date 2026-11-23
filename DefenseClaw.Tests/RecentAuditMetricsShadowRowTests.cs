using DefenseClaw.Core.Audit;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// A shadow measurement row (runtime 1.0.10: action <c>guardrail-inspection</c>, an effects lane details prefix or the secret flow target) records what a lane would
/// have done and is never a block, even when its free text holds an <c>action=block</c> token (a secret flow row's <c>program=</c> value is an unescaped tool name).
/// A real hook decision and a real block row still count. Synthetic rows.
/// </summary>
public sealed class RecentAuditMetricsShadowRowTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly TestAuditDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task A_shadow_row_whose_details_hold_action_block_is_not_a_block_but_a_real_one_is()
    {
        _database.InsertEvent("a", Base.AddMinutes(1), "guardrail-inspection", "INFO", details: "lane=effects-shadow mode=observe tier=high would=block action=block");
        _database.InsertEvent("b", Base.AddMinutes(2), "guardrail-inspection", "INFO", target: "secret-flow:shadow", details: "rule=r mode=observe kind=k program=tool action=block would=block connector=codex", connector: "codex");
        _database.InsertEvent("c", Base.AddMinutes(3), "connector-hook", "INFO", connector: "codex", details: "connector=codex result=ok action=block raw_action=block mode=action");
        _database.InsertEvent("d", Base.AddMinutes(4), "guardrail-block", "HIGH", connector: "codex");
        _database.InsertEvent("e", Base.AddMinutes(5), "guardrail-inspection", "INFO", target: "elsewhere", details: "program=x action=block");

        var result = await new RecentAuditMetricsReader(_database.Path).ReadAsync();

        // The hook decision, the block row, and the guardrail-inspection row that is not a shadow row (the old rule, unchanged).
        Assert.Equal(3, result.Blocks);
        Assert.Equal(new ConnectorMetrics(1, 2), result.For("codex"));
    }

    [Theory]
    [InlineData("guardrail-inspection", "lane=effects-shadow would=block action=block", null, false)]
    [InlineData("guardrail-inspection", "program=a action=block", "secret-flow:shadow", false)]
    [InlineData("guardrail-inspection", "program=a action=block", "other", true)]
    [InlineData("connector-hook", "lane=effects-shadow action=block", "secret-flow:shadow", true)]
    [InlineData("guardrail-block", null, "secret-flow:shadow", true)]
    public void IsBlockRow_judges_a_shadow_row_by_its_action_and_marks(string action, string? details, string? target, bool expected)
    {
        Assert.Equal(expected, RecentAuditMetricsReader.IsBlockRow(action, details, target));
    }
}
