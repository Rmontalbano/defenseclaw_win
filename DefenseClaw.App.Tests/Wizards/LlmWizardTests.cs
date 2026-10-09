using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The curated <c>setup llm</c> wizard (CUST-269): Role, Provider, Model, API key variable and base URL, then the Bedrock, Vertex AI, Azure and TLS
/// groups that appear only for the provider they belong to, then an Apply page with Inherit From and Ping. Built from the help screen the
/// installed 0.8.10 CLI prints (<c>Fixtures/runtime-0.8.10/help/setup-llm.txt</c>), through the catalog, the way the app builds it.
/// </summary>
public class LlmWizardTests
{
    private static readonly string[] CloudPages =
    {
        WizardPages.LlmBedrock, WizardPages.LlmVertex, WizardPages.LlmAzure, WizardPages.LlmTls,
    };

    private static Task<WizardDefinition> Real() => CatalogHelp.RealAsync("llm");

    private static string[] CloudPagesShown(WizardDefinition definition, WizardValues values) =>
        WizardAnswers.Pages(definition, values).Where(id => CloudPages.Contains(id)).ToArray();

    // ------------------------------------------------------------------ the layout

    [Fact]
    public async Task The_real_help_becomes_a_curated_wizard_with_the_questions_first_and_the_groups_after_them()
    {
        var definition = await Real();

        Assert.True(definition.IsCurated);
        Assert.Equal(
            new[]
            {
                WizardGoals.StepId, WizardPages.LlmProvider, WizardPages.LlmKey, WizardPages.LlmBedrock, WizardPages.LlmVertex, WizardPages.LlmAzure,
                WizardPages.LlmTls, WizardPages.LlmApply, "more-page-1",
            },
            definition.Steps.Select(s => s.Id).ToArray());

        Assert.Equal(
            new[] { "--role", "--provider", "--instance-name", "--model" },
            definition.Steps.Single(s => s.Id == WizardPages.LlmProvider).Fields.Select(f => f.Flag!).ToArray());
        Assert.Equal(
            new[] { "--api-key-env", "--api-key", "--base-url", "--timeout", "--max-retries" },
            definition.Steps.Single(s => s.Id == WizardPages.LlmKey).Fields.Select(f => f.Flag!).ToArray());
        Assert.Equal(
            new[] { "--inherit-from", "--ping", "--non-interactive" },
            definition.Steps.Single(s => s.Id == WizardPages.LlmApply).Fields.Select(f => f.Flag!).ToArray());
    }

    [Fact]
    public async Task What_the_layout_does_not_place_is_still_on_the_more_options_page()
    {
        var definition = await Real();

        var more = definition.Steps.Single(s => s.Id == "more-page-1").Fields.Select(f => f.Flag!).ToArray();

        // The generic region and auth-mode flags, --show and the interactive-preflight switch: nothing the CLI accepts is hidden.
        Assert.Contains("--region", more);
        Assert.Contains("--auth-mode", more);
        Assert.Contains("--show", more);
        Assert.Contains("--inherit", more);
    }

    [Fact]
    public async Task Providers_are_the_tuis_list_in_the_tuis_order_followed_by_the_rest_of_the_clis()
    {
        var definition = await Real();
        var provider = WizardAnswers.Field(definition, "--provider");

        var offered = provider.Choices.Select(c => c.Value).ToArray();
        Assert.Equal(string.Empty, offered[0]);

        var expectedFirst = ConfigFieldCatalog.LlmProviders.ToArray();
        Assert.Equal(expectedFirst, offered.Skip(1).Take(expectedFirst.Length).ToArray());

        // The CLI also lists providers the TUI's panel does not offer; they follow, and nothing is invented.
        var rest = offered.Skip(1 + expectedFirst.Length).ToArray();
        Assert.Contains("fireworks_ai", rest);
        Assert.Contains("custom", rest);
        Assert.DoesNotContain(rest, p => expectedFirst.Contains(p));
    }

    [Fact]
    public async Task The_model_field_opens_the_model_picker_for_the_provider_and_instance_the_wizard_has()
    {
        var definition = await Real();
        var model = WizardAnswers.Field(definition, "--model");

        Assert.True(model.HasPicker);
        Assert.Equal(WizardFieldPickers.Model, model.PickerKind);
        Assert.Equal("provider", model.PickerProviderFieldId);
        Assert.Equal("instance-name", model.PickerInstanceFieldId);
        Assert.Contains(definition.AllFields, f => f.Id == model.PickerProviderFieldId);
        Assert.Contains(definition.AllFields, f => f.Id == model.PickerInstanceFieldId);
    }

    // ------------------------------------------------------------------ provider-gated groups

    [Theory]
    [InlineData("anthropic")]
    [InlineData("openai")]
    [InlineData("ollama")]
    [InlineData("")]
    public async Task A_provider_with_no_cloud_settings_shows_none_of_the_groups(string provider)
    {
        var definition = await Real();

        Assert.Empty(CloudPagesShown(definition, WizardAnswers.Start(definition, ("provider", provider))));
    }

    [Theory]
    [InlineData("bedrock", new[] { WizardPages.LlmBedrock, WizardPages.LlmTls })]
    [InlineData("vertex_ai", new[] { WizardPages.LlmVertex, WizardPages.LlmTls })]
    [InlineData("azure", new[] { WizardPages.LlmAzure, WizardPages.LlmTls })]
    [InlineData("custom", new[] { WizardPages.LlmTls })]
    public async Task The_provider_gating_table_shows_each_cloud_its_own_group_and_the_tls_overrides_where_they_apply(string provider, string[] expected)
    {
        var definition = await Real();

        Assert.Equal(expected, CloudPagesShown(definition, WizardAnswers.Start(definition, ("provider", provider))));
    }

    [Fact]
    public async Task Bedrock_shows_only_the_bedrock_rows_and_no_other_clouds()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(definition, ("provider", "bedrock"), ("bedrock-auth-mode", "iam_credentials"));

        var shown = definition.Evaluate(values);
        var providerRows = definition.Steps
            .Where(s => CloudPages.Contains(s.Id) && s.Id != WizardPages.LlmTls)
            .SelectMany(s => s.Fields)
            .Where(f => shown.IsShown(f))
            .Select(f => f.Flag!)
            .ToArray();

        // Every row the provider-specific groups show is a Bedrock row.
        Assert.NotEmpty(providerRows);
        Assert.All(providerRows, flag => Assert.StartsWith("--bedrock-", flag, StringComparison.Ordinal));
        Assert.Equal(
            new[]
            {
                "--bedrock-region", "--bedrock-auth-mode", "--bedrock-access-key-env", "--bedrock-secret-key-env", "--bedrock-session-token-env",
                "--bedrock-inference-profile", "--bedrock-deployment",
            },
            providerRows);

        // Not one Vertex or Azure row counts, shown or not.
        var counted = WizardAnswers.ActiveFlags(definition, values);
        Assert.DoesNotContain(counted, flag => flag.StartsWith("--vertex-", StringComparison.Ordinal) || flag.StartsWith("--azure-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("api_key", new string[0])]
    [InlineData("instance_role", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("iam_credentials", new[] { "bedrock-access-key-env", "bedrock-secret-key-env", "bedrock-session-token-env" })]
    [InlineData("profile", new[] { "bedrock-profile-name" })]
    public async Task Bedrock_asks_for_credentials_only_as_the_auth_mode_needs_them(string authMode, string[] expectedExtras)
    {
        var definition = await Real();
        var shown = WizardAnswers.Shown(definition, WizardAnswers.Start(definition, ("provider", "bedrock"), ("bedrock-auth-mode", authMode)), WizardPages.LlmBedrock);

        Assert.Equal(new[] { "bedrock-region", "bedrock-auth-mode", "bedrock-inference-profile", "bedrock-deployment" }.Concat(expectedExtras).OrderBy(x => x).ToArray(), shown.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Answers_left_over_from_another_provider_never_reach_the_command()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(
            definition,
            ("provider", "bedrock"),
            ("model", "us.anthropic.claude-sonnet-4-6"),
            ("bedrock-region", "us-east-1"),
            ("vertex-project-id", "stale-project"),
            ("azure-endpoint", "https://stale.example.test"),
            ("bedrock-auth-mode", "profile"),
            ("bedrock-access-key-env", "STALE_ACCESS_KEY"));

        var argv = definition.BuildArgv(values);

        Assert.Equal(
            new[]
            {
                "setup", "llm", "--provider", "bedrock", "--model", "us.anthropic.claude-sonnet-4-6", "--bedrock-region", "us-east-1",
                "--bedrock-auth-mode", "profile", "--non-interactive",
            },
            argv);
        Assert.DoesNotContain(argv, a => a.Contains("stale", StringComparison.OrdinalIgnoreCase) || a.Contains("STALE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_the_provider_back_takes_the_groups_out_of_the_command_again()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(definition, ("provider", "azure"), ("azure-endpoint", "https://contoso.openai.azure.example"), ("model", "gpt-x"));
        Assert.Contains("--azure-endpoint", definition.BuildArgv(values));

        values["provider"] = "openai";

        var argv = definition.BuildArgv(values);
        Assert.DoesNotContain("--azure-endpoint", argv);
        Assert.Equal(new[] { "setup", "llm", "--provider", "openai", "--model", "gpt-x", "--non-interactive" }, argv);
    }

    [Fact]
    public async Task Repeatable_deployment_aliases_are_one_pair_per_line()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(
            definition,
            ("provider", "azure"),
            ("azure-deployment-alias", "gpt-a=deploy-a\ngpt-b=deploy-b"));

        var argv = definition.BuildArgv(values);

        Assert.Equal(
            new[] { "--azure-deployment-alias", "gpt-a=deploy-a", "--azure-deployment-alias", "gpt-b=deploy-b" },
            argv.SkipWhile(a => a != "--azure-deployment-alias").Take(4).ToArray());
    }

    [Fact]
    public async Task The_tls_group_carries_the_ca_bundle_and_the_insecure_switch_for_a_custom_endpoint()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(
            definition,
            ("provider", "custom"),
            ("instance-name", "corp-gateway"),
            ("tls-ca-cert-file", @"C:\certs\corp-ca.pem"),
            ("insecure-skip-verify", ToggleValues.On));

        var argv = definition.BuildArgv(values);

        Assert.Equal(
            new[]
            {
                "setup", "llm", "--provider", "custom", "--instance-name", "corp-gateway", "--tls-ca-cert-file", @"C:\certs\corp-ca.pem",
                "--insecure-skip-verify", "--non-interactive",
            },
            argv);
    }

    [Fact]
    public async Task A_ca_bundle_together_with_skipping_verification_is_refused_before_the_review_and_either_alone_is_fine()
    {
        var definition = await Real();
        var both = WizardAnswers.Start(
            definition,
            ("provider", "custom"),
            ("instance-name", "corp-gateway"),
            ("tls-ca-cert-file", @"C:\certs\corp-ca.pem"),
            ("insecure-skip-verify", ToggleValues.On));
        var bundleOnly = WizardAnswers.Start(
            definition,
            ("provider", "custom"),
            ("instance-name", "corp-gateway"),
            ("tls-ca-cert-file", @"C:\certs\corp-ca.pem"));
        var skipOnly = WizardAnswers.Start(
            definition,
            ("provider", "custom"),
            ("instance-name", "corp-gateway"),
            ("insecure-skip-verify", ToggleValues.On));

        Assert.Contains("not both", definition.CrossCheck(both), StringComparison.Ordinal);
        Assert.Null(definition.CrossCheck(bundleOnly));
        Assert.Null(definition.CrossCheck(skipOnly));
    }

    [Fact]
    public async Task A_ca_bundle_left_on_a_page_the_provider_no_longer_shows_does_not_count_against_skipping_verification()
    {
        var definition = await Real();
        var values = WizardAnswers.Start(
            definition,
            ("provider", "openai"),
            ("tls-ca-cert-file", @"C:\certs\corp-ca.pem"),
            ("insecure-skip-verify", ToggleValues.On));

        Assert.Null(definition.CrossCheck(values));
    }

    // ------------------------------------------------------------------ the apply page

    [Fact]
    public async Task Ping_and_inherit_from_are_the_apply_pages_choices()
    {
        var definition = await Real();
        var ping = WizardAnswers.Field(definition, "--ping");
        var inherit = WizardAnswers.Field(definition, "--inherit-from");

        Assert.Equal(WizardFieldKind.Toggle, ping.Kind);
        Assert.Equal("Ping after saving", ping.Label);
        Assert.Contains("real request", ping.Help, StringComparison.Ordinal);
        Assert.Equal(new[] { string.Empty, "guardrail", "guardrail.judge", "scanners.skill", "scanners.mcp", "scanners.plugin" }, inherit.Choices.Select(c => c.Value).ToArray());

        Assert.Equal(
            new[] { "setup", "llm", "--inherit-from", "guardrail.judge", "--ping", "--non-interactive" },
            definition.BuildArgv(WizardAnswers.Start(definition, ("inherit-from", "guardrail.judge"), ("ping", ToggleValues.On))));
        Assert.Equal(
            new[] { "setup", "llm", "--no-ping", "--non-interactive" },
            definition.BuildArgv(WizardAnswers.Start(definition, ("ping", ToggleValues.Off))));
    }

    [Fact]
    public async Task An_untouched_wizard_sends_only_the_switch_that_stops_the_cli_prompting()
    {
        var definition = await Real();

        Assert.Equal(new[] { "setup", "llm", "--non-interactive" }, definition.BuildArgv(WizardAnswers.Start(definition)));
    }

    // ------------------------------------------------------------------ secrets

    [Fact]
    public async Task The_api_key_is_a_credential_card_that_names_the_variable_and_never_a_value_on_the_command_line()
    {
        var definition = await Real();
        var key = WizardAnswers.Field(definition, "--api-key");
        var values = WizardAnswers.Start(definition, ("api-key-env", "MY_PROVIDER_KEY"), ("api-key", "synthetic-key-must-never-appear"));

        Assert.True(key.IsSecret);
        Assert.NotNull(key.Credential);
        Assert.Equal("MY_PROVIDER_KEY", key.Credential!.EnvName(values));
        Assert.Null(key.Credential.InAppVariable(values));

        var argv = definition.BuildArgv(values);
        Assert.DoesNotContain("--api-key", argv);
        Assert.Equal(new[] { "setup", "llm", "--api-key-env", "MY_PROVIDER_KEY", "--non-interactive" }, argv);
        Assert.All(argv, a => Assert.DoesNotContain("synthetic-key", a, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_variable_name_field_takes_a_name_and_refuses_a_value_that_looks_like_a_key()
    {
        var definition = await Real();
        var field = WizardAnswers.Field(definition, "--api-key-env");

        Assert.Equal(WizardFieldKind.EnvVarName, field.Kind);
    }

    // ------------------------------------------------------------------ flags checked against the live help

    [Fact]
    public async Task A_flag_the_installed_cli_does_not_have_is_not_offered()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "llm",
            new Dictionary<string, string>
            {
                ["llm"] = CatalogHelp.Screen(
                    "llm",
                    "--provider [anthropic|openai|bedrock]  Provider.",
                    "--model TEXT  Model.",
                    "--api-key-env TEXT  Variable holding the key.",
                    "--bedrock-region TEXT  AWS region.",
                    "--non-interactive  Never prompt."),
            });

        var flags = definition.AllFields.Select(f => f.Flag).ToArray();

        // No ping, no role, no inherit-from, no vertex or azure: the help does not list them, so no field and no page does.
        Assert.DoesNotContain("--ping", flags);
        Assert.DoesNotContain("--role", flags);
        Assert.DoesNotContain("--inherit-from", flags);
        Assert.DoesNotContain(flags, f => f is not null && (f.StartsWith("--vertex", StringComparison.Ordinal) || f.StartsWith("--azure", StringComparison.Ordinal)));
        Assert.Contains(definition.Steps, s => s.Id == WizardPages.LlmBedrock);
        Assert.DoesNotContain(definition.Steps, s => s.Id is WizardPages.LlmVertex or WizardPages.LlmAzure or WizardPages.LlmTls);

        // What is there still works: the bedrock page is gated on the provider, and the argv carries only listed flags.
        var argv = definition.BuildArgv(WizardAnswers.Start(definition, ("provider", "bedrock"), ("bedrock-region", "eu-west-1")));
        Assert.Equal(new[] { "setup", "llm", "--provider", "bedrock", "--bedrock-region", "eu-west-1", "--non-interactive" }, argv);
    }

    [Fact]
    public async Task Cloud_flags_with_no_provider_flag_to_gate_them_are_not_hidden_behind_a_gate_that_never_opens()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "llm",
            new Dictionary<string, string>
            {
                ["llm"] = CatalogHelp.Screen("llm", "--model TEXT  Model.", "--bedrock-region TEXT  AWS region.", "--non-interactive  Never prompt."),
            });

        // No --provider, so no page can be gated on it: the region is on the trailing page where it can be reached.
        Assert.DoesNotContain(definition.Steps, s => s.Id == WizardPages.LlmBedrock);
        var more = Assert.Single(definition.Steps, s => s.Id.StartsWith("more-", StringComparison.Ordinal));
        Assert.Contains(more.Fields, f => f.Flag == "--bedrock-region");
        Assert.Contains("--bedrock-region", WizardAnswers.ActiveFlags(definition, WizardAnswers.Start(definition)));
    }

    [Fact]
    public async Task Credentials_that_depend_on_an_auth_mode_the_cli_lacks_are_asked_for_without_it()
    {
        var definition = await CatalogHelp.DefinitionAsync(
            "llm",
            new Dictionary<string, string>
            {
                ["llm"] = CatalogHelp.Screen(
                    "llm",
                    "--provider [bedrock|openai]  Provider.",
                    "--bedrock-region TEXT  AWS region.",
                    "--bedrock-profile-name TEXT  AWS profile.",
                    "--non-interactive  Never prompt."),
            });

        var profile = definition.AllFields.Single(f => f.Flag == "--bedrock-profile-name");

        Assert.Null(profile.VisibleWhenFieldId);
        Assert.Contains("bedrock-profile-name", WizardAnswers.Shown(definition, WizardAnswers.Start(definition, ("provider", "bedrock")), WizardPages.LlmBedrock));
    }

    // ------------------------------------------------------------------ pre-fill from config.yaml

    private const string StoredConfig = """
        llm:
          provider: bedrock
          model: us.anthropic.claude-sonnet-4-6
          api_key_env: AWS_BEARER_TOKEN_BEDROCK
          bedrock:
            region: us-west-2
            auth_mode: profile
            profile_name: team-profile
            inference_profile: us.
          vertex:
            project_id: example-project
        """;

    [Fact]
    public async Task The_wizard_starts_from_the_stored_block_including_the_provider_group_and_sends_nothing_for_it()
    {
        var definition = WizardBaseline.Apply(await Real(), WizardSamples.Config(LineEndings.Normalize(StoredConfig)), configReadable: true);
        var values = WizardAnswers.Start(definition);

        Assert.Equal("bedrock", values["provider"]);
        Assert.Equal("us-west-2", values["bedrock-region"]);
        Assert.Equal("profile", values["bedrock-auth-mode"]);
        Assert.Equal("team-profile", values["bedrock-profile-name"]);
        Assert.Equal("us.", values["bedrock-inference-profile"]);

        // The Bedrock group is there because the stored provider is Bedrock; the Vertex project config also holds is not asked about.
        Assert.Equal(new[] { WizardPages.LlmBedrock, WizardPages.LlmTls }, CloudPagesShown(definition, values));
        Assert.Equal(new[] { "setup", "llm", "--non-interactive" }, definition.BuildArgv(values));

        values["bedrock-region"] = "eu-central-1";
        Assert.Equal(new[] { "setup", "llm", "--bedrock-region", "eu-central-1", "--non-interactive" }, definition.BuildArgv(values));
    }

    // ------------------------------------------------------------------ provider and model are needed, unless config.yaml has them

    [Fact]
    public async Task With_neither_in_config_yaml_the_wizard_says_a_provider_and_a_model_are_needed_before_the_cli_would()
    {
        var definition = WizardBaseline.Apply(await Real(), WizardSamples.Config("config_version: 5\n"), configReadable: true);
        var values = WizardAnswers.Start(definition);

        Assert.Contains("provider", definition.CrossValidator!(values), StringComparison.OrdinalIgnoreCase);

        values["provider"] = "openai";
        Assert.Contains("model", definition.CrossValidator!(values), StringComparison.OrdinalIgnoreCase);

        values["model"] = "gpt-x";
        Assert.Null(definition.CrossValidator!(values));
    }

    [Fact]
    public async Task What_config_yaml_already_has_is_enough()
    {
        var definition = WizardBaseline.Apply(await Real(), WizardSamples.Config(LineEndings.Normalize(StoredConfig)), configReadable: true);
        var values = WizardAnswers.Start(definition);

        Assert.Null(definition.CrossValidator!(values));

        // Leaving the box unchanged keeps the stored one.
        values["provider"] = string.Empty;
        values["model"] = string.Empty;
        Assert.Null(definition.CrossValidator!(values));
    }

    [Fact]
    public async Task The_judge_role_inherits_what_it_does_not_set_so_nothing_is_required()
    {
        var definition = WizardBaseline.Apply(await Real(), WizardSamples.Config("config_version: 5\n"), configReadable: true);

        Assert.Null(definition.CrossValidator!(WizardAnswers.Start(definition, ("role", "judge"))));
    }
}
