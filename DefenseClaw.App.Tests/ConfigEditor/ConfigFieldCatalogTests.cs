using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-268: the choice lists and hints are the 0.8.10 TUI's (<c>cli_choices.py</c>, <c>panels/setup.py</c>), as data. These tests pin the lists
/// the issue names - <c>llm.provider</c>, <c>claw.mode</c> for Windows, the hook fail modes, the guardrail mode, the action-matrix cells - and the
/// rules around them (a value the list does not have is kept; a read-only key says why).
/// </summary>
public class ConfigFieldCatalogTests
{
    private static readonly ConfigChoiceProfile Windows = new("windows", false);
    private static readonly ConfigChoiceProfile WindowsPin = new("windows", true);
    private static readonly ConfigChoiceProfile Mac = new("darwin", false);

    private static IReadOnlyList<string> Options(string path, ConfigChoiceProfile? profile = null)
    {
        var info = ConfigFieldCatalog.Resolve(path, profile ?? Windows);
        Assert.NotNull(info);
        Assert.True(info!.IsChoice, path + " is not a choice");
        return info.Options!;
    }

    // ------------------------------------------------------------------ llm.provider

    [Fact]
    public void The_unified_llm_provider_is_the_tuis_sixteen_in_its_order()
    {
        Assert.Equal(
            new[]
            {
                "anthropic", "openai", "openrouter", "azure", "gemini", "gemini-openai", "groq", "mistral", "cohere", "deepseek", "xai",
                "bedrock", "vertex_ai", "ollama", "vllm", "lm_studio",
            },
            Options("llm.provider"));
    }

    [Fact]
    public void A_component_override_offers_the_same_providers_after_a_blank_that_inherits()
    {
        foreach (var prefix in new[] { "guardrail.llm", "guardrail.judge.llm", "scanners.skill_scanner.llm", "scanners.mcp_scanner.llm", "scanners.plugin_llm" })
        {
            var options = Options(prefix + ".provider");

            Assert.Equal(17, options.Count);
            Assert.Equal(string.Empty, options[0]);
            Assert.Equal(ConfigFieldCatalog.LlmProviders, options.Skip(1));
            Assert.Equal("Blank inherits Unified LLM.", ConfigFieldCatalog.Resolve(prefix + ".provider", Windows)!.Hint);
        }
    }

    [Fact]
    public void The_providers_the_wizard_has_and_the_editors_list_does_not_are_not_in_the_list()
    {
        // WIZARD_LLM_PROVIDERS also has fireworks_ai, perplexity, huggingface, replicate, together_ai and cerebras; the config panel's field does not.
        Assert.DoesNotContain("fireworks_ai", Options("llm.provider"));
        Assert.DoesNotContain("cerebras", Options("llm.provider"));
    }

    // ------------------------------------------------------------------ claw.mode and the Windows filter

    [Fact]
    public void Claw_mode_on_windows_is_codex_and_claudecode_never_openclaw_or_zeptoclaw()
    {
        var options = Options("claw.mode", Windows);

        Assert.Equal(new[] { "codex", "claudecode" }, options);
        Assert.DoesNotContain("openclaw", options);
        Assert.DoesNotContain("zeptoclaw", options);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("Windows")]
    [InlineData("win32")]
    [InlineData(" windows ")]
    public void Any_spelling_of_windows_filters(string os) =>
        Assert.Equal(new[] { "codex", "claudecode" }, Options("claw.mode", new ConfigChoiceProfile(os, false)));

    [Theory]
    [InlineData("darwin")]
    [InlineData("linux")]
    public void Everywhere_else_every_connector_stays(string os)
    {
        var options = Options("claw.mode", new ConfigChoiceProfile(os, false));

        Assert.Equal(
            new[]
            {
                "openclaw", "zeptoclaw", "codex", "claudecode", "hermes", "cursor", "windsurf", "geminicli", "copilot", "openhands", "antigravity", "opencode", "omnigent",
            },
            options);
    }

    [Fact]
    public void On_windows_the_not_certified_and_unsupported_connectors_are_hidden_as_well()
    {
        // supported_connector_choices keeps only what the platform table calls supported or preview. 0.8.10 has two such connectors on Windows.
        var options = Options("claw.mode", Windows);

        foreach (var hidden in new[] { "cursor", "windsurf", "geminicli", "copilot", "antigravity", "opencode", "hermes", "openhands", "omnigent" })
        {
            Assert.DoesNotContain(hidden, options);
        }
    }

    [Fact]
    public void The_pinned_runtime_has_its_own_connectors_and_its_own_windows_table()
    {
        Assert.Equal(
            new[] { "codex", "claudecode", "hermes", "cursor", "devin", "copilot", "antigravity", "opencode", "amp", "omnigent", "kiro" },
            Options("claw.mode", WindowsPin));

        var everywhere = Options("claw.mode", new ConfigChoiceProfile("darwin", true));
        Assert.Equal(14, everywhere.Count);
        Assert.Contains("kiro", everywhere);
        Assert.DoesNotContain("windsurf", everywhere);
        Assert.DoesNotContain("openclaw", Options("claw.mode", WindowsPin));
        Assert.DoesNotContain("zeptoclaw", Options("claw.mode", WindowsPin));
        Assert.DoesNotContain("openhands", Options("claw.mode", WindowsPin));
    }

    [Fact]
    public void The_guardrail_connector_is_a_blank_that_follows_claw_mode_then_the_same_filtered_list()
    {
        Assert.Equal(new[] { string.Empty, "codex", "claudecode" }, Options("guardrail.connector", Windows));
        Assert.Equal(13 + 1, Options("guardrail.connector", Mac).Count);
        Assert.Equal("Blank follows claw.mode.", ConfigFieldCatalog.Resolve("guardrail.connector", Windows)!.Hint);
    }

    [Fact]
    public void The_host_profile_is_this_machine_with_the_0810_set()
    {
        var host = ConfigChoiceProfile.ForHost();

        Assert.False(host.ExtendedConnectors);
        Assert.Equal("windows", host.OsName);
        Assert.Equal(new[] { "codex", "claudecode" }, ConfigFieldCatalog.SupportedConnectors(host));

        // No profile at all means the same thing.
        Assert.Equal(new[] { "codex", "claudecode" }, ConfigFieldCatalog.Resolve("claw.mode")!.Options);
    }

    // ------------------------------------------------------------------ hook fail modes

    [Theory]
    [InlineData("claude_code.fail_mode")]
    [InlineData("codex.fail_mode")]
    [InlineData("connector_hooks.cursor.fail_mode")]
    [InlineData("connector_hooks.some-future-agent.fail_mode")]
    public void The_agent_hook_fail_mode_is_blank_open_or_closed(string path)
    {
        Assert.Equal(new[] { string.Empty, "open", "closed" }, Options(path));
        Assert.Equal("Legacy policy-layer hint.", ConfigFieldCatalog.Resolve(path, Windows)!.Hint);
    }

    [Theory]
    [InlineData("claude_code.mode")]
    [InlineData("codex.mode")]
    [InlineData("connector_hooks.cursor.mode")]
    public void The_agent_hook_mode_is_blank_observe_or_action(string path) =>
        Assert.Equal(new[] { string.Empty, "observe", "action" }, Options(path));

    [Theory]
    [InlineData("guardrail.hook_fail_mode")]
    [InlineData("guardrail.connectors.claudecode.hook_fail_mode")]
    [InlineData("guardrail.connectors.codex.hook_fail_mode")]
    public void The_guardrail_hook_fail_mode_is_not_editable_here_and_says_where_it_is(string path)
    {
        var info = ConfigFieldCatalog.Resolve(path, Windows)!;

        Assert.False(info.IsChoice);
        Assert.NotNull(info.ReadOnlyReason);
        Assert.Contains("Setup panel", info.ReadOnlyReason, StringComparison.Ordinal);
        Assert.Contains("guardrail fail-mode", info.ReadOnlyReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_agent_hooks_hint_names_the_agent()
    {
        Assert.Equal("Claude Code hooks master switch.", ConfigFieldCatalog.Resolve("claude_code.enabled", Windows)!.Hint);
        Assert.Equal("Codex hooks master switch.", ConfigFieldCatalog.Resolve("codex.enabled", Windows)!.Hint);
        Assert.Equal("cursor hooks master switch.", ConfigFieldCatalog.Resolve("connector_hooks.cursor.enabled", Windows)!.Hint);
    }

    // ------------------------------------------------------------------ guardrail mode and the rest of the guardrail section

    [Fact]
    public void The_guardrail_mode_is_observe_or_action_with_the_tuis_hint()
    {
        Assert.Equal(new[] { "observe", "action" }, Options("guardrail.mode"));
        Assert.Equal("observe=log only; action=block.", ConfigFieldCatalog.Resolve("guardrail.mode", Windows)!.Hint);
    }

    [Theory]
    [InlineData("guardrail.scanner_mode", new[] { "local", "remote", "both" })]
    [InlineData("guardrail.hilt.min_severity", new[] { "HIGH", "MEDIUM", "LOW", "CRITICAL" })]
    [InlineData("guardrail.detection_strategy", new[] { "regex_only", "regex_judge", "judge_first" })]
    [InlineData("guardrail.detection_strategy_prompt", new[] { "", "regex_only", "regex_judge", "judge_first" })]
    [InlineData("guardrail.detection_strategy_completion", new[] { "", "regex_only", "regex_judge", "judge_first" })]
    [InlineData("guardrail.detection_strategy_tool_call", new[] { "", "regex_only", "regex_judge", "judge_first" })]
    [InlineData("asset_policy.mode", new[] { "observe", "action" })]
    [InlineData("asset_policy.skill.default", new[] { "allow", "deny" })]
    [InlineData("asset_policy.mcp.registry_empty_action", new[] { "deny", "allow" })]
    [InlineData("asset_policy.plugin.default", new[] { "allow", "deny" })]
    [InlineData("asset_policy.mcp.runtime_detection.unknown_terminal_mcp", new[] { "observe", "action" })]
    [InlineData("openshell.mode", new[] { "", "docker", "standalone" })]
    public void The_other_fixed_lists_are_the_tuis(string path, string[] expected) =>
        Assert.Equal(expected, Options(path));

    [Fact]
    public void A_connectors_overrides_use_the_connectors_name_in_the_hint()
    {
        var mode = ConfigFieldCatalog.Resolve("guardrail.connectors.claudecode.mode", Windows)!;
        Assert.Equal(new[] { "observe", "action" }, mode.Options);
        Assert.Equal("Per-connector mode for claudecode (blank inherits the global mode).", mode.Hint);

        var severity = ConfigFieldCatalog.Resolve("guardrail.connectors.codex.hilt.min_severity", Windows)!;
        Assert.Equal(new[] { "HIGH", "MEDIUM", "LOW", "CRITICAL" }, severity.Options);
        Assert.Equal("Minimum severity for codex approval prompts.", severity.Hint);

        Assert.Equal(
            "Per-connector block message for codex (blank inherits the global message).",
            ConfigFieldCatalog.Resolve("guardrail.connectors.codex.block_message", Windows)!.Hint);
    }

    [Fact]
    public void An_asset_policy_override_names_the_connector_and_the_asset()
    {
        var mode = ConfigFieldCatalog.Resolve("asset_policy.connectors.cursor.mode", Windows)!;
        Assert.Equal(new[] { string.Empty, "observe", "action" }, mode.Options);
        Assert.Equal("Per-connector asset-policy mode for cursor; blank inherits the global mode.", mode.Hint);

        var mcp = ConfigFieldCatalog.Resolve("asset_policy.connectors.cursor.mcp.default", Windows)!;
        Assert.Equal(new[] { string.Empty, "allow", "deny" }, mcp.Options);
        Assert.Equal("MCP override for cursor; blank inherits the global mcp policy.", mcp.Hint);

        var skill = ConfigFieldCatalog.Resolve("asset_policy.connectors.cursor.skill.registry_empty_action", Windows)!;
        Assert.Equal(new[] { string.Empty, "deny", "warn", "allow", "block" }, skill.Options);
        Assert.Equal("Skill override for cursor; blank inherits the global skill policy.", skill.Hint);
    }

    // ------------------------------------------------------------------ the action matrices

    public static TheoryData<string, string, string> MatrixCells()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var matrix in new[] { "skill_actions", "mcp_actions", "plugin_actions" })
        {
            foreach (var severity in new[] { "critical", "high", "medium", "low", "info" })
            {
                foreach (var column in new[] { "file", "runtime", "install" })
                {
                    data.Add(matrix, severity, column);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MatrixCells))]
    public void Every_cell_of_every_matrix_is_a_choice_with_its_columns_list_and_the_tuis_hint(string matrix, string severity, string column)
    {
        var info = ConfigFieldCatalog.Resolve($"{matrix}.{severity}.{column}", Windows)!;
        var upper = severity.ToUpperInvariant();

        switch (column)
        {
            case "file":
                Assert.Equal(new[] { "none", "quarantine" }, info.Options);
                Assert.Equal($"On {upper}: quarantine moves the artifact; none leaves it in place.", info.Hint);
                break;
            case "runtime":
                Assert.Equal(new[] { "enable", "disable" }, info.Options);
                Assert.Equal($"On {upper}: disable stops runtime invocation; enable keeps it live.", info.Hint);
                break;
            default:
                Assert.Equal(new[] { "none", "block", "allow" }, info.Options);
                Assert.Equal($"On {upper}: block rejects installs; allow permits; none defers.", info.Hint);
                break;
        }
    }

    [Fact]
    public void A_severity_the_matrix_does_not_have_is_not_a_cell()
    {
        Assert.Null(ConfigFieldCatalog.Resolve("skill_actions.urgent.file", Windows));
        Assert.Null(ConfigFieldCatalog.Resolve("skill_actions.critical.speed", Windows));
        Assert.Null(ConfigFieldCatalog.Resolve("other_actions.critical.file", Windows));
    }

    // ------------------------------------------------------------------ what is a hint and not a choice

    [Fact]
    public void A_number_a_switch_and_a_text_box_have_a_hint_and_no_list()
    {
        foreach (var (path, hint) in new[]
                 {
                     ("gateway.api_port", "REST sidecar port."),
                     ("gateway.tls_skip_verify", "Skip cert verification."),
                     ("llm.model", "Model identifier."),
                     ("notifications.dedup_window", "Duration string like 30s, 1m, or 500ms."),
                     ("ai_discovery.mode", "passive or enhanced."),
                     ("cisco_ai_defense.endpoint", "Cisco AI Defense API endpoint."),
                 })
        {
            var info = ConfigFieldCatalog.Resolve(path, Windows)!;
            Assert.Equal(hint, info.Hint);
            Assert.False(info.IsChoice);
            Assert.Null(info.ReadOnlyReason);
        }
    }

    [Theory]
    [InlineData("scanners.skill_scanner.policy")]
    [InlineData("openshell.auto_pair")]
    [InlineData("openshell.host_networking")]
    [InlineData("asset_policy.connectors.cursor.mcp.registry_required")]
    public void The_policy_choice_and_the_tri_state_booleans_are_deliberately_not_lists(string path)
    {
        var info = ConfigFieldCatalog.Resolve(path, Windows)!;

        Assert.False(info.IsChoice);
        Assert.False(string.IsNullOrEmpty(info.Hint));
    }

    [Theory]
    [InlineData("nothing.the.tui.lists")]
    [InlineData("llm")]
    [InlineData("")]
    [InlineData("guardrail.connectors.claudecode")]
    [InlineData("guardrail.connectors.claudecode.mode.extra")]
    [InlineData("claw")]
    public void A_key_the_tui_does_not_list_has_nothing(string path) =>
        Assert.Null(ConfigFieldCatalog.Resolve(path, Windows));

    [Fact]
    public void The_catalogue_covers_the_tuis_fields_and_every_choice_has_a_list_that_names_each_value_once()
    {
        Assert.True(ConfigFieldCatalog.ExactPaths.Count >= 190, $"only {ConfigFieldCatalog.ExactPaths.Count} exact keys");

        foreach (var path in ConfigFieldCatalog.ExactPaths)
        {
            var info = ConfigFieldCatalog.Resolve(path, Windows)!;

            if (info.IsChoice)
            {
                Assert.NotEmpty(info.Options!);
                Assert.Equal(info.Options!.Count, info.Options.Distinct(StringComparer.Ordinal).Count());
            }

            Assert.True(info.IsChoice || info.ReadOnlyReason is not null || info.Hint.Length > 0, path + " has neither a hint nor a list");
        }
    }

    // ------------------------------------------------------------------ the combo's items

    [Fact]
    public void The_items_are_the_options_in_order_with_the_blank_named()
    {
        var items = ConfigFieldCatalog.ChoiceItems(new[] { string.Empty, "open", "closed" }, "open");

        Assert.Equal(new[] { string.Empty, "open", "closed" }, items.Select(i => i.Value));
        Assert.Equal(new[] { "(blank)", "open", "closed" }, items.Select(i => i.Label));
    }

    [Fact]
    public void A_current_value_the_list_does_not_have_is_one_more_item_and_nothing_is_replaced()
    {
        var items = ConfigFieldCatalog.ChoiceItems(new[] { "codex", "claudecode" }, "openclaw");

        Assert.Equal(new[] { "codex", "claudecode", "openclaw" }, items.Select(i => i.Value));
        Assert.Equal("openclaw", items[^1].Label);
    }

    [Fact]
    public void A_blank_current_value_that_is_not_an_option_is_kept_too()
    {
        var items = ConfigFieldCatalog.ChoiceItems(new[] { "observe", "action" }, string.Empty);

        Assert.Equal(new[] { "observe", "action", string.Empty }, items.Select(i => i.Value));
        Assert.Equal("(blank)", items[^1].Label);
    }

    [Fact]
    public void Values_are_compared_exactly_so_a_different_case_is_a_value_of_its_own()
    {
        var items = ConfigFieldCatalog.ChoiceItems(new[] { "HIGH", "MEDIUM" }, "high");

        Assert.Equal(new[] { "HIGH", "MEDIUM", "high" }, items.Select(i => i.Value));
    }
}
