using DefenseClaw.Core.Config;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="SetupStateLines"/> (CUST-271): the one-line "here is what is configured today" of the 0.8.10 TUI's Setup goal menu
/// (<c>wizard_state_summary</c>), for the Setup hub's cards. The long form is the TUI's own sentence, character for character; the short form is
/// what fits a tile. A made-up config throughout.
/// </summary>
public class SetupStateLinesTests
{
    private static ConfigDocument Config(string yaml) => ConfigStore.Parse(yaml);

    // ---- the LLM card ----

    [Fact]
    public void The_llm_card_reads_main_judge_and_connectors_in_the_tuis_words()
    {
        var state = SetupStateLines.Llm(Config("""
            llm:
              provider: openai
              model: gpt-synthetic
            guardrail:
              judge:
                model: judge-synthetic
              connectors:
                codex: {}
                claudecode: {}
            """));

        Assert.Equal("Main: openai/gpt-synthetic  ·  Judge: judge-synthetic  ·  Connectors: claudecode, codex (judge only)", state.Detail);
        Assert.Equal("Main: openai/gpt-synthetic · Judge: judge-synthetic", state.Text);
    }

    [Theory]
    [InlineData("llm:\n  model: only-model\n", "Main: only-model")]
    [InlineData("llm:\n  provider: only-provider\n", "Main: only-provider")]
    [InlineData("llm: {}\n", "Main: not set")]
    [InlineData("", "Main: not set")]
    public void The_main_model_is_provider_slash_model_else_whichever_is_set_else_not_set(string yaml, string start)
    {
        var state = SetupStateLines.Llm(Config(yaml));

        Assert.StartsWith(start + "  ·  Judge: not set  ·  Connectors: none (judge only)", state.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proxy_connector_makes_the_agent_model_available_too()
    {
        var state = SetupStateLines.Llm(Config("guardrail:\n  connectors:\n    openclaw: {}\n"));

        Assert.EndsWith("Connectors: openclaw (judge+agent available)", state.Detail, StringComparison.Ordinal);
    }

    // ---- the guardrail card ----

    [Fact]
    public void The_guardrail_card_says_on_off_mode_and_strategy()
    {
        var on = SetupStateLines.Guardrail(Config("guardrail:\n  enabled: true\n  mode: action\n  detection_strategy: regex_judge\n"));
        var silent = SetupStateLines.Guardrail(Config(string.Empty));

        Assert.Equal("Guardrail: on  ·  Mode: action  ·  Strategy: regex_judge", on.Detail);
        Assert.Equal("On · action · regex_judge", on.Text);
        Assert.Equal("Guardrail: off  ·  Mode: observe  ·  Strategy: regex_only", silent.Detail);
        Assert.Equal("Off · observe · regex_only", silent.Text);
    }

    // ---- the connectors ----

    [Fact]
    public void The_roster_lists_the_configured_connectors_sorted_or_says_none_is_set()
    {
        Assert.Equal("Active connectors: claudecode, codex", SetupStateLines.Roster(Config("guardrail:\n  connectors:\n    codex: {}\n    Claude-Code: {}\n")).Detail);
        Assert.Equal("Active: claudecode, codex", SetupStateLines.Roster(Config("guardrail:\n  connectors:\n    codex: {}\n    claudecode: {}\n")).Text);
        Assert.Equal("Active connectors: not set", SetupStateLines.Roster(Config(string.Empty)).Detail);
        Assert.Equal("No connector configured", SetupStateLines.Roster(Config(string.Empty)).Text);
    }

    [Fact]
    public void A_single_connector_install_with_no_map_still_has_its_one_active_connector()
    {
        Assert.Equal("Active connectors: codex", SetupStateLines.Roster(Config("claw:\n  mode: Codex\n")).Detail);
    }

    [Fact]
    public void One_connectors_card_says_whether_it_is_configured_and_how()
    {
        var yaml = """
            guardrail:
              connectors:
                claudecode:
                  mode: action
                  hook_fail_mode: closed
                codex:
                  mode: observe
            """;

        var claude = SetupStateLines.Connector(Config(yaml), "claude-code");
        var codex = SetupStateLines.Connector(Config(yaml), "codex");
        var cursor = SetupStateLines.Connector(Config(yaml), "cursor");

        Assert.Equal("Configured · action · fail-closed", claude.Text);
        Assert.Equal("claudecode: configured (action, fail-closed). The wizard starts from these values.", claude.Detail);
        Assert.Equal("Configured · observe", codex.Text);
        Assert.Equal("Not configured", cursor.Text);
        Assert.Contains("cursor: not in guardrail.connectors", cursor.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connector_the_file_names_with_no_settings_is_configured_without_a_mode()
    {
        var state = SetupStateLines.Connector(Config("guardrail:\n  connectors:\n    hermes:\n"), "hermes");

        Assert.Equal("Configured", state.Text);
    }

    // ---- notifications ----

    [Fact]
    public void The_switch_says_on_or_off_and_when_the_file_is_silent_that_the_runtimes_default_applies()
    {
        var off = SetupStateLines.NotificationsSwitch(Config("notifications:\n  enabled: false\n"));
        var silent = SetupStateLines.NotificationsSwitch(Config(string.Empty));

        Assert.Equal("Desktop notifications off", off.Text);
        Assert.StartsWith("Desktop notifications: off.", off.Detail, StringComparison.Ordinal);
        Assert.Equal("Desktop notifications on", silent.Text);
        Assert.Contains("config.yaml does not say", silent.Detail, StringComparison.Ordinal);
        Assert.Contains("not the tray alerts of this app", silent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_categories_count_how_many_are_on_and_name_the_ones_that_are_off()
    {
        var defaults = SetupStateLines.NotificationCategories(Config(string.Empty));
        var some = SetupStateLines.NotificationCategories(Config("notifications:\n  block_would_block: true\n  hitl_approval: false\n  sources:\n    hook: no\n"));

        Assert.Equal("5 of 6 on", defaults.Text);
        Assert.Equal("Off: Block (would-block / observe).", defaults.Detail);
        Assert.Equal("4 of 6 on", some.Text);
        Assert.Equal("Off: HITL approval, Source: hooks.", some.Detail);
    }

    [Fact]
    public void With_the_switch_off_the_categories_say_none_of_them_raises_anything()
    {
        var state = SetupStateLines.NotificationCategories(Config("notifications:\n  enabled: false\n"));

        Assert.Equal("5 of 6 on · switch off", state.Text);
        Assert.EndsWith("The master switch is off, so none of them raises one.", state.Detail, StringComparison.Ordinal);
    }

    // ---- what comes out of the file is drawn visibly ----

    [Fact]
    public void A_control_or_bidirectional_character_in_a_value_is_written_out_not_drawn()
    {
        var state = SetupStateLines.Guardrail(Config("guardrail:\n  enabled: true\n  mode: \"obs\\u202Eerve\"\n"));

        Assert.DoesNotContain('‮', state.Text);
        Assert.DoesNotContain('‮', state.Detail);
    }

    [Fact]
    public void A_document_that_cannot_be_read_gives_the_not_set_lines_not_an_error()
    {
        Assert.Equal("Active connectors: not set", SetupStateLines.Roster(null).Detail);
        Assert.Equal("Main: not set  ·  Judge: not set  ·  Connectors: none (judge only)", SetupStateLines.Llm(null).Detail);
    }
}
