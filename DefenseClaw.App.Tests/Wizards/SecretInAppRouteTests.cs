using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// Which secret-taking flags may be typed into the app and delivered in the child's environment, and which stay
/// terminal-only. The decisions rest on the installed CLI's source (0.8.10): where it reads an environment
/// variable in place of the flag the route is offered; where it does not, it is not — a value supplied that way
/// would simply be ignored.
/// </summary>
public class SecretInAppRouteTests
{
    /// <summary>The 0.8.10 <c>setup splunk --help</c> screen, trimmed to the options the wizard works from.</summary>
    internal static readonly string SplunkHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup splunk [OPTIONS] [COMMAND] [ARGS]...

          Configure Splunk integration for DefenseClaw.

        Options:
          --o11y                          Enable Splunk Observability Cloud (OTLP
                                          traces + metrics)
          --logs                          Enable local Splunk via Docker (HEC logs +
                                          dashboards, Free mode)
          --enterprise                    Enable remote Splunk Enterprise via HEC
                                          endpoint + token
          --realm TEXT                    Splunk O11y realm (e.g. us1, us0, eu0)
          --access-token TEXT             Splunk O11y access token
          --hec-endpoint TEXT             Remote Splunk Enterprise HEC endpoint
          --hec-token TEXT                Remote Splunk Enterprise HEC token
          --non-interactive               Use flags instead of prompts
          --help                          Show this message and exit.

        Commands:
          dashboards  Create/update Splunk Observability Cloud dashboards.
        """);

    /// <summary>
    /// The 0.8.10 <c>setup observability add --help</c> screen (trimmed). Note how Click wraps the
    /// <c>[env var: …]</c> marker of <c>--token</c> across two lines.
    /// </summary>
    internal static readonly string ObservabilityAddHelpWithEnvVar = LineEndings.Normalize("""
        Usage: defenseclaw setup observability add [OPTIONS] <preset>

          Configure a telemetry destination.

        Options:
          --name TEXT                     Destination name (default: derived from
                                          preset+inputs)
          --token TEXT                    Secret value to persist under the preset's
                                          token_env in ~/.defenseclaw/.env  [env var:
                                          DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN]
          --dry-run                       Preview YAML/dotenv changes without writing
          --non-interactive               Skip prompts; use flags only
          --help                          Show this message and exit.
        """);

    internal static WizardDefinition Splunk()
    {
        var help = SetupHelpParser.Parse(SplunkHelp);
        var dashboards = new Dictionary<string, ParsedHelp> { ["dashboards"] = new() { Summary = "Create/update dashboards." } };

        // The same pipeline WizardCatalog runs: build, Windows policy, then secret routes.
        var steps = WizardStepFactory.BuildGroup(help, dashboards);
        steps = WizardWindowsPolicy.Filter("splunk", steps);
        steps = SecretRoutes.Annotate("splunk", steps);

        return new WizardDefinition
        {
            Target = "splunk",
            Title = "Splunk",
            Group = WizardGroups.Observability,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsDetailLoaded = true,
            CrossValidator = WizardWindowsPolicy.CrossValidatorFor("splunk"),
        };
    }

    internal static WizardDefinition Observability(string addHelp)
    {
        var help = SetupHelpParser.Parse(WizardSamples.ObservabilityHelp);
        var add = SetupHelpParser.Parse(addHelp, commandDepth: 2);
        var steps = WizardStepFactory.BuildGroup(help, new Dictionary<string, ParsedHelp> { ["add"] = add });
        steps = WizardWindowsPolicy.Filter("observability", steps);
        steps = SecretRoutes.Annotate("observability", steps);

        return new WizardDefinition
        {
            Target = "observability",
            Title = "Observability",
            Group = WizardGroups.Observability,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsDetailLoaded = true,
        };
    }

    private static SecretRoute RouteOf(WizardDefinition definition, string flag) =>
        definition.AllFields.Single(f => f.Flag == flag && f.Kind == WizardFieldKind.Secret).Credential!;

    [Theory]
    [InlineData("--access-token", "SPLUNK_ACCESS_TOKEN")]
    [InlineData("--hec-token", "DEFENSECLAW_SPLUNK_HEC_TOKEN")]
    public void Splunk_tokens_are_typed_in_the_app_and_delivered_as_the_variable_the_cli_reads(string flag, string variable)
    {
        var definition = Splunk();
        var route = RouteOf(definition, flag);

        Assert.Equal(variable, route.InAppVariable(WizardSamples.StartingValues(definition)));

        // Where it is stored is the same variable, so the card's "is it set" check and the delivery agree.
        Assert.Equal(variable, route.EnvName(new WizardValues()));
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
    public void An_observability_token_is_delivered_through_the_one_variable_the_option_is_bound_to(string preset, string storedAs)
    {
        var route = RouteOf(Observability(ObservabilityAddHelpWithEnvVar), "--token");
        var values = new WizardValues();
        values["add:arg1-preset"] = " " + preset + " ";

        // Delivered under the option's own variable; the CLI stores it under the preset's name.
        Assert.Equal("DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN", route.InAppVariable(values));
        Assert.Equal(storedAs, route.EnvName(values));
    }

    [Theory]
    [InlineData("")]
    [InlineData("some-new-vendor")]
    [InlineData("otlp")]
    public void An_observability_preset_with_no_known_token_has_no_in_app_route(string preset)
    {
        var route = RouteOf(Observability(ObservabilityAddHelpWithEnvVar), "--token");
        var values = new WizardValues();
        values["add:arg1-preset"] = preset;

        Assert.Null(route.InAppVariable(values));
    }

    [Fact]
    public void The_observability_route_is_offered_only_while_the_cli_help_still_advertises_the_variable()
    {
        // WizardSamples.ObservabilityAddHelp has no "[env var: …]" marker: a CLI that dropped the binding would
        // ignore the environment, so the token stays terminal-only rather than being sent where nothing reads it.
        var route = RouteOf(Observability(WizardSamples.ObservabilityAddHelp), "--token");
        var values = new WizardValues();
        values["add:arg1-preset"] = "datadog";

        Assert.Null(route.InAppVariable(values));
        Assert.Equal("DD_API_KEY", route.EnvName(values)); // the terminal card still knows what to store
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_parser_keeps_the_wrapped_env_var_marker_in_the_option_help_under_either_line_ending(string eol)
    {
        // The real CLI pipes CRLF on Windows; the marker is wrapped across two lines whichever ending it uses.
        var definition = Observability(LineEndings.With(ObservabilityAddHelpWithEnvVar, eol));
        var token = definition.AllFields.Single(f => f.Kind == WizardFieldKind.Secret);
        var values = new WizardValues();
        values["add:arg1-preset"] = "datadog";

        Assert.Contains("DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN", token.Help, StringComparison.Ordinal);
        Assert.Equal("DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN", token.Credential!.InAppVariable(values));
    }

    [Theory]
    [InlineData("llm", "--api-key")]
    [InlineData("gateway", "--token")]
    [InlineData("mystery", "--shared-secret")]
    public void A_flag_the_cli_reads_only_from_its_command_line_stays_terminal_only(string target, string flag)
    {
        var field = new WizardField { Id = "secret", Label = "Secret", Kind = WizardFieldKind.Secret, Flag = flag };
        var steps = SecretRoutes.Annotate(target, new[] { new WizardStep { Id = "s", Title = "S", Fields = new[] { field } } });
        var route = steps[0].Fields[0].Credential!;

        Assert.Null(route.InAppEnvName);
        Assert.Null(route.InAppVariable(new WizardValues()));
    }

    [Fact]
    public void The_llm_key_stays_terminal_only_even_when_the_wizard_names_its_own_variable()
    {
        var definition = WizardSamples.Llm();
        var route = RouteOf(definition, "--api-key");
        var values = WizardSamples.StartingValues(definition);
        values["api-key-env"] = "MY_OPENAI_KEY";

        Assert.Equal("MY_OPENAI_KEY", route.EnvName(values));
        Assert.Null(route.InAppVariable(values));
    }

    [Theory]
    [InlineData("NOT A NAME")]
    [InlineData("HAS=EQUALS")]
    [InlineData("1DIGIT_FIRST")]
    [InlineData("")]
    public void A_variable_name_that_is_not_a_legal_environment_name_is_treated_as_no_route(string name)
    {
        var route = new SecretRoute
        {
            Purpose = "x",
            EnvName = _ => null,
            IfMissing = "x",
            InAppEnvName = _ => name,
        };

        Assert.Null(route.InAppVariable(new WizardValues()));
    }

    [Fact]
    public void A_secret_never_reaches_argv_even_if_it_were_put_in_the_answers()
    {
        // Emit() writes nothing for a secret field; this pins that so no future route can leak through the values.
        var definition = Splunk();
        var values = WizardSamples.StartingValues(definition);
        values["o11y"] = ToggleValues.On;
        var token = definition.AllFields.Single(f => f.Flag == "--access-token");
        values[token.Id] = "synthetic-should-never-appear-0001";

        var argv = definition.BuildArgv(values);

        Assert.All(argv, a => Assert.DoesNotContain("synthetic-should-never-appear", a, StringComparison.Ordinal));
        Assert.DoesNotContain("--access-token", argv);
    }
}
