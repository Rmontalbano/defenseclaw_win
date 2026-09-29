using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// Which environment variable NAME each secret-taking flag is satisfied through. The values never pass through
/// the app; only the names are known here.
/// </summary>
public class SecretRoutesTests
{
    private static WizardField Field(string id, WizardFieldKind kind, string flag, bool positional = false, params string[] gate) => new()
    {
        Id = id,
        Label = id,
        Kind = kind,
        Flag = positional ? null : flag,
        IsPositional = positional,
        VisibleWhenFieldId = gate.Length > 0 ? "subcommand" : null,
        VisibleWhenValues = gate,
    };

    private static IReadOnlyList<WizardStep> Annotated(string target, params WizardField[] fields) =>
        SecretRoutes.Annotate(target, new[] { new WizardStep { Id = "s", Title = "S", Fields = fields } });

    private static SecretRoute RouteOf(IReadOnlyList<WizardStep> steps, string flag) =>
        steps.SelectMany(s => s.Fields).Single(f => f.Flag == flag).Credential!;

    [Fact]
    public void The_llm_key_maps_to_the_default_variable_until_the_operator_names_another()
    {
        var steps = Annotated(
            "llm",
            Field("api-key", WizardFieldKind.Secret, "--api-key"),
            Field("api-key-env", WizardFieldKind.EnvVarName, "--api-key-env"));
        var route = RouteOf(steps, "--api-key");

        Assert.Equal("LLM provider API key", route.Purpose);
        Assert.Equal("DEFENSECLAW_LLM_KEY", route.EnvName(new WizardValues()));

        var values = new WizardValues();
        values["api-key-env"] = "  MY_OPENAI_KEY  ";
        Assert.Equal("MY_OPENAI_KEY", route.EnvName(values));
    }

    [Fact]
    public void The_llm_key_maps_to_the_default_variable_when_the_wizard_has_no_env_name_field()
    {
        var route = RouteOf(Annotated("llm", Field("api-key", WizardFieldKind.Secret, "--api-key")), "--api-key");

        Assert.Equal("DEFENSECLAW_LLM_KEY", route.EnvName(new WizardValues()));
    }

    [Fact]
    public void The_llm_wizard_built_from_help_routes_its_key_to_defenseclaw_llm_key()
    {
        var definition = WizardSamples.Llm();

        var credential = Assert.Single(definition.AllFields, f => f.Kind == WizardFieldKind.Secret);

        Assert.NotNull(credential.Credential);
        Assert.Equal("DEFENSECLAW_LLM_KEY", credential.Credential!.EnvName(WizardSamples.StartingValues(definition)));
    }

    [Theory]
    [InlineData("gateway", "--token", "OpenClaw gateway token", "OPENCLAW_GATEWAY_TOKEN")]
    [InlineData("splunk", "--access-token", "Splunk Observability Cloud access token", "SPLUNK_ACCESS_TOKEN")]
    [InlineData("splunk", "--hec-token", "Splunk Enterprise HEC token", "DEFENSECLAW_SPLUNK_HEC_TOKEN")]
    public void Each_verified_flag_has_a_fixed_variable(string target, string flag, string purpose, string envName)
    {
        var route = RouteOf(Annotated(target, Field("secret", WizardFieldKind.Secret, flag)), flag);

        Assert.Equal(purpose, route.Purpose);
        Assert.Equal(envName, route.EnvName(new WizardValues()));
        Assert.False(string.IsNullOrWhiteSpace(route.IfMissing));
    }

    [Theory]
    [InlineData("splunk-o11y", "SPLUNK_ACCESS_TOKEN")]
    [InlineData("splunk-hec", "DEFENSECLAW_SPLUNK_HEC_TOKEN")]
    [InlineData("splunk-enterprise", "DEFENSECLAW_SPLUNK_HEC_TOKEN")]
    [InlineData("datadog", "DD_API_KEY")]
    [InlineData("Datadog", "DD_API_KEY")]
    [InlineData("honeycomb", "HONEYCOMB_API_KEY")]
    [InlineData("newrelic", "NEW_RELIC_LICENSE_KEY")]
    [InlineData("grafana-cloud", "GRAFANA_OTLP_TOKEN")]
    [InlineData("galileo", "GALILEO_API_KEY")]
    public void An_observability_token_maps_to_the_variable_its_preset_reads(string preset, string envName)
    {
        var route = ObservabilityRoute();
        var values = new WizardValues();
        values["add:arg1-preset"] = "  " + preset + " ";

        Assert.Equal(envName, route.EnvName(values));
    }

    [Fact]
    public void An_unknown_or_unpicked_observability_preset_has_no_known_variable()
    {
        var route = ObservabilityRoute();

        Assert.Null(route.EnvName(new WizardValues()));

        var values = new WizardValues();
        values["add:arg1-preset"] = "some-new-vendor";
        Assert.Null(route.EnvName(values));
    }

    [Fact]
    public void The_observability_route_reads_the_preset_of_its_own_subcommand()
    {
        var help = SetupHelpParser.Parse(WizardSamples.ObservabilityHelp);
        var add = SetupHelpParser.Parse(WizardSamples.ObservabilityAddHelp, commandDepth: 2);
        var steps = WizardStepFactory.BuildGroup(help, new Dictionary<string, ParsedHelp> { ["add"] = add });
        var annotated = SecretRoutes.Annotate("observability", steps);

        var token = annotated.SelectMany(s => s.Fields).Single(f => f.Kind == WizardFieldKind.Secret);
        var values = new WizardValues();
        values["add:arg1-preset"] = "datadog";

        Assert.Equal("telemetry destination token", token.Credential!.Purpose);
        Assert.Equal("DD_API_KEY", token.Credential.EnvName(values));
    }

    [Fact]
    public void A_secret_flag_with_no_verified_route_still_gets_one_so_it_is_never_rendered_as_an_input()
    {
        var steps = Annotated("mystery", Field("shhh", WizardFieldKind.Secret, "--shared-secret"));
        var route = RouteOf(steps, "--shared-secret");

        Assert.NotNull(route);
        Assert.Null(route.EnvName(new WizardValues()));
        Assert.Equal("shhh", route.Purpose);
        Assert.Contains("never does", route.IfMissing, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_secret_fields_are_annotated_and_steps_without_secrets_are_the_same_objects()
    {
        var plainStep = new WizardStep { Id = "plain", Title = "Plain", Fields = new[] { Field("model", WizardFieldKind.Text, "--model") } };
        var secretStep = new WizardStep
        {
            Id = "secret",
            Title = "Secret",
            Fields = new[]
            {
                Field("api-key", WizardFieldKind.Secret, "--api-key"),
                Field("api-key-env", WizardFieldKind.EnvVarName, "--api-key-env"),
            },
        };

        var result = SecretRoutes.Annotate("llm", new[] { plainStep, secretStep });

        Assert.Same(plainStep, result[0]);
        Assert.NotSame(secretStep, result[1]);
        Assert.NotNull(result[1].Fields[0].Credential);
        Assert.Null(result[1].Fields[1].Credential);
        Assert.Equal(WizardFieldKind.Secret, result[1].Fields[0].Kind);
    }

    [Fact]
    public void A_wizard_with_no_secret_fields_is_returned_untouched()
    {
        IReadOnlyList<WizardStep> steps = new[] { new WizardStep { Id = "s", Title = "S", Fields = new[] { Field("model", WizardFieldKind.Text, "--model") } } };

        Assert.Same(steps, SecretRoutes.Annotate("llm", steps));
    }

    private static SecretRoute ObservabilityRoute()
    {
        var steps = Annotated(
            "observability",
            Field("add:arg1-preset", WizardFieldKind.Text, "", positional: true, "add"),
            Field("add:token", WizardFieldKind.Secret, "--token", false, "add"));

        return RouteOf(steps, "--token");
    }
}
