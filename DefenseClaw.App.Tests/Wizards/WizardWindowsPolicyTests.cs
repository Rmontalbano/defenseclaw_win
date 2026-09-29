using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>What the Setup hub and the wizards do not offer on Windows, and the reason shown instead.</summary>
public class WizardWindowsPolicyTests
{
    private static WizardField Field(
        string id,
        string? flag,
        WizardFieldKind kind = WizardFieldKind.Text,
        IReadOnlyList<WizardChoice>? choices = null,
        bool positional = false,
        string? gate = null,
        string[]? gateValues = null) => new()
    {
        Id = id,
        Label = id,
        Kind = kind,
        Flag = flag,
        Choices = choices ?? Array.Empty<WizardChoice>(),
        IsPositional = positional,
        VisibleWhenFieldId = gate,
        VisibleWhenValues = gateValues ?? Array.Empty<string>(),
    };

    private static WizardStep Step(string id, params WizardField[] fields) => new() { Id = id, Title = id, Fields = fields };

    /// <summary>A page gated on the "subcommand" pick, the way <c>WizardStepFactory.BuildGroup</c> builds them.</summary>
    private static WizardStep GatedStep(string id, string gateValue, params WizardField[] fields) => new()
    {
        Id = id,
        Title = id,
        Fields = fields,
        VisibleWhenFieldId = "subcommand",
        VisibleWhenValues = new[] { gateValue },
    };

    // ------------------------------------------------------------------ which cards are launchable

    [Theory]
    [InlineData("registry")]
    [InlineData("local-observability")]
    [InlineData("gateway")]
    public void Interactive_only_and_docker_targets_are_unavailable_whatever_the_cli_certifies(string target)
    {
        foreach (var status in Enum.GetValues<PlatformStatus>())
        {
            var reason = WizardWindowsPolicy.UnavailableReason(target, status);

            Assert.False(string.IsNullOrWhiteSpace(reason), $"{target} / {status}");
        }
    }

    [Fact]
    public void The_registry_reason_points_at_the_panel_that_replaces_it()
    {
        var reason = WizardWindowsPolicy.UnavailableReason("registry", PlatformStatus.Certified);

        Assert.Contains("real terminal", reason, StringComparison.Ordinal);
        Assert.Contains("Registries panel", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_connector_is_unavailable_and_says_the_cli_refuses_it()
    {
        var reason = WizardWindowsPolicy.UnavailableReason("openclaw", PlatformStatus.Unsupported);

        Assert.Contains("unsupported on Windows", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlatformStatus.Certified)]
    [InlineData(PlatformStatus.NotCertified)]
    [InlineData(PlatformStatus.Unknown)]
    [InlineData(PlatformStatus.NotApplicable)]
    public void Everything_else_stays_launchable(PlatformStatus status)
    {
        Assert.Null(WizardWindowsPolicy.UnavailableReason("claude-code", status));
        Assert.Null(WizardWindowsPolicy.UnavailableReason("cursor", status));
    }

    // ------------------------------------------------------------------ flags that are suppressed

    [Fact]
    public void The_local_stack_flag_is_suppressed_everywhere_and_the_claude_code_wizard_never_offers_it()
    {
        var help = SetupHelpParser.Parse(WizardSamples.ClaudeCodeHelp);
        Assert.NotNull(help.Option("--with-local-stack"));

        var (steps, _) = WizardStepFactory.Build("claude-code", help);
        Assert.Contains(steps.SelectMany(s => s.Fields), f => f.Flag == "--with-local-stack");

        var filtered = WizardWindowsPolicy.Filter("claude-code", steps);

        Assert.DoesNotContain(filtered.SelectMany(s => s.Fields), f => f.Flag == "--with-local-stack");
        Assert.DoesNotContain(filtered.SelectMany(s => s.Fields), f => f.Flag == "--no-local-stack");
        Assert.Contains(filtered.SelectMany(s => s.Fields), f => f.Flag == "--restart");
    }

    [Fact]
    public void A_page_left_with_no_fields_is_dropped_but_a_page_that_was_empty_to_begin_with_is_kept()
    {
        var steps = new[]
        {
            Step("only-docker", Field("stack", "--with-local-stack", WizardFieldKind.Toggle)),
            Step("no-options"),
            Step("mixed", Field("stack", "--with-local-stack", WizardFieldKind.Toggle), Field("model", "--model")),
        };

        var filtered = WizardWindowsPolicy.Filter("codex", steps);

        Assert.Equal(new[] { "no-options", "mixed" }, filtered.Select(s => s.Id).ToArray());
        Assert.Equal("--model", Assert.Single(filtered[1].Fields).Flag);
    }

    [Fact]
    public void A_step_with_nothing_removed_is_returned_as_the_same_object()
    {
        var untouched = Step("plain", Field("model", "--model"));

        var filtered = WizardWindowsPolicy.Filter("codex", new[] { untouched });

        Assert.Same(untouched, Assert.Single(filtered));
    }

    [Fact]
    public void A_positional_field_has_no_flag_and_is_never_suppressed()
    {
        var positional = Step("p", Field("arg", null, positional: true));

        Assert.Single(WizardWindowsPolicy.Filter("splunk", new[] { positional }));
    }

    // ------------------------------------------------------------------ splunk: no Docker pipeline, no dashboards

    private static IReadOnlyList<WizardStep> SplunkSteps() => new[]
    {
        Step(
            "command",
            Field(
                "subcommand",
                null,
                WizardFieldKind.Choice,
                new[]
                {
                    new WizardChoice(string.Empty, "(guided)"),
                    new WizardChoice("dashboards", "dashboards"),
                },
                positional: true)),
        GatedStep(
            "guided",
            string.Empty,
            Field("o11y", "--o11y", WizardFieldKind.Toggle, gate: "subcommand", gateValues: new[] { string.Empty }),
            Field("logs", "--logs", WizardFieldKind.Switch, gate: "subcommand", gateValues: new[] { string.Empty }),
            Field("s3-bucket", "--s3-bucket", gate: "subcommand", gateValues: new[] { string.Empty }),
            Field("hec-endpoint", "--hec-endpoint", gate: "subcommand", gateValues: new[] { string.Empty })),
        GatedStep(
            "dashboards-page",
            "dashboards",
            Field("dash:tf", "--terraform-dir", gate: "subcommand", gateValues: new[] { "dashboards" })),
    };

    [Fact]
    public void Splunk_loses_its_docker_flags_and_the_dashboards_subcommand()
    {
        var filtered = WizardWindowsPolicy.Filter("splunk", SplunkSteps());

        var flags = filtered.SelectMany(s => s.Fields).Select(f => f.Flag).ToArray();
        Assert.Contains("--o11y", flags);
        Assert.Contains("--hec-endpoint", flags);
        Assert.DoesNotContain("--logs", flags);
        Assert.DoesNotContain("--s3-bucket", flags);
        Assert.DoesNotContain("--terraform-dir", flags);
        Assert.DoesNotContain(filtered, s => s.Id == "dashboards-page");
    }

    [Fact]
    public void Splunk_drops_the_pick_a_subcommand_page_once_only_the_guided_choice_is_left()
    {
        var filtered = WizardWindowsPolicy.Filter("splunk", SplunkSteps());

        Assert.DoesNotContain(filtered, s => s.Id == "command");
        Assert.Equal(new[] { "guided" }, filtered.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Splunk_keeps_the_subcommand_page_while_a_real_alternative_remains()
    {
        var steps = new[]
        {
            Step(
                "command",
                Field(
                    "subcommand",
                    null,
                    WizardFieldKind.Choice,
                    new[]
                    {
                        new WizardChoice(string.Empty, "(guided)"),
                        new WizardChoice("verify", "verify"),
                        new WizardChoice("dashboards", "dashboards"),
                    },
                    positional: true)),
        };

        var filtered = WizardWindowsPolicy.Filter("splunk", steps);

        var page = Assert.Single(filtered);
        Assert.Equal(new[] { string.Empty, "verify" }, page.Fields[0].Choices.Select(c => c.Value).ToArray());
    }

    [Fact]
    public void The_splunk_suppressions_do_not_leak_to_other_targets()
    {
        var steps = new[] { Step("guided", Field("logs", "--logs"), Field("s3-bucket", "--s3-bucket")) };

        var filtered = WizardWindowsPolicy.Filter("galileo", steps);

        Assert.Equal(2, Assert.Single(filtered).Fields.Count);
    }

    // ------------------------------------------------------------------ cross-field checks

    private static WizardValues Values(params (string Id, string Value)[] answers)
    {
        var values = new WizardValues();
        foreach (var (id, value) in answers)
        {
            values[id] = value;
        }

        return values;
    }

    [Fact]
    public void Only_splunk_has_a_cross_validator()
    {
        Assert.NotNull(WizardWindowsPolicy.CrossValidatorFor("splunk"));
        Assert.Null(WizardWindowsPolicy.CrossValidatorFor("claude-code"));
        Assert.Null(WizardWindowsPolicy.CrossValidatorFor("llm"));
    }

    [Fact]
    public void Splunk_with_no_pipeline_chosen_is_refused_because_the_cli_would_open_a_prompt()
    {
        var validate = WizardWindowsPolicy.CrossValidatorFor("splunk")!;

        var message = validate(Values(("o11y", ToggleValues.Off), ("enterprise", ToggleValues.Off)));

        Assert.NotNull(message);
        Assert.Contains("interactive wizard", message, StringComparison.Ordinal);
        Assert.NotNull(validate(new WizardValues()));
    }

    [Fact]
    public void Splunk_enterprise_needs_a_hec_endpoint()
    {
        var validate = WizardWindowsPolicy.CrossValidatorFor("splunk")!;

        Assert.Contains("HEC endpoint", validate(Values(("enterprise", ToggleValues.On), ("hec-endpoint", "   ")))!, StringComparison.Ordinal);
        Assert.Null(validate(Values(("enterprise", ToggleValues.On), ("hec-endpoint", "https://splunk.example.test:8088"))));
    }

    [Fact]
    public void Splunk_observability_cloud_alone_is_enough_and_a_subcommand_skips_the_check()
    {
        var validate = WizardWindowsPolicy.CrossValidatorFor("splunk")!;

        Assert.Null(validate(Values(("o11y", ToggleValues.On))));
        Assert.Null(validate(Values(("subcommand", "verify"))));
    }
}
