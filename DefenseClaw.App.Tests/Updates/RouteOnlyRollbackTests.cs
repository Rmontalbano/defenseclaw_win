using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// G10I-02: the rollback review's warning for route-only agents. install.ps1 1.0.10 puts back the model settings route-only changed before it swaps the install
/// (scripts/install.ps1@1.0.10:5063-5084), so the review says so only when today's config.yaml names an agent and the target has no route-only. Synthetic configs.
/// </summary>
public sealed class RouteOnlyRollbackTests
{
    private const string Both = "routing:\n  route_only:\n    connectors:\n      - codex\n      - claudecode\n";

    [Fact]
    public void Both_agents_are_named_in_the_order_claudecode_then_codex_for_a_target_before_1_0_10()
    {
        var text = RouteOnlyRollback.Evaluate(Both, "1.0.9");

        Assert.NotNull(text);
        Assert.StartsWith("Route-only is on for claudecode and codex. DefenseClaw 1.0.9 has no route-only, ", text, StringComparison.Ordinal);
        Assert.Contains("Restart those agents after the rollback.", text, StringComparison.Ordinal);
        Assert.Contains("gets 401 on its model calls until the DefenseClaw base URL and route header are removed from its settings.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("routing disable", text, StringComparison.Ordinal);
        Assert.Equal("Route-only agents", RouteOnlyRollback.WarningTitle);
    }

    [Fact]
    public void One_agent_and_a_v_prefixed_older_target_are_named_and_duplicates_count_once()
    {
        var text = RouteOnlyRollback.Evaluate("routing:\n  route_only:\n    connectors: [codex, codex]\n", "v0.8.10");

        Assert.NotNull(text);
        Assert.StartsWith("Route-only is on for codex. DefenseClaw 0.8.10 has no route-only, ", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.10")]
    [InlineData("1.0.11")]
    [InlineData("1.1.0")]
    public void A_target_with_route_only_gets_no_warning(string backTo) => Assert.Null(RouteOnlyRollback.Evaluate(Both, backTo));

    [Theory]
    [InlineData("config_version: 10\n")]
    [InlineData("routing:\n  enabled: true\n")]
    [InlineData("routing:\n  route_only:\n    connectors: []\n")]
    [InlineData("routing:\n  route_only:\n    connectors:\n")]
    [InlineData("routing:\n  route_only:\n    upstreams:\n      claudecode: https://synthetic.invalid\n")]
    [InlineData("routing:\n  route_only:\n    connectors:\n      - openclaw\n      - Codex\n")]
    [InlineData("")]
    public void A_config_that_names_no_route_only_agent_gets_no_warning(string config) => Assert.Null(RouteOnlyRollback.Evaluate(config, "1.0.9"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.0.9; calc")]
    [InlineData("..\\..\\1.0.9")]
    [InlineData("1.0.9\n1.0.8")]
    public void A_target_that_is_not_a_version_gets_no_warning(string? backTo) => Assert.Null(RouteOnlyRollback.Evaluate(Both, backTo));

    [Fact]
    public void No_config_text_gets_no_warning() => Assert.Null(RouteOnlyRollback.Evaluate(null, "1.0.9"));
}
