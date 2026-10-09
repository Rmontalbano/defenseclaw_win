using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="OverviewConfigFacts"/> (CUST-274): what the Overview's Configuration card, notices and Quick Actions read from config.yaml that the
/// typed model does not carry - human approval, the policy folder, the LLM provider and model, the AI Defense endpoint, the notification switch and
/// whether the connector map reads as a roster - resolved the way the 0.8.10 loader resolves them (<c>config.py</c>, <c>tui/app.py: _overview_config</c>).
/// A made-up config; every credential starts with <c>synth</c>, and the tests hold that none of them is on anything the type exposes.
/// </summary>
public class OverviewConfigFactsTests
{
    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthtoken", "synthkey", "synthpath-secret", "synthfrag" };

    private static OverviewConfigFacts Facts(string yaml) => OverviewConfigFacts.FromYaml(yaml);

    private static string Everything(OverviewConfigFacts facts) => JsonSerializer.Serialize(facts);

    // ---- human approval ----

    [Theory]
    [InlineData("guardrail:\n  hilt:\n    enabled: true\n    min_severity: critical\n", true, "CRITICAL", "ON (min CRITICAL)")]
    [InlineData("guardrail:\n  hilt:\n    enabled: true\n", true, "HIGH", "ON (min HIGH)")]
    [InlineData("guardrail:\n  hilt:\n    enabled: true\n    min_severity: \"\"\n", true, "HIGH", "ON (min HIGH)")]
    [InlineData("guardrail:\n  hilt:\n    enabled: false\n    min_severity: low\n", false, "LOW", "OFF")]
    [InlineData("guardrail:\n  hitl:\n    enabled: true\n    min_severity: medium\n", true, "MEDIUM", "ON (min MEDIUM)")]
    [InlineData("guardrail:\n  hilt: {}\n", false, "HIGH", "OFF")]
    [InlineData("guardrail:\n  enabled: true\n", false, "HIGH", "OFF")]
    [InlineData("guardrail:\n  hilt: true\n", false, "HIGH", "OFF")]
    [InlineData("llm: {}\n", false, "HIGH", "OFF")]
    public void The_global_block_reads_as_the_tuis_on_min_severity_or_off(string yaml, bool enabled, string severity, string text)
    {
        var approval = Facts(yaml).GlobalApproval;

        Assert.Equal(enabled, approval.Enabled);
        Assert.Equal(severity, approval.MinSeverity);
        Assert.Equal(text, approval.Text);
        Assert.Equal(approval, Facts(yaml).Approval);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("yes", true)]
    [InlineData("ON", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("maybe", false)]
    [InlineData("''", false)]
    public void The_switch_reads_the_words_the_runtimes_loader_reads_as_a_boolean(string value, bool enabled) =>
        Assert.Equal(enabled, Facts($"guardrail:\n  hilt:\n    enabled: {value}\n").GlobalApproval.Enabled);

    [Fact]
    public void With_one_connector_that_connectors_own_block_replaces_the_global_one()
    {
        var facts = Facts("""
            guardrail:
              hilt:
                enabled: false
              connectors:
                ClaudeCode:
                  mode: observe
                  hilt:
                    enabled: true
                    min_severity: medium
            """);

        Assert.Equal(new[] { "claudecode" }, facts.ConnectorNames);
        Assert.Equal("ON (min MEDIUM)", facts.Approval.Text);
        Assert.Equal("OFF", facts.GlobalApproval.Text);
    }

    [Fact]
    public void A_connector_block_that_is_there_and_empty_is_off_not_inherit_and_one_that_is_absent_inherits()
    {
        var empty = Facts("guardrail:\n  hilt:\n    enabled: true\n  connectors:\n    codex:\n      hilt: {}\n");
        var absent = Facts("guardrail:\n  hilt:\n    enabled: true\n    min_severity: low\n  connectors:\n    codex:\n      mode: observe\n");

        Assert.Equal("OFF", empty.Approval.Text);
        Assert.Equal("ON (min LOW)", absent.Approval.Text);
    }

    [Fact]
    public void With_several_connectors_only_the_global_block_is_shown_as_in_the_tui()
    {
        var facts = Facts("""
            guardrail:
              hilt:
                enabled: true
                min_severity: critical
              connectors:
                claudecode:
                  hilt:
                    enabled: false
                codex:
                  mode: action
            """);

        Assert.Equal(new[] { "claudecode", "codex" }, facts.ConnectorNames);
        Assert.Equal("ON (min CRITICAL)", facts.Approval.Text);
        Assert.False(facts.ConnectorApproval["claudecode"].Enabled);
    }

    // ---- the policy folder ----

    [Theory]
    [InlineData("policy_dir: D:\\policies\\acme\n", "D:\\policies\\acme")]
    [InlineData("policy_dir: ~/.defenseclaw/policies\n", "~/.defenseclaw/policies")]
    [InlineData("policy_dir:\n", "")]
    [InlineData("policy_dir: ''\n", "")]
    public void The_policy_folder_is_as_written_and_empty_when_the_key_is_empty(string yaml, string expected) =>
        Assert.Equal(expected, Facts(yaml).PolicyDirectory);

    [Fact]
    public void A_config_that_does_not_set_the_policy_folder_leaves_it_to_the_runtimes_default()
    {
        Assert.Null(Facts("guardrail:\n  enabled: true\n").PolicyDirectory);
        Assert.Null(OverviewConfigFacts.Empty.PolicyDirectory);
    }

    // ---- the LLM ----

    [Fact]
    public void The_llm_is_the_unified_block_first()
    {
        var facts = Facts("llm:\n  provider: openai\n  model: gpt-4o\ninspect_llm:\n  provider: anthropic\n  model: claude\ndefault_llm_model: legacy\n");

        Assert.Equal("openai", facts.LlmProvider);
        Assert.Equal("gpt-4o", facts.LlmModel);
    }

    [Fact]
    public void The_legacy_fields_fill_what_the_unified_block_leaves_out_in_the_loaders_order()
    {
        // model: llm.model, else default_llm_model, else inspect_llm.model; provider: llm.provider, else inspect_llm.provider.
        var withDefault = Facts("llm:\n  provider: ''\ndefault_llm_model: from-default\ninspect_llm:\n  provider: bedrock\n  model: from-inspect\n");
        Assert.Equal("bedrock", withDefault.LlmProvider);
        Assert.Equal("from-default", withDefault.LlmModel);

        var inspectOnly = Facts("inspect_llm:\n  provider: bedrock\n  model: from-inspect\n");
        Assert.Equal("bedrock", inspectOnly.LlmProvider);
        Assert.Equal("from-inspect", inspectOnly.LlmModel);
    }

    [Fact]
    public void A_config_with_no_llm_names_neither_and_the_card_draws_no_row()
    {
        var facts = Facts("llm:\n  api_key_env: EXAMPLE_KEY\n");

        Assert.Equal(string.Empty, facts.LlmProvider);
        Assert.Equal(string.Empty, facts.LlmModel);
    }

    // ---- AI Defense ----

    [Fact]
    public void The_ai_defense_endpoint_is_its_host_and_port_and_nothing_else()
    {
        var facts = Facts("cisco_ai_defense:\n  endpoint: https://synthuser:synthpass@aidefense.example.test:8443/synthpath-secret/v1?token=synthtoken#synthfrag\n  api_key_env: EXAMPLE_KEY\n");

        Assert.Equal("aidefense.example.test:8443", facts.AiDefenseHost);
        var everything = Everything(facts);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("endpoint: https://eu.api.example.test", "eu.api.example.test")]
    [InlineData("endpoint: aidefense.example.test:9000", "aidefense.example.test:9000")]
    [InlineData("endpoint: 'https://[::1]:8443/x'", "[::1]:8443")]
    [InlineData("endpoint: \"\"", "")]
    [InlineData("endpoint:", "")]
    [InlineData("endpoint: https://exa mple.test/x", EndpointDisplay.Unreadable)]
    public void The_ai_defense_endpoint_is_reduced_the_way_every_address_is(string line, string expected) =>
        Assert.Equal(expected, Facts($"cisco_ai_defense:\n  {line}\n").AiDefenseHost);

    [Theory]
    [InlineData("cisco_ai_defense:\n  api_key_env: EXAMPLE_KEY\n")]
    [InlineData("cisco_ai_defense: {}\n")]
    [InlineData("guardrail:\n  enabled: true\n")]
    public void An_endpoint_the_config_does_not_set_is_null_so_the_default_can_be_named(string yaml) =>
        Assert.Null(Facts(yaml).AiDefenseHost);

    // ---- notifications ----

    [Theory]
    [InlineData("notifications:\n  enabled: false\n", false)]
    [InlineData("notifications:\n  enabled: true\n", true)]
    [InlineData("notifications:\n  enabled: off\n  block_enforced: true\n", false)]
    [InlineData("notifications:\n  enabled:\n", false)]
    public void The_switch_is_what_the_config_says(string yaml, bool expected) =>
        Assert.Equal(expected, Facts(yaml).NotificationsEnabled);

    [Theory]
    [InlineData("guardrail:\n  enabled: true\n")]
    [InlineData("notifications:\n  block_enforced: true\n")]
    [InlineData("notifications: {}\n")]
    [InlineData("notifications:\n")]
    public void A_config_that_is_silent_leaves_the_switch_to_the_runtimes_default(string yaml) =>
        Assert.Null(Facts(yaml).NotificationsEnabled);

    // ---- the roster ----

    [Fact]
    public void Names_that_differ_only_by_case_are_one_connector_and_the_runtime_rejects_the_map()
    {
        var facts = Facts("guardrail:\n  connectors:\n    claudecode:\n      mode: observe\n    ClaudeCode:\n      mode: action\n");

        Assert.Equal(new[] { "claudecode" }, facts.ConnectorNames);
        Assert.Equal(
            "guardrail.connectors: 'ClaudeCode' and 'claudecode' refer to the same connector 'claudecode'; keep only one",
            facts.RosterProblem);
    }

    [Theory]
    [InlineData("open-hands", "openhands")]
    [InlineData("open_hands", "openhands")]
    public void The_two_spellings_of_openhands_are_one_connector(string alias, string other)
    {
        var facts = Facts($"guardrail:\n  connectors:\n    {other}: {{}}\n    {alias}: {{}}\n");

        Assert.Equal(new[] { "openhands" }, facts.ConnectorNames);
        Assert.NotNull(facts.RosterProblem);
        Assert.Contains("refer to the same connector 'openhands'", facts.RosterProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_connector_name_is_rejected()
    {
        var facts = Facts("guardrail:\n  connectors:\n    '': {}\n    codex: {}\n");

        Assert.Equal("guardrail.connectors: empty connector name is not allowed", facts.RosterProblem);
        Assert.Equal(new[] { "codex" }, facts.ConnectorNames);
    }

    [Fact]
    public void A_clean_map_has_no_problem_and_neither_has_a_config_without_one()
    {
        Assert.Null(Facts("guardrail:\n  connectors:\n    claudecode: {}\n    codex: {}\n    openhands: {}\n").RosterProblem);
        Assert.Null(Facts("guardrail:\n  enabled: true\n").RosterProblem);
        Assert.Null(OverviewConfigFacts.Empty.RosterProblem);
    }

    [Fact]
    public void A_name_cannot_smuggle_a_control_character_into_the_sentence()
    {
        var facts = Facts("guardrail:\n  connectors:\n    \"a\\u202eb\": {}\n    \"A\\u202eB\": {}\n");

        Assert.NotNull(facts.RosterProblem);
        Assert.DoesNotContain('\u202e', facts.RosterProblem!);
        Assert.Contains("\\u202E", facts.RosterProblem, StringComparison.Ordinal);
    }

    // ---- what is never read, and what is never an error ----

    [Fact]
    public void No_secret_in_the_file_is_on_anything_the_type_exposes()
    {
        var facts = Facts("""
            llm:
              provider: openai
              model: gpt-4o
              api_key: synthkey
              api_key_env: SYNTH_ENV
            inspect_llm:
              api_key: synthkey
            cisco_ai_defense:
              api_key: synthkey
              endpoint: https://synthuser:synthpass@host.example.test/synthpath-secret
            gateway:
              token: synthtoken
            guardrail:
              hilt:
                enabled: true
            """);

        Assert.Equal("host.example.test", facts.AiDefenseHost);
        var everything = Everything(facts);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("- a\n- b\n")]
    [InlineData("just a string")]
    [InlineData("guardrail: [unclosed\n")]
    [InlineData("guardrail:\n  hilt: [1, 2]\n  connectors: 7\n")]
    [InlineData("guardrail:\n  hilt:\n    enabled: true\n  hilt:\n    enabled: false\n")]
    [InlineData("a: !!python/object:os.system x\n")]
    [InlineData("a: &x\n  b: 1\nc: *y\n")]
    [InlineData("\tguardrail:\n  enabled: true\n")]
    [InlineData("{{{{{{{{{{{{")]
    public void Text_that_is_not_a_mapping_says_nothing_and_never_throws(string? yaml)
    {
        var facts = OverviewConfigFacts.FromYaml(yaml);

        Assert.Equal("OFF", facts.Approval.Text);
        Assert.Empty(facts.ConnectorNames);
        Assert.Null(facts.PolicyDirectory);
        Assert.Null(facts.AiDefenseHost);
        Assert.Null(facts.NotificationsEnabled);
        Assert.Null(facts.RosterProblem);
    }

    [Fact]
    public void A_file_over_the_limit_says_nothing()
    {
        var huge = "policy_dir: x\n" + new string('#', OverviewConfigFacts.MaxLength);

        Assert.Same(OverviewConfigFacts.Empty, OverviewConfigFacts.FromYaml(huge));
    }

    [Fact]
    public void A_loaded_document_and_a_missing_one_are_read_the_same_way()
    {
        var document = ConfigStore.Parse("policy_dir: D:\\p\nnotifications:\n  enabled: false\n");

        Assert.Equal("D:\\p", OverviewConfigFacts.FromConfig(document).PolicyDirectory);
        Assert.False(OverviewConfigFacts.FromConfig(document).NotificationsEnabled);
        Assert.Same(OverviewConfigFacts.Empty, OverviewConfigFacts.FromConfig(null));
    }

    [Theory]
    [InlineData("ClaudeCode", "claudecode")]
    [InlineData("  Codex ", "codex")]
    [InlineData("Open-Hands", "openhands")]
    [InlineData("OPEN_HANDS", "openhands")]
    [InlineData("", "openclaw")]
    [InlineData(null, "openclaw")]
    public void A_connector_name_is_keyed_the_way_the_runtime_keys_it(string? name, string expected) =>
        Assert.Equal(expected, OverviewConfigFacts.Normalize(name));
}
