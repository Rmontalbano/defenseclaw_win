using DefenseClaw.App.Services.Wizards;
using System.Text.RegularExpressions;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The <c>setup decision-model</c> wizard on DefenseClaw 1.0.10 (<c>--protocol</c>, retired <c>--on-confident-block</c>; CUST-458) and, next to it, the
/// proof that a 1.0.7 to 1.0.9 help (no <c>--protocol</c>) is laid out word for word as it was. The help fixtures are synthetic, written in Click's layout
/// from the options the CLI declares.
/// </summary>
public sealed class DecisionModelProtocolTests
{
    private const string Intro109 =
        "A small, fast model that speaks the System One protocol. It stays off until you turn it on or name an endpoint or a model. " +
        "If it is down, the rules verdict and the LLM judge, where it runs, stand.";

    private static readonly string Help109 = LineEndings.Normalize("""
        Usage: defenseclaw setup decision-model [OPTIONS]

          Configure the unified decision model (decision_model:).

          A small, fast model that speaks the System One protocol. Off by default.

        Options:
          --show                          Print the current decision model settings and exit.
          --enable / --disable            Turn the decision model on or off.
          --model TEXT                    Model name the System One endpoint serves (sent in each request).
          --base-url TEXT                 Full endpoint URL, e.g. http://127.0.0.1:8791/v1/systemone. https anywhere, http only to loopback.
          --api-key-env TEXT              Env var holding the bearer token (default DEFENSECLAW_DECISION_MODEL_KEY; local servers need none).
          --timeout FLOAT                 Seconds one call may take (default 1; never more than a quarter of the judge's time).
          --allow-threshold FLOAT         Risk at or below this skips the judge (default 0.30).
          --block-threshold FLOAT         Risk at or above this is a confident block (default 0.75).
          --on-confident-block [escalate|decide]
                                          escalate (default): the judge still checks a confident block. decide: the block stands without the judge.
          --connectors TEXT               Connectors the decision model answers on its own where the LLM judge does not run: comma-separated names, or * for all. An empty string clears it.
          --ping / --no-ping              After saving, send one benign request to check the endpoint.
          --non-interactive, --accept-defaults  Use flags instead of prompts.
          --help                          Show this message and exit.
        """);

    private static readonly string Help1010 = LineEndings.Normalize("""
        Usage: defenseclaw setup decision-model [OPTIONS]

          Configure the unified decision model (decision_model:).

          A small, fast model behind a wire protocol. Off by default.

        Options:
          --show                          Print the current decision model settings and exit.
          --enable / --disable            Turn the decision model on or off.
          --model TEXT                    Model name the System One endpoint serves (sent in each request).
          --base-url TEXT                 Full endpoint URL, e.g. http://127.0.0.1:8791/v1/systemone. https anywhere, http only to loopback.
          --protocol [system-one|gemini-decisions]
                                          Wire protocol: system-one (default) or gemini-decisions (Gemini Decision 1, Google AI Studio key).
          --api-key-env TEXT              Env var holding the token (default DEFENSECLAW_DECISION_MODEL_KEY; local servers need none).
          --timeout FLOAT                 Seconds one call may take (default 1; never more than a quarter of the judge's time).
          --allow-threshold FLOAT         Risk at or below this skips the judge (default 0.30).
          --block-threshold FLOAT         Risk at or above this is a confident block (default 0.75).
          --on-confident-block [escalate|decide]
                                          Retired: a confident block always goes to the judge. Accepted for old scripts; either value removes the key from config.yaml.
          --connectors TEXT               Connectors the decision model answers on its own where the LLM judge does not run: comma-separated names, or * for all. An empty string clears it.
          --ping / --no-ping              After saving, send one benign request to check the endpoint.
          --non-interactive, --accept-defaults  Use flags instead of prompts.
          --help                          Show this message and exit.
        """);

    private static WizardDefinition Definition(string helpText)
    {
        var help = SetupHelpParser.Parse(helpText);
        var (steps, curated) = WizardStepFactory.Build("decision-model", help);
        steps = WizardWindowsPolicy.Filter("decision-model", steps);
        steps = SecretRoutes.Annotate("decision-model", steps);

        return new WizardDefinition
        {
            Target = "decision-model",
            Title = "Decision model",
            Group = WizardGroups.GuardrailAndPolicy,
            Description = help.Summary,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsCurated = curated,
            IsDetailLoaded = true,
        };
    }

    private static IReadOnlyList<string> Argv(string helpText, Action<WizardValues>? answer = null)
    {
        var definition = Definition(helpText);
        var values = WizardSamples.StartingValues(definition);
        answer?.Invoke(values);
        return definition.BuildArgv(values);
    }

    private static string AllText(WizardDefinition definition) => string.Join(
        "\n",
        definition.Steps.SelectMany(s => new[] { s.Title, s.Subtitle }
            .Concat(s.Fields.SelectMany(f => new[] { f.Label, f.Help, f.Placeholder }.Concat(f.Choices.Select(c => c.Label))))));

    // ------------------------------------------------------------------ 1.0.10

    [Fact]
    public void On_1_0_10_the_retired_flag_is_not_offered_anywhere_and_decide_is_not_in_the_text()
    {
        var definition = Definition(Help1010);

        Assert.DoesNotContain(definition.AllFields, f => f.Flag == "--on-confident-block" || f.Id == "on-confident-block");
        Assert.DoesNotMatch(new Regex(@"\bdecide\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), AllText(definition));
        Assert.DoesNotContain(definition.Steps, s => s.Id.StartsWith("more-", StringComparison.Ordinal));
        Assert.DoesNotContain("--on-confident-block", Argv(Help1010));
    }

    [Fact]
    public void On_1_0_10_the_protocol_is_a_choice_of_two_that_sends_nothing_unless_picked()
    {
        var definition = Definition(Help1010);
        var protocol = definition.Steps.Single(s => s.Id == "decision-model").Fields[0];

        Assert.Equal("--protocol", protocol.Flag);
        Assert.Equal(WizardFieldKind.Choice, protocol.Kind);
        Assert.Equal(
            new[] { string.Empty, DecisionModelCard.SystemOneProtocol, DecisionModelCard.GeminiProtocol },
            protocol.Choices.Select(c => c.Value).ToArray());
        Assert.Contains(protocol.Choices, c => c.Label == "System One (default)");
        Assert.Contains(protocol.Choices, c => c.Label == "Gemini Decision 1");

        Assert.Equal(new[] { "setup", "decision-model", "--non-interactive" }, Argv(Help1010));
        Assert.Equal(
            new[] { "setup", "decision-model", "--protocol", "gemini-decisions", "--non-interactive" },
            Argv(Help1010, v => v["protocol"] = DecisionModelCard.GeminiProtocol));
        Assert.Equal(
            new[] { "setup", "decision-model", "--protocol", "system-one", "--non-interactive" },
            Argv(Help1010, v => v["protocol"] = DecisionModelCard.SystemOneProtocol));
    }

    [Fact]
    public void On_1_0_10_the_copy_is_neutral_between_the_two_protocols()
    {
        var definition = Definition(Help1010);
        var text = AllText(definition);

        var page = definition.Steps.Single(s => s.Id == "decision-model");
        Assert.DoesNotContain("speaks the System One protocol", page.Subtitle, StringComparison.Ordinal);

        var url = definition.AllFields.Single(f => f.Flag == "--base-url");
        Assert.Contains("https://generativelanguage.googleapis.com/v1beta/decisions", url.Help + url.Placeholder, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:8791/v1/systemone", url.Help + url.Placeholder, StringComparison.Ordinal);
        Assert.Contains("?key= is refused", url.Help, StringComparison.Ordinal);

        Assert.Contains("gemini-decision-1", definition.AllFields.Single(f => f.Flag == "--model").Help, StringComparison.Ordinal);

        var token = definition.AllFields.Single(f => f.Flag == "--api-key-env");
        Assert.DoesNotContain("bearer token (", token.Help, StringComparison.Ordinal);
        Assert.Contains("x-goog-api-key", token.Help, StringComparison.Ordinal);
        Assert.Contains("defenseclaw keys set DEFENSECLAW_DECISION_MODEL_KEY", token.Help, StringComparison.Ordinal);

        var block = definition.AllFields.Single(f => f.Flag == "--block-threshold");
        Assert.Contains("decision_model.block_risk_score", block.Help, StringComparison.Ordinal);
        Assert.Contains("0.75", block.Help, StringComparison.Ordinal);

        var connectors = definition.Steps.Single(s => s.Id == "decision-model-connectors").Fields[0].Help;
        Assert.Contains("decision_model.block_risk_score", connectors, StringComparison.Ordinal);
        Assert.Contains("flow_guard", connectors, StringComparison.Ordinal);
        Assert.Contains("decision_model.history_turns", connectors, StringComparison.Ordinal);
        Assert.Contains("25 turns", connectors, StringComparison.Ordinal);
        Assert.Contains("stands if it gives no verdict", connectors, StringComparison.Ordinal);
        Assert.Contains("Each connector is on one of three paths", connectors, StringComparison.Ordinal);
        Assert.NotEmpty(text);
    }

    [Fact]
    public void On_1_0_10_the_review_warns_about_rolling_back_and_about_a_missing_url_or_model()
    {
        var bare = DecisionModelReview.Warnings(new[] { "setup", "decision-model", "--protocol", "gemini-decisions", "--non-interactive" });

        Assert.Equal(2, bare.Count);
        Assert.Equal(DecisionModelReview.RollbackTitle, bare[0].Title);
        Assert.Contains("decision_model.protocol removed first", bare[0].Message, StringComparison.Ordinal);
        Assert.Equal(DecisionModelReview.GeminiNeedsEndpointTitle, bare[1].Title);
        Assert.Contains("setup fills in nothing in non-interactive mode", bare[1].Message, StringComparison.Ordinal);

        var full = DecisionModelReview.Warnings(new[]
        {
            "setup", "decision-model", "--protocol", "gemini-decisions",
            "--base-url", "https://generativelanguage.googleapis.com/v1beta/decisions", "--model", "gemini-decision-1", "--non-interactive",
        });
        var only = Assert.Single(full);
        Assert.Equal(DecisionModelReview.RollbackTitle, only.Title);
    }

    // The CLI infers the protocol from a Google Decisions API URL and writes decision_model.protocol (decision_model.py@1.0.10:99-102, infer_protocol).
    private const string GoogleUrl = "https://generativelanguage.googleapis.com/v1beta/decisions";

    [Theory]
    [InlineData(GoogleUrl)]
    [InlineData("https://GenerativeLanguage.GoogleAPIs.com:443/v1beta/decisions")]
    [InlineData("  https://generativelanguage.googleapis.com/v1beta/decisions  ")]
    public void A_google_url_with_no_protocol_still_carries_the_rollback_bar_because_the_cli_writes_the_key(string url)
    {
        var warnings = DecisionModelReview.Warnings(
            new[] { "setup", "decision-model", "--base-url", url, "--model", "gemini-decision-1", "--non-interactive" },
            ConfigYamlReader.Parse("decision_model:\n  enabled: false\n"));

        var bar = Assert.Single(warnings);
        Assert.Equal(DecisionModelReview.RollbackTitle, bar.Title);
        Assert.Contains("decision_model.protocol removed first", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inference_needs_no_saved_protocol_no_protocol_flag_and_googles_host()
    {
        var google = new[] { "setup", "decision-model", "--base-url", GoogleUrl, "--model", "m", "--non-interactive" };

        // A protocol already saved is not inferred over (nothing new is written), system-one is never written, and another host is not Google's.
        Assert.Empty(DecisionModelReview.Warnings(google, ConfigYamlReader.Parse("decision_model:\n  protocol: system-one\n")));
        Assert.Empty(DecisionModelReview.Warnings(
            new[] { "setup", "decision-model", "--protocol", "system-one", "--base-url", GoogleUrl, "--model", "m", "--non-interactive" },
            ConfigYamlReader.Parse(null)));
        Assert.Empty(DecisionModelReview.Warnings(
            new[] { "setup", "decision-model", "--base-url", "https://generativelanguage.googleapis.com.example.net/v1", "--model", "m", "--non-interactive" },
            ConfigYamlReader.Parse(null)));
        Assert.Empty(DecisionModelReview.Warnings(
            new[] { "setup", "decision-model", "--base-url", "https://example.net/generativelanguage.googleapis.com", "--model", "m", "--non-interactive" },
            ConfigYamlReader.Parse(null)));
        Assert.Empty(DecisionModelReview.Warnings(
            new[] { "setup", "decision-model", "--base-url", "http://127.0.0.1:8791/v1/systemone", "--non-interactive" }, ConfigYamlReader.Parse(null)));
        Assert.Empty(DecisionModelReview.Warnings(new[] { "setup", "decision-model", "--model", "m", "--non-interactive" }, ConfigYamlReader.Parse(null)));
        Assert.Single(DecisionModelReview.Warnings(google, ConfigYamlReader.Parse(null)));
    }

    [Fact]
    public void The_url_and_model_bar_counts_what_config_yaml_already_holds()
    {
        var explicitGemini = new[] { "setup", "decision-model", "--protocol", "gemini-decisions", "--non-interactive" };
        var saved = ConfigYamlReader.Parse("decision_model:\n  base_url: " + GoogleUrl + "\n  model: gemini-decision-1\n");
        var onlyUrl = ConfigYamlReader.Parse("decision_model:\n  base_url: " + GoogleUrl + "\n");

        // Both saved already: the rollback bar only.
        Assert.Equal(DecisionModelReview.RollbackTitle, Assert.Single(DecisionModelReview.Warnings(explicitGemini, saved)).Title);

        // One missing: the bar still shows; none known (no config): as before.
        Assert.Contains(DecisionModelReview.Warnings(explicitGemini, onlyUrl), w => w.Title == DecisionModelReview.GeminiNeedsEndpointTitle);
        Assert.Contains(DecisionModelReview.Warnings(explicitGemini), w => w.Title == DecisionModelReview.GeminiNeedsEndpointTitle);

        // An empty --base-url clears the saved one.
        var cleared = new[] { "setup", "decision-model", "--protocol", "gemini-decisions", "--base-url", string.Empty, "--non-interactive" };
        Assert.Contains(DecisionModelReview.Warnings(cleared, saved), w => w.Title == DecisionModelReview.GeminiNeedsEndpointTitle);
    }

    [Fact]
    public void The_review_has_nothing_to_add_for_system_one_or_for_no_protocol()
    {
        Assert.Empty(DecisionModelReview.Warnings(new[] { "setup", "decision-model", "--protocol", "system-one", "--non-interactive" }));
        Assert.Empty(DecisionModelReview.Warnings(new[] { "setup", "decision-model", "--non-interactive" }));
    }

    // ------------------------------------------------------------------ 1.0.7 to 1.0.9: word for word

    [Fact]
    public void A_help_without_protocol_is_laid_out_exactly_as_before()
    {
        var definition = Definition(Help109);

        Assert.Equal(
            new[] { "decision-model", "decision-model-connectors", "decision-model-thresholds", "decision-model-apply" },
            definition.Steps.Select(s => s.Id).ToArray());
        Assert.DoesNotContain(definition.AllFields, f => f.Flag == "--protocol");

        var page = definition.Steps[0];
        Assert.Equal(Intro109, page.Subtitle);
        Assert.Equal(new[] { "--enable", "--base-url", "--model", "--api-key-env", "--show" }, page.Fields.Select(f => f.Flag).ToArray());

        var url = page.Fields[1];
        Assert.Equal("The full endpoint URL. https is accepted anywhere, http only to a loopback address.", url.Help);
        Assert.Equal("http://127.0.0.1:8791/v1/systemone", url.Placeholder);
        Assert.Equal("The model name the endpoint serves. It is sent in each request.", page.Fields[2].Help);
        Assert.Equal(
            "The NAME of the variable that holds the bearer token, DEFENSECLAW_DECISION_MODEL_KEY if blank. A local server needs none. " +
            "Never type the token here. Store it with `defenseclaw keys set DEFENSECLAW_DECISION_MODEL_KEY` in a terminal, which writes ~/.defenseclaw/.env, then restart the gateway.",
            page.Fields[3].Help);

        var connectors = definition.Steps[1].Fields[0].Help;
        Assert.Equal(
            "The decision model answers on its own for the connectors in this list, where the LLM judge does not run. A confident allow is allowed, " +
            "a confident block is denied, and anything else asks (Claude Code, Copilot, Amp, Antigravity) or raises a HIGH alert (strict mode blocks). " +
            "Where the judge runs, the model allows clear benign calls and sends the rest to it. " +
            "Each connector is on one of three paths: rules only, rules then decision model, or rules then decision model then judge.",
            connectors);

        var thresholds = definition.Steps[2];
        Assert.Equal(
            new[] { "--timeout", "--allow-threshold", "--block-threshold", "--on-confident-block" },
            thresholds.Fields.Select(f => f.Flag).ToArray());
        Assert.Equal("A risk at or above this is a confident block. The CLI's default is 0.75.", thresholds.Fields[2].Help);

        var onBlock = thresholds.Fields[3];
        Assert.Equal("On a confident block", onBlock.Label);
        Assert.Equal("escalate is the default and the judge still checks a confident block. decide lets the block stand without the judge.", onBlock.Help);
        Assert.Contains(onBlock.Choices, c => c.Value == "decide");
    }

    [Fact]
    public void A_help_without_protocol_builds_the_same_argv_and_the_same_review_as_before()
    {
        Assert.Equal(new[] { "setup", "decision-model", "--non-interactive" }, Argv(Help109));
        Assert.Equal(
            new[] { "setup", "decision-model", "--on-confident-block", "decide", "--non-interactive" },
            Argv(Help109, v => v["on-confident-block"] = "decide"));

        // The 1.0.10 rollback bar is only for the value the 1.0.10 wizard alone can send.
        Assert.Empty(DecisionModelReview.Warnings(new[] { "setup", "decision-model", "--on-confident-block", "decide", "--non-interactive" }));
    }
}
