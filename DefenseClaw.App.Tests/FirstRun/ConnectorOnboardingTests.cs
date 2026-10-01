using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.FirstRun;

/// <summary>Which connectors first run and the per-row Add may offer, and what Add runs (CUST-210).</summary>
public sealed class ConnectorOnboardingTests
{
    private static WizardDefinition Card(string target, PlatformStatus status = PlatformStatus.NotCertified, string group = WizardGroups.Connectors) => new()
    {
        Target = target,
        Title = target,
        Group = group,
        PlatformStatus = status,
    };

    [Theory]
    [InlineData("claude-code", "claudecode")]
    [InlineData(" Claude-Code ", "claudecode")]
    [InlineData("CODEX", "codex")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Names_normalize_to_the_config_spelling(string? input, string expected)
    {
        Assert.Equal(expected, ConnectorOnboarding.Normalize(input));
    }

    [Theory]
    [InlineData("claudecode", "claude-code")]
    [InlineData("claude-code", "claude-code")]
    [InlineData("codex", "codex")]
    [InlineData("geminicli", "geminicli")]
    public void Only_claude_code_is_hyphenated_in_the_setup_command(string connector, string alias)
    {
        Assert.Equal(alias, ConnectorOnboarding.SetupAlias(connector));
    }

    [Fact]
    public void Add_runs_setup_for_the_alias_in_observe_mode_and_adds_alongside_the_others()
    {
        Assert.Equal(new[] { "setup", "claude-code", "--yes", "--mode", "observe" }, ConnectorOnboarding.AddArgv("claudecode"));
        Assert.Equal(new[] { "setup", "codex", "--yes", "--mode", "observe" }, ConnectorOnboarding.AddArgv("Codex"));
    }

    [Fact]
    public void Add_is_state_changing_restarts_the_gateway_and_never_replaces_or_carries_a_secret()
    {
        foreach (var id in new[] { "codex", "claudecode", "cursor" })
        {
            var argv = ConnectorOnboarding.AddArgv(id);

            Assert.Equal(CommandTier.StateChanging, CommandReview.ResolveTier(argv, CommandTier.StateChanging));
            Assert.True(CommandReview.RestartsGatewayFor(argv));
            Assert.DoesNotContain("--replace", argv);
            Assert.DoesNotContain(argv, a => a.Contains("key", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- detection (reads the AI-discovery scan; executes nothing) ----

    private static string Scan(params (string Connector, string State, string Category)[] signals) =>
        "{\"version\":2,\"signals\":{" + string.Join(
            ",",
            signals.Select((s, i) => $"\"sha256:{i}\":{{\"name\":\"n{i}\",\"category\":\"{s.Category}\",\"state\":\"{s.State}\",\"supported_connector\":\"{s.Connector}\"}}")) + "}}";

    [Fact]
    public void Detection_keeps_supported_connectors_of_agents_that_are_not_gone_in_the_windows_order()
    {
        var found = ConnectorOnboarding.ParseDetected(Scan(
            ("hermes", "seen", "active_process"),
            ("codex", "new", "active_process"),
            ("codex", "seen", "active_process"),
            ("claude-code", "changed", "active_process"),
            ("cursor", "gone", "active_process"),
            ("windsurf", "seen", "local_model"),
            ("", "seen", "active_process"),
            ("openclaw", "seen", "active_process"),
            ("zeptoclaw", "seen", "active_process")));

        Assert.Equal(new[] { "codex", "claudecode", "hermes" }, found.ToArray());
    }

    [Fact]
    public void Detection_reads_an_array_of_signals_and_a_file_with_none()
    {
        var array = "{\"signals\":[{\"supported_connector\":\"copilot\",\"state\":\"seen\"}]}";

        Assert.Equal(new[] { "copilot" }, ConnectorOnboarding.ParseDetected(array).ToArray());
        Assert.Empty(ConnectorOnboarding.ParseDetected("{\"version\":2}"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Detection_of_a_file_that_is_not_a_json_object_throws_for_the_caller_to_report(string text)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ConnectorOnboarding.ParseDetected(text));
    }

    // ---- what is offered here ----

    [Fact]
    public void Offered_connectors_follow_the_catalog_minus_proxies_unsupported_and_non_connectors()
    {
        var offered = ConnectorOnboarding.Offerable(new[]
        {
            Card("hermes"),
            Card("codex", PlatformStatus.Certified),
            Card("claude-code", PlatformStatus.Certified),
            Card("omnigent", PlatformStatus.Unsupported),
            Card("openclaw", PlatformStatus.Unsupported),
            Card("zeptoclaw"),
            Card("remove", PlatformStatus.NotApplicable),
            Card("guardrail", PlatformStatus.NotApplicable, WizardGroups.GuardrailAndPolicy),
        });

        Assert.Equal(new[] { "codex", "claudecode", "hermes" }, offered.Select(o => o.Id).ToArray());
        Assert.Equal("claude-code", offered[1].Alias);
        Assert.Equal("Claude Code", offered[1].Label);
    }

    [Fact]
    public void A_connector_that_is_not_certified_on_windows_is_offered_with_a_caution_and_a_certified_one_without()
    {
        var offered = ConnectorOnboarding.Offerable(new[] { Card("codex", PlatformStatus.Certified), Card("cursor") });

        Assert.False(offered.Single(o => o.Id == "codex").HasCaution);
        Assert.Contains("not certified on Windows", offered.Single(o => o.Id == "cursor").Caution, StringComparison.Ordinal);
    }

    [Fact]
    public void Before_the_catalog_is_read_the_built_in_list_stands_without_the_two_known_unsupported()
    {
        var offered = ConnectorOnboarding.Offerable(null).Select(o => o.Id).ToArray();

        Assert.Contains("codex", offered);
        Assert.Contains("claudecode", offered);
        Assert.DoesNotContain("openhands", offered);
        Assert.DoesNotContain("omnigent", offered);
        Assert.DoesNotContain("openclaw", offered);
    }

    [Fact]
    public void Unconfigured_is_detected_and_offered_minus_what_is_already_configured_with_names_compared_normalized()
    {
        var offerable = ConnectorOnboarding.Offerable(null);

        var unconfigured = ConnectorOnboarding.Unconfigured(
            detected: new[] { "codex", "claudecode", "hermes", "qodo" },
            configured: new[] { "Claude-Code" },
            offerable);

        Assert.Equal(new[] { "codex", "hermes" }, unconfigured.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Nothing_detected_or_everything_configured_leaves_nothing_to_add()
    {
        var offerable = ConnectorOnboarding.Offerable(null);

        Assert.Empty(ConnectorOnboarding.Unconfigured(Array.Empty<string>(), Array.Empty<string>(), offerable));
        Assert.Empty(ConnectorOnboarding.Unconfigured(new[] { "codex" }, new[] { "codex" }, offerable));
    }
}
