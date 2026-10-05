using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The readiness checklist is the TUI's <c>build_readiness_checks</c>: same rows, same pass/warn/fail rules, same fix argv. Everything here is
/// synthetic input; nothing runs.
/// </summary>
public sealed class SetupReadinessTests
{
    private static ReadinessInputs Healthy() => new()
    {
        ActiveConnectors = new[] { "claudecode" },
        Gateway = AppGatewayStateKind.Reachable,
        GatewaySubsystemState = "running",
        ApiState = "running",
        GuardrailEnabled = true,
        GuardrailMode = "observe",
        Config = new ReadinessConfig("anthropic", "example-model", string.Empty, string.Empty, string.Empty, true, false, false),
    };

    private static ReadinessCheck Row(ReadinessInputs inputs, string title) =>
        Assert.Single(SetupReadiness.Build(inputs), c => c.Title == title);

    private static string Argv(ReadinessFixStep step) => step.Executable + " " + string.Join(' ', step.Argv);

    [Fact]
    public void A_healthy_install_passes_every_row_and_offers_no_fix()
    {
        var checks = SetupReadiness.Build(Healthy());

        Assert.All(checks, c => Assert.Equal(ReadinessStatus.Pass, c.Status));
        Assert.All(checks, c => Assert.Null(c.Fix));
        Assert.Equal(
            new[]
            {
                "Active Connector: claudecode", "Gateway / API Health", "Guardrail", "Required Credentials", "LLM Config",
                "Scanner Availability", "Observability v8", "Registry / Asset Policy", "Restart Pending",
            },
            checks.Select(c => c.Title));
    }

    [Fact]
    public void Missing_required_credentials_fail_with_the_tuis_fill_missing_argv_in_a_terminal()
    {
        var inputs = Healthy() with
        {
            Credentials = new[]
            {
                new CredentialRow("EXAMPLE_A", "EXAMPLE_A", "F", "required", "unset", false, string.Empty),
                new CredentialRow("EXAMPLE_B", "EXAMPLE_B", "F", "required", "unset", false, string.Empty),
                new CredentialRow("EXAMPLE_C", "EXAMPLE_C", "F", "optional", "unset", false, string.Empty),
            },
        };

        var row = Row(inputs, "Required Credentials");

        Assert.Equal(ReadinessStatus.Fail, row.Status);
        Assert.Equal("2 required credential(s) missing", row.Detail);
        Assert.Equal(ReadinessFixKind.Terminal, row.Fix!.Kind);
        Assert.Equal("defenseclaw keys fill-missing --yes", Argv(Assert.Single(row.Fix.Steps)));
    }

    [Fact]
    public void The_doctor_cache_counts_only_while_the_key_list_shows_nothing_missing()
    {
        var doctorOnly = Healthy() with { DoctorMissingCredentials = new[] { "EXAMPLE_A" } };
        Assert.Equal("1 required credential(s) missing", Row(doctorOnly, "Required Credentials").Detail);

        var listWins = doctorOnly with
        {
            Credentials = new[]
            {
                new CredentialRow("X", "X", "F", "required", "unset", false, string.Empty),
                new CredentialRow("Y", "Y", "F", "required", "unset", false, string.Empty),
            },
        };
        Assert.Equal("2 required credential(s) missing", Row(listWins, "Required Credentials").Detail);
    }

    [Theory]
    [InlineData("llm", "", "")]
    [InlineData("llm", "", "overlay")]
    public void An_llm_without_a_model_or_an_overlay_warns_and_opens_the_llm_wizard(string provider, string model, string instance)
    {
        var inputs = Healthy() with { Config = Healthy().Config with { LlmProvider = provider, LlmModel = model, LlmInstanceName = instance } };

        var row = Row(inputs, "LLM Config");

        if (instance.Length > 0)
        {
            Assert.Equal(ReadinessStatus.Pass, row.Status);
            Assert.Equal("llm (via instance overlay)", row.Detail);
            Assert.NotNull(Assert.Single(SetupReadiness.Build(inputs), c => c.Title == "Custom-provider Overlay"));
            return;
        }

        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.Equal(ReadinessFixKind.Wizard, row.Fix!.Kind);
        Assert.Equal("llm", row.Fix.WizardTarget);
        Assert.Equal("defenseclaw setup llm", Argv(Assert.Single(row.Fix.Steps))); // the TUI's argv, kept for the tooltip
    }

    [Fact]
    public void A_regional_provider_without_a_region_warns_and_azure_accepts_an_endpoint()
    {
        var bedrock = Healthy() with { Config = Healthy().Config with { LlmProvider = "bedrock", LlmModel = "m" } };
        var row = Row(bedrock, "Regional Provider");
        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.Equal("bedrock selected but no region configured.", row.Detail);
        Assert.Equal("llm", row.Fix!.WizardTarget);

        var azure = Healthy() with { Config = Healthy().Config with { LlmProvider = "azure", LlmModel = "m", AzureEndpoint = "https://example.invalid" } };
        var azureRow = Row(azure, "Regional Provider");
        Assert.Equal(ReadinessStatus.Pass, azureRow.Status);
        Assert.Equal("azure (https://example.invalid)", azureRow.Detail);

        var azureBare = Healthy() with { Config = Healthy().Config with { LlmProvider = "azure", LlmModel = "m" } };
        Assert.Equal("azure selected but no endpoint configured.", Row(azureBare, "Regional Provider").Detail);

        // No regional row at all for a provider that does not need one.
        Assert.DoesNotContain(SetupReadiness.Build(Healthy()), c => c.Title == "Regional Provider");
    }

    [Fact]
    public void Scanner_availability_is_a_two_step_doctor_fix_preview_then_apply()
    {
        var inputs = Healthy() with { Config = Healthy().Config with { ScannerConfigured = false } };

        var row = Row(inputs, "Scanner Availability");

        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.Equal(ReadinessFixKind.Review, row.Fix!.Kind);
        Assert.Equal(
            new[] { "defenseclaw doctor --fix --dry-run", "defenseclaw doctor --fix --yes" },
            row.Fix.Steps.Select(Argv));
    }

    [Fact]
    public void A_registry_required_but_empty_policy_warns_only_when_the_policy_is_on_and_syncs_every_registry()
    {
        var on = Healthy() with { Config = Healthy().Config with { AssetPolicyEnabled = true, RegistryRequiredButEmpty = true } };
        var row = Row(on, "Registry / Asset Policy");
        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.Equal("defenseclaw registry sync --all", Argv(Assert.Single(row.Fix!.Steps)));

        var off = Healthy() with { Config = Healthy().Config with { AssetPolicyEnabled = false, RegistryRequiredButEmpty = true } };
        Assert.Equal(ReadinessStatus.Pass, Row(off, "Registry / Asset Policy").Status);
    }

    [Fact]
    public void A_queued_restart_warns_and_the_fix_restarts_the_gateway()
    {
        var row = Row(Healthy() with { RestartReason = "Guardrail change saved." }, "Restart Pending");

        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.Equal("Guardrail change saved.", row.Detail);
        Assert.True(row.Fix!.RestartsGateway);
        Assert.Equal("defenseclaw-gateway restart", Argv(Assert.Single(row.Fix.Steps)));
    }

    [Fact]
    public void The_gateway_row_follows_the_tui_start_when_down_restart_when_unhealthy_nothing_when_unrunnable()
    {
        var stopped = Row(Healthy() with { Gateway = AppGatewayStateKind.Stopped }, "Gateway / API Health");
        Assert.Equal(ReadinessStatus.Fail, stopped.Status);
        Assert.Equal("defenseclaw-gateway start", Argv(Assert.Single(stopped.Fix!.Steps)));

        var unhealthy = Row(Healthy() with { ApiState = "error" }, "Gateway / API Health");
        Assert.Equal(ReadinessStatus.Warn, unhealthy.Status);
        Assert.Equal("gateway=running api=error", unhealthy.Detail);
        Assert.Equal("defenseclaw-gateway restart", Argv(Assert.Single(unhealthy.Fix!.Steps)));

        // A standalone install reports gateway=disabled, which is healthy.
        Assert.Equal(ReadinessStatus.Pass, Row(Healthy() with { GatewaySubsystemState = "disabled" }, "Gateway / API Health").Status);

        var notInstalled = Row(
            Healthy() with { Gateway = AppGatewayStateKind.NotRunnableHere, GatewayDetail = "defenseclaw is not installed" },
            "Gateway / API Health");
        Assert.Equal(ReadinessStatus.Fail, notInstalled.Status);
        Assert.Null(notInstalled.Fix);
    }

    [Fact]
    public void No_connector_fails_without_the_tuis_openclaw_fix_because_openclaw_is_not_a_windows_connector()
    {
        var row = Assert.Single(SetupReadiness.Build(Healthy() with { ActiveConnectors = Array.Empty<string>() }), c => c.Title == "Active Connector");

        Assert.Equal(ReadinessStatus.Fail, row.Status);
        Assert.Null(row.Fix);
    }

    [Fact]
    public void A_disabled_guardrail_warns_and_opens_the_guardrail_wizard()
    {
        var row = Row(Healthy() with { GuardrailEnabled = false }, "Guardrail");

        Assert.Equal(ReadinessStatus.Warn, row.Status);
        Assert.Equal("guardrail", row.Fix!.WizardTarget);
        Assert.Equal("defenseclaw setup guardrail", Argv(Assert.Single(row.Fix.Steps)));
    }

    [Fact]
    public void Every_review_fix_argv_is_classified_as_changing_state_never_read_only_so_it_is_always_reviewed()
    {
        var everything = Healthy() with
        {
            Gateway = AppGatewayStateKind.Stopped,
            RestartReason = "queued",
            Config = new ReadinessConfig("llm", string.Empty, string.Empty, string.Empty, string.Empty, false, true, true),
        };

        var fixes = SetupReadiness.Build(everything).Where(c => c.Fix is { Kind: ReadinessFixKind.Review }).Select(c => c.Fix!).ToArray();

        Assert.NotEmpty(fixes);
        foreach (var step in fixes.SelectMany(f => f.Steps).Where(s => s.Argv.Contains("--dry-run") == false))
        {
            Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(step.Argv));
        }
    }
}
