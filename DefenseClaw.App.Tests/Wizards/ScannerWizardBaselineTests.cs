using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// Synthetic <c>setup skill-scanner</c> / <c>setup mcp-scanner</c> help screens, written from the option lists in <c>cmd_setup.py</c> at 1.0.4 and
/// 0.8.10 (the provider lists are shortened), and the definitions the catalog would build from them.
/// </summary>
internal static class ScannerWizardSamples
{
    public static readonly string SkillHelp104 = LineEndings.Normalize("""
        Usage: defenseclaw setup skill-scanner [OPTIONS]

          Configure skill-scanner analyzers, API keys, and policy.

        Options:
          --use-llm / --no-use-llm        Enable (or disable) the LLM analyzer
          --use-behavioral / --no-use-behavioral
                                          Enable (or disable) the behavioral analyzer
          --enable-meta / --no-enable-meta
                                          Enable (or disable) the meta-analyzer
          --use-trigger / --no-use-trigger
                                          Enable (or disable) the trigger analyzer
          --use-virustotal / --no-use-virustotal
                                          Enable (or disable) the VirusTotal scanner
          --use-aidefense / --no-use-aidefense
                                          Enable (or disable) the AI Defense analyzer
          --use-osv / --no-use-osv        Enable (or disable) OSV dependency checks
          --llm-provider [anthropic|openai|openrouter|bedrock|vllm|openai-compatible]
                                          Judge LLM provider (any `setup llm` provider, or openai-compatible with --llm-base-url)
          --llm-model TEXT                LLM model name
          --llm-base-url TEXT             Base URL for openai-compatible and vllm judges
          --llm-consensus-runs INTEGER    LLM consensus runs (0=disabled)
          --policy [strict|balanced|permissive|low-noise|quiet]
                                          Scan policy preset (recommended: quiet)
          --fail-on-severity [INFO|LOW|MEDIUM|HIGH|CRITICAL]
                                          Findings at or above this severity block (recommended: HIGH)
          --review-queue-min [INFO|LOW|MEDIUM|HIGH|CRITICAL]
                                          Findings from this severity up to the gate go to review (recommended: MEDIUM)
          --lenient / --no-lenient        Tolerate malformed skills (or fail them)
          --verify / --no-verify          Run connectivity checks after setup (default: on)
          --non-interactive               Use flags instead of prompts
          --help                          Show this message and exit.
        """);

    public static readonly string SkillHelp0810 = LineEndings.Normalize("""
        Usage: defenseclaw setup skill-scanner [OPTIONS]

          Configure skill-scanner analyzers, API keys, and policy.

        Options:
          --use-llm                       Enable LLM analyzer
          --use-behavioral                Enable behavioral analyzer
          --enable-meta                   Enable meta-analyzer
          --use-trigger                   Enable trigger analyzer
          --use-virustotal                Enable VirusTotal scanner
          --use-aidefense                 Enable AI Defense analyzer
          --llm-provider [anthropic|openai]
                                          LLM provider (anthropic or openai)
          --llm-model TEXT                LLM model name
          --llm-consensus-runs INTEGER    LLM consensus runs (0=disabled)
          --policy [strict|balanced|permissive|none]
                                          Scan policy preset (strict, balanced, permissive, none)
          --lenient                       Tolerate malformed skills
          --verify / --no-verify          Run connectivity checks after setup (default: on)
          --non-interactive               Use flags instead of prompts
          --help                          Show this message and exit.
        """);

    public static readonly string McpHelp104 = LineEndings.Normalize("""
        Usage: defenseclaw setup mcp-scanner [OPTIONS]

          Configure mcp-scanner analyzers and scan options.

        Options:
          --analyzers TEXT                Comma-separated analyzer list (yara,api,llm,behavioral,readiness); auto picks YARA plus a ready LLM
          --llm-provider [anthropic|openai|openrouter|bedrock|vllm|openai-compatible]
                                          Judge LLM provider (any `setup llm` provider, or openai-compatible)
          --llm-model TEXT                LLM model for semantic analysis
          --llm-base-url TEXT             Base URL for openai-compatible and vllm judges
          --api-endpoint TEXT             Cisco AI Defense API URL for the api analyzer
          --api-key-env TEXT              Env var holding the Cisco AI Defense API key
          --api-timeout-ms INTEGER RANGE  Cisco AI Defense request timeout in milliseconds  [x>=1]
          --scan-prompts / --no-scan-prompts
                                          Scan MCP prompts (or stop)
          --scan-resources / --no-scan-resources
                                          Scan MCP resources (or stop)
          --scan-instructions / --no-scan-instructions
                                          Scan server instructions (or stop)
          --verify / --no-verify          Run connectivity checks after setup (default: on)
          --non-interactive               Use flags instead of prompts
          --help                          Show this message and exit.
        """);

    /// <summary>A config_version 9 file (1.0.x) with every skill-scanner key set away from its default, a v9 analyzer list and the shared llm: block.</summary>
    public static readonly string ConfigV9 = LineEndings.Normalize("""
        config_version: 9
        llm:
          provider: anthropic
          model: anthropic/claude-sonnet-5-5
          base_url: http://127.0.0.1:8000/v1
        cisco_ai_defense:
          endpoint: https://aidefense.example.test
          api_key_env: CISCO_AI_DEFENSE_API_KEY
        scanners:
          skill_scanner:
            use_llm: false
            use_behavioral: true
            enable_meta: true
            use_trigger: true
            llm_consensus_runs: 3
            policy: strict
            lenient: false
            fail_on_severity: critical
            review_queue_min: low
            analyzers:
              virustotal:
                enabled: true
                api_key_env: VIRUSTOTAL_API_KEY
              aidefense:
                enabled: false
              osv:
                enabled: true
          mcp_scanner:
            analyzers: [yara, llm]
            scan_prompts: true
            scan_resources: false
            scan_instructions: true
        """);

    /// <summary>A config_version 8 file (0.8.x, or 1.0.x before <c>defenseclaw migrate</c>): the v8 analyzer switches and a comma-separated list.</summary>
    public static readonly string ConfigV8 = LineEndings.Normalize("""
        config_version: 8
        scanners:
          skill_scanner:
            use_virustotal: true
            use_aidefense: true
          mcp_scanner:
            analyzers: yara,api
        """);

    /// <summary>A config_version 9 file that sets none of the scanner keys.</summary>
    public static readonly string ConfigV9Empty = LineEndings.Normalize("""
        config_version: 9
        gateway:
          port: 18970
        """);

    public static WizardDefinition Build(string target, string helpText)
    {
        var help = SetupHelpParser.Parse(helpText);
        var (steps, curated) = WizardStepFactory.Build(target, help);
        steps = WizardWindowsPolicy.Filter(target, steps);
        steps = SecretRoutes.Annotate(target, steps);

        return new WizardDefinition
        {
            Target = target,
            Title = target == "skill-scanner" ? "Skill scanner" : "MCP scanner",
            Group = WizardGroups.Scanners,
            Description = help.Summary,
            Steps = steps,
            PlatformStatus = PlatformStatus.NotApplicable,
            IsCurated = curated,
            IsDetailLoaded = true,
            HelpText = helpText,
        };
    }

    public static WizardDefinition Applied(string target, string helpText, string config, bool readable = true) =>
        WizardBaseline.Apply(Build(target, helpText), WizardSamples.Config(config), readable);
}

/// <summary>
/// The scanner wizards open on the configured values (CUST-390): every <c>setup skill-scanner</c> / <c>setup mcp-scanner</c> option defaults to None
/// (<c>cmd_setup.py</c> at 1.0.4), so a value read from config.yaml is the field's baseline. An untouched Run sends no setting, a change sends exactly
/// that flag, and a cleared MCP analyzer list sends <c>--analyzers auto</c>. Defaults for keys that are not set follow <c>config.py</c> <c>load</c>
/// (use_llm on and the quiet policy, except in a config_version 8 file) and <c>scanner/settings.py</c> (block at HIGH, review from MEDIUM).
/// </summary>
public sealed class ScannerWizardBaselineTests
{
    private static string Start(WizardDefinition definition, string flag) => WizardSamples.Find(definition, flag).DefaultValue;

    private static IReadOnlyList<string> Untouched(WizardDefinition definition) => definition.BuildArgv(WizardSamples.StartingValues(definition));

    private static IReadOnlyList<string> Changed(WizardDefinition definition, string flag, string value)
    {
        var values = WizardSamples.StartingValues(definition);
        values[WizardSamples.Find(definition, flag).Id] = value;
        return definition.BuildArgv(values);
    }

    // ---------------------------------------------------------------- skill-scanner: what it opens on

    [Theory]
    [InlineData("--use-llm", ToggleValues.Off)]
    [InlineData("--use-behavioral", ToggleValues.On)]
    [InlineData("--enable-meta", ToggleValues.On)]
    [InlineData("--use-trigger", ToggleValues.On)]
    [InlineData("--use-virustotal", ToggleValues.On)]
    [InlineData("--use-aidefense", ToggleValues.Off)]
    [InlineData("--use-osv", ToggleValues.On)]
    [InlineData("--lenient", ToggleValues.Off)]
    [InlineData("--llm-consensus-runs", "3")]
    [InlineData("--policy", "strict")]
    [InlineData("--fail-on-severity", "CRITICAL")]
    [InlineData("--review-queue-min", "LOW")]
    [InlineData("--llm-provider", "anthropic")]
    [InlineData("--llm-model", "anthropic/claude-sonnet-5-5")]
    [InlineData("--llm-base-url", "http://127.0.0.1:8000/v1")]
    public void A_v9_skill_scanner_field_opens_on_the_configured_value_as_its_baseline(string flag, string expected)
    {
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV9);

        var field = WizardSamples.Find(definition, flag);
        Assert.Equal(expected, field.DefaultValue);
        Assert.Equal(expected, field.BaselineValue);
        Assert.StartsWith("config.yaml → ", field.BaselineSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--use-llm", ToggleValues.On)]
    [InlineData("--use-behavioral", ToggleValues.Off)]
    [InlineData("--enable-meta", ToggleValues.Off)]
    [InlineData("--use-trigger", ToggleValues.Off)]
    [InlineData("--use-virustotal", ToggleValues.Off)]
    [InlineData("--use-aidefense", ToggleValues.Off)]
    [InlineData("--use-osv", ToggleValues.Off)]
    [InlineData("--lenient", ToggleValues.On)]
    [InlineData("--llm-consensus-runs", "0")]
    [InlineData("--policy", "quiet")]
    [InlineData("--fail-on-severity", "HIGH")]
    [InlineData("--review-queue-min", "MEDIUM")]
    public void A_v9_file_without_the_keys_opens_on_the_runtimes_defaults_with_the_llm_judge_on(string flag, string expected)
    {
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV9Empty);

        Assert.Equal(expected, Start(definition, flag));
        Assert.Equal(expected, WizardSamples.Find(definition, flag).BaselineValue);
    }

    [Theory]
    [InlineData("--use-llm", ToggleValues.Off)]
    [InlineData("--use-virustotal", ToggleValues.On)]
    [InlineData("--use-aidefense", ToggleValues.On)]
    [InlineData("--use-osv", ToggleValues.Off)]
    [InlineData("--policy", "permissive")]
    [InlineData("--fail-on-severity", "HIGH")]
    [InlineData("--review-queue-min", "MEDIUM")]
    public void A_v8_file_reads_use_virustotal_and_use_aidefense_and_keeps_the_v8_defaults_for_the_judge_and_policy(string flag, string expected)
    {
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV8);

        Assert.Equal(expected, Start(definition, flag));
    }

    [Fact]
    public void A_written_analyzers_key_wins_over_the_v8_switch()
    {
        var config = ScannerWizardSamples.ConfigV8.Replace(
            "    use_aidefense: true\n",
            "    use_aidefense: true\n    analyzers:\n      virustotal:\n        enabled: false\n",
            StringComparison.Ordinal);

        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, config);

        Assert.Equal(ToggleValues.Off, Start(definition, "--use-virustotal"));
        Assert.Equal(ToggleValues.On, Start(definition, "--use-aidefense"));
    }

    [Fact]
    public void An_empty_policy_is_the_quiet_preset_and_one_the_form_cannot_show_is_left_alone()
    {
        var empty = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV9.Replace("policy: strict", "policy: ''", StringComparison.Ordinal));
        var custom = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV9.Replace("policy: strict", "policy: custom", StringComparison.Ordinal));

        Assert.Equal("quiet", Start(empty, "--policy"));
        Assert.Equal(string.Empty, Start(custom, "--policy"));
        Assert.Equal(new[] { "setup", "skill-scanner", "--non-interactive" }, Untouched(custom));
    }

    // ---------------------------------------------------------------- skill-scanner: what it sends

    [Theory]
    [InlineData("v9")]
    [InlineData("v9-empty")]
    [InlineData("v8")]
    public void An_untouched_skill_scanner_run_sends_no_setting(string shape)
    {
        var config = shape switch
        {
            "v9" => ScannerWizardSamples.ConfigV9,
            "v8" => ScannerWizardSamples.ConfigV8,
            _ => ScannerWizardSamples.ConfigV9Empty,
        };
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, config);

        Assert.Equal(new[] { "setup", "skill-scanner", "--non-interactive" }, Untouched(definition));
    }

    [Theory]
    [InlineData("--use-llm", ToggleValues.On, "--use-llm")]
    [InlineData("--use-behavioral", ToggleValues.Off, "--no-use-behavioral")]
    [InlineData("--enable-meta", ToggleValues.Off, "--no-enable-meta")]
    [InlineData("--use-trigger", ToggleValues.Off, "--no-use-trigger")]
    [InlineData("--use-virustotal", ToggleValues.Off, "--no-use-virustotal")]
    [InlineData("--use-aidefense", ToggleValues.On, "--use-aidefense")]
    [InlineData("--use-osv", ToggleValues.Off, "--no-use-osv")]
    [InlineData("--lenient", ToggleValues.On, "--lenient")]
    [InlineData("--llm-consensus-runs", "1", "--llm-consensus-runs 1")]
    [InlineData("--policy", "quiet", "--policy quiet")]
    [InlineData("--fail-on-severity", "HIGH", "--fail-on-severity HIGH")]
    [InlineData("--review-queue-min", "MEDIUM", "--review-queue-min MEDIUM")]
    [InlineData("--llm-model", "anthropic/claude-haiku-5", "--llm-model anthropic/claude-haiku-5")]
    public void Changing_one_skill_scanner_field_sends_only_that_flag(string flag, string value, string expected)
    {
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV9);

        var argv = Changed(definition, flag, value);

        Assert.Equal(new[] { "setup", "skill-scanner" }.Concat(expected.Split(' ')).Append("--non-interactive"), argv);
    }

    [Fact]
    public void Leave_unchanged_on_an_analyzer_sends_nothing_for_it()
    {
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV9);

        Assert.Equal(new[] { "setup", "skill-scanner", "--non-interactive" }, Changed(definition, "--use-virustotal", ToggleValues.Unset));
    }

    [Fact]
    public void On_0_8_10_the_bare_switches_stay_as_they_were_and_the_policy_opens_on_the_v8_default()
    {
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp0810, ScannerWizardSamples.ConfigV8.Replace("use_aidefense: true", "use_aidefense: true\n    use_llm: true", StringComparison.Ordinal));

        Assert.Equal(WizardFieldKind.Switch, WizardSamples.Find(definition, "--use-llm").Kind);
        Assert.Equal(ToggleValues.Off, Start(definition, "--use-llm"));
        Assert.Equal(ToggleValues.Off, Start(definition, "--use-virustotal"));
        Assert.Equal("permissive", Start(definition, "--policy"));
        Assert.DoesNotContain(definition.AllFields, f => f.Flag is "--fail-on-severity" or "--use-osv" or "--llm-base-url");
        Assert.Equal(new[] { "setup", "skill-scanner", "--non-interactive" }, Untouched(definition));
    }

    [Fact]
    public void An_unreadable_config_leaves_the_scanner_wizards_as_the_cli_described_them()
    {
        var skill = ScannerWizardSamples.Build("skill-scanner", ScannerWizardSamples.SkillHelp104);
        var mcp = ScannerWizardSamples.Build("mcp-scanner", ScannerWizardSamples.McpHelp104);

        Assert.Same(skill, WizardBaseline.Apply(skill, WizardSamples.Config(ScannerWizardSamples.ConfigV9), configReadable: false));
        Assert.Same(mcp, WizardBaseline.Apply(mcp, WizardSamples.Config(ScannerWizardSamples.ConfigV9), configReadable: false));
    }

    [Fact]
    public void The_note_says_where_the_values_came_from()
    {
        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, ScannerWizardSamples.ConfigV9);

        Assert.Contains("scanners.skill_scanner", definition.BaselineNote, StringComparison.Ordinal);
        Assert.Contains("Only fields you change are sent", definition.BaselineNote, StringComparison.Ordinal);
        Assert.Equal("config.yaml → llm", WizardSamples.Find(definition, "--llm-provider").BaselineSource);
        Assert.Equal("config.yaml → scanners.skill_scanner", WizardSamples.Find(definition, "--policy").BaselineSource);
    }

    // ---------------------------------------------------------------- mcp-scanner

    [Theory]
    [InlineData("v9", "yara,llm")]
    [InlineData("v8", "yara,api")]
    [InlineData("v9-empty", "auto")]
    public void The_mcp_analyzers_open_on_the_v8_text_or_the_v9_list_and_on_auto_when_unset(string shape, string expected)
    {
        var config = shape switch
        {
            "v9" => ScannerWizardSamples.ConfigV9,
            "v8" => ScannerWizardSamples.ConfigV8,
            _ => ScannerWizardSamples.ConfigV9Empty,
        };
        var definition = ScannerWizardSamples.Applied("mcp-scanner", ScannerWizardSamples.McpHelp104, config);

        Assert.Equal(expected, Start(definition, "--analyzers"));
        Assert.Equal(new[] { "setup", "mcp-scanner", "--non-interactive" }, Untouched(definition));
    }

    [Theory]
    [InlineData("--scan-prompts", ToggleValues.On)]
    [InlineData("--scan-resources", ToggleValues.Off)]
    [InlineData("--scan-instructions", ToggleValues.On)]
    [InlineData("--llm-provider", "anthropic")]
    public void The_mcp_scope_switches_open_on_the_configured_values(string flag, string expected)
    {
        var definition = ScannerWizardSamples.Applied("mcp-scanner", ScannerWizardSamples.McpHelp104, ScannerWizardSamples.ConfigV9);

        Assert.Equal(expected, Start(definition, flag));
    }

    [Fact]
    public void The_cisco_ai_defense_api_fields_are_not_prefilled_so_a_key_pasted_into_the_env_name_is_never_shown()
    {
        var definition = ScannerWizardSamples.Applied("mcp-scanner", ScannerWizardSamples.McpHelp104, ScannerWizardSamples.ConfigV9);

        Assert.Equal(string.Empty, Start(definition, "--api-endpoint"));
        Assert.Equal(string.Empty, Start(definition, "--api-key-env"));
    }

    [Theory]
    [InlineData("--scan-prompts", ToggleValues.Off, "--no-scan-prompts")]
    [InlineData("--scan-resources", ToggleValues.On, "--scan-resources")]
    [InlineData("--analyzers", "yara", "--analyzers yara")]
    [InlineData("--analyzers", "  ", "--analyzers auto")]
    [InlineData("--analyzers", "", "--analyzers auto")]
    public void Changing_one_mcp_field_sends_only_that_flag_and_a_cleared_list_sends_auto(string flag, string value, string expected)
    {
        var definition = ScannerWizardSamples.Applied("mcp-scanner", ScannerWizardSamples.McpHelp104, ScannerWizardSamples.ConfigV9);

        var argv = Changed(definition, flag, value);

        Assert.Equal(new[] { "setup", "mcp-scanner" }.Concat(expected.Split(' ')).Append("--non-interactive"), argv);
    }

    [Theory]
    [InlineData("v9-empty")]
    [InlineData("auto")]
    [InlineData("empty")]
    public void Clearing_a_list_that_already_was_auto_sends_nothing(string shape)
    {
        var config = shape switch
        {
            "auto" => ScannerWizardSamples.ConfigV9.Replace("analyzers: [yara, llm]", "analyzers: auto", StringComparison.Ordinal),
            "empty" => ScannerWizardSamples.ConfigV9.Replace("analyzers: [yara, llm]", "analyzers: ''", StringComparison.Ordinal),
            _ => ScannerWizardSamples.ConfigV9Empty,
        };
        var definition = ScannerWizardSamples.Applied("mcp-scanner", ScannerWizardSamples.McpHelp104, config);

        Assert.Equal(new[] { "setup", "mcp-scanner", "--non-interactive" }, Changed(definition, "--analyzers", string.Empty));
    }

    [Fact]
    public void The_auto_rule_survives_the_copies_the_wizard_makes_of_its_definition()
    {
        var definition = ScannerWizardSamples.Applied("mcp-scanner", ScannerWizardSamples.McpHelp104, ScannerWizardSamples.ConfigV9)
            .WithCrossValidator(static _ => null);

        Assert.Equal(new[] { "setup", "mcp-scanner", "--analyzers", "auto", "--non-interactive" }, Changed(definition, "--analyzers", string.Empty));
    }

    [Fact]
    public void A_config_with_crlf_line_endings_reads_the_same()
    {
        var crlf = ScannerWizardSamples.ConfigV9.Replace("\n", "\r\n", StringComparison.Ordinal);

        var definition = ScannerWizardSamples.Applied("skill-scanner", ScannerWizardSamples.SkillHelp104, crlf);
        var mcp = ScannerWizardSamples.Applied("mcp-scanner", ScannerWizardSamples.McpHelp104, crlf);

        Assert.Equal("strict", Start(definition, "--policy"));
        Assert.Equal("yara,llm", Start(mcp, "--analyzers"));
    }
}
