using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// What the status strip repeats and no poll of its own brings (CUST-273): the credentials still missing, read by the Setup panel's Credentials card
/// (CUST-266), and the aggregate redaction label, worded by the Overview's Observability card (CUST-272). The holder, and the two panels that hand
/// their facts over - and, as important, that a failed read hands over nothing. Synthetic data only.
/// </summary>
public sealed class StatusFactsTests : IDisposable
{
    private const string Listing = """
        [
          {"env_name": "EXAMPLE_JUDGE_KEY", "canonical_env_name": "EXAMPLE_JUDGE_KEY", "feature": "LLM judge", "description": "d", "requirement": "required", "source": "unset", "set": false},
          {"env_name": "EXAMPLE_SCANNER_KEY", "canonical_env_name": "EXAMPLE_SCANNER_KEY", "feature": "Scanner", "description": "d", "requirement": "required", "source": "dotenv", "set": true},
          {"env_name": "EXAMPLE_TELEMETRY_KEY", "canonical_env_name": "EXAMPLE_TELEMETRY_KEY", "feature": "Telemetry", "description": "d", "requirement": "required", "source": "unset", "set": false},
          {"env_name": "EXAMPLE_OPTIONAL_KEY", "canonical_env_name": "EXAMPLE_OPTIONAL_KEY", "feature": "Telemetry", "description": "d", "requirement": "optional", "source": "unset", "set": false}
        ]
        """;

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _created = new();

    public void Dispose()
    {
        foreach (var services in _created)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(string? config = null)
    {
        var services = TestServices.Create(_temp, config);
        _created.Add(services);
        return services;
    }

    // ---- The holder -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_fact_nobody_has_handed_over_is_null_not_none()
    {
        var facts = new StatusFacts();

        Assert.Null(facts.MissingKeys);
        Assert.Null(facts.Redaction);
    }

    [Fact]
    public void Publishing_raises_changed_only_when_what_is_held_differs()
    {
        var facts = new StatusFacts();
        var raised = 0;
        facts.Changed += (_, _) => raised++;

        facts.PublishMissingKeys(new[] { "A", "B" });
        Assert.Equal(1, raised);

        facts.PublishMissingKeys(new[] { "A", "B" });
        Assert.Equal(1, raised);

        facts.PublishMissingKeys(new[] { "B", "A" });
        Assert.Equal(2, raised);

        facts.PublishMissingKeys(Array.Empty<string>());
        Assert.Equal(3, raised);
        Assert.NotNull(facts.MissingKeys);
        Assert.Empty(facts.MissingKeys!);

        facts.PublishMissingKeys(Array.Empty<string>());
        Assert.Equal(3, raised);

        facts.PublishRedaction("per-route · unredacted");
        facts.PublishRedaction("per-route · unredacted");
        Assert.Equal(4, raised);

        facts.PublishRedaction("per-route · sensitive");
        Assert.Equal(5, raised);
    }

    [Fact]
    public void The_first_empty_list_is_news_it_says_the_credentials_were_read_and_none_is_missing()
    {
        var facts = new StatusFacts();
        var raised = 0;
        facts.Changed += (_, _) => raised++;

        facts.PublishMissingKeys(Array.Empty<string>());

        Assert.Equal(1, raised);
        Assert.NotNull(facts.MissingKeys);
    }

    [Fact]
    public void Names_are_trimmed_blanks_are_dropped_and_the_order_the_card_gave_is_kept()
    {
        var facts = new StatusFacts();

        facts.PublishMissingKeys(new[] { "  B_KEY ", "", "  ", "A_KEY" });

        Assert.Equal(new[] { "B_KEY", "A_KEY" }, facts.MissingKeys);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_fail_the_panel_that_published_or_silence_the_others()
    {
        var facts = new StatusFacts();
        var heard = 0;
        facts.Changed += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        facts.Changed += (_, _) => heard++;

        facts.PublishRedaction("per-route · unredacted");

        Assert.Equal(1, heard);
        Assert.Equal("per-route · unredacted", facts.Redaction);
    }

    [Fact]
    public void Nothing_null_goes_in()
    {
        var facts = new StatusFacts();

        _ = Assert.Throws<ArgumentNullException>(() => facts.PublishMissingKeys(null!));
        _ = Assert.Throws<ArgumentNullException>(() => facts.PublishRedaction(null!));
    }

    // ---- The Credentials card hands over what it read -------------------------------------------------------------------------------------

    private sealed class CredentialHarness
    {
        public CredentialHarness(AppServices services)
        {
            Credentials = new CredentialsViewModel(services) { RunRead = RunAsync };
        }

        public string Output { get; set; } = Listing;

        public int ExitCode { get; set; }

        public CredentialsViewModel Credentials { get; }

        private Task<CliInvocation> RunAsync(IReadOnlyList<string> argv)
        {
            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            foreach (var line in Output.Split('\n'))
            {
                InvocationFactory.Append(invocation, line.TrimEnd('\r'));
            }

            InvocationFactory.Finish(invocation, ExitCode);
            return Task.FromResult(invocation);
        }
    }

    [Fact]
    public async Task The_credentials_card_hands_over_the_names_of_the_required_credentials_that_are_not_set_and_only_those()
    {
        var services = Create();
        var harness = new CredentialHarness(services);
        Assert.Null(services.StatusFacts.MissingKeys);

        await harness.Credentials.RefreshAsync();

        Assert.Equal(new[] { "EXAMPLE_JUDGE_KEY", "EXAMPLE_TELEMETRY_KEY" }, services.StatusFacts.MissingKeys);
    }

    [Fact]
    public async Task A_read_that_finds_every_required_credential_set_hands_over_an_empty_list_which_is_not_the_same_as_never_having_read()
    {
        var services = Create();
        var harness = new CredentialHarness(services)
        {
            Output = """[{"env_name": "EXAMPLE_JUDGE_KEY", "canonical_env_name": "EXAMPLE_JUDGE_KEY", "feature": "LLM judge", "description": "d", "requirement": "required", "source": "env", "set": true}]""",
        };

        await harness.Credentials.RefreshAsync();

        Assert.NotNull(services.StatusFacts.MissingKeys);
        Assert.Empty(services.StatusFacts.MissingKeys!);
    }

    [Fact]
    public async Task A_failed_or_unreadable_read_hands_over_nothing_so_the_last_good_list_stands_and_nothing_reads_as_all_clear()
    {
        var services = Create();
        var harness = new CredentialHarness(services);
        await harness.Credentials.RefreshAsync();
        var raised = 0;
        services.StatusFacts.Changed += (_, _) => raised++;

        harness.Output = "Traceback: boom";
        harness.ExitCode = 1;
        await harness.Credentials.RefreshAsync();
        harness.Output = "this is not a credential list";
        harness.ExitCode = 0;
        await harness.Credentials.RefreshAsync();

        Assert.Equal(0, raised);
        Assert.Equal(new[] { "EXAMPLE_JUDGE_KEY", "EXAMPLE_TELEMETRY_KEY" }, services.StatusFacts.MissingKeys);
    }

    [Fact]
    public async Task Setting_a_credential_and_reading_again_takes_it_off_the_list_and_the_strip_follows()
    {
        var services = Create();
        using var strip = new StatusStripViewModel(services, new FakeFreshness(), new TickingClock(), action => action(), new CommandActivity(services.Cli, action => action()));
        var harness = new CredentialHarness(services);

        await harness.Credentials.RefreshAsync();
        Assert.Equal("Keys: missing EXAMPLE_JUDGE_KEY, EXAMPLE_TELEMETRY_KEY", strip.Chip(StripChipKey.Keys).Text);

        harness.Output = Listing.Replace("\"source\": \"unset\", \"set\": false}", "\"source\": \"dotenv\", \"set\": true}", StringComparison.Ordinal);
        await harness.Credentials.RefreshAsync();

        Assert.False(strip.Chip(StripChipKey.Keys).IsShown);
    }

    // ---- The Overview hands over its aggregate redaction label ----------------------------------------------------------------------------

    private static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static ObservabilityPlanRead Ok(string fixture) =>
        new(ObservabilityPlanStatus.Ok, ObservabilityPlanParser.Parse(Fixture(fixture)).Plan, string.Empty, DateTimeOffset.UtcNow);

    private static GatewaySnapshot Running() => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = JsonSerializer.Deserialize<GatewayHealth>("{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"telemetry\":{\"state\":\"running\",\"details\":{\"destinations\":[]}}}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
        PolledAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void The_overview_hands_over_its_label_as_it_words_it_loading_then_the_plans_aggregate_then_the_next_plans()
    {
        var services = Create($"gateway:\n  api_port: {OverviewScene.Port}\n");
        Assert.Null(services.StatusFacts.Redaction);

        // The panel is built on its first visit: from then on the Overview has a label, which is "loading" until a plan has been read.
        var vm = new OverviewPanelViewModel(services);
        Assert.Equal("per-route (loading)", services.StatusFacts.Redaction);

        vm.Apply(Running());
        Assert.Equal("per-route (loading)", services.StatusFacts.Redaction);

        vm.ApplyObservabilityPlan(Ok("runtime-95159fd/cli/observability-plan.json"));
        Assert.Equal("per-route · unredacted", services.StatusFacts.Redaction);
        Assert.Equal(vm.RedactionSummary, services.StatusFacts.Redaction);

        vm.ApplyObservabilityPlan(Ok("runtime-0.8.10/cli/observability-plan.destinations.synthetic.json"));
        Assert.Equal("per-route · none,sensitive,strict", services.StatusFacts.Redaction);
        Assert.Equal(vm.RedactionSummary, services.StatusFacts.Redaction);
    }

    [Fact]
    public void A_plan_that_could_not_be_read_hands_over_unavailable_and_the_strip_draws_it_neutral_not_green()
    {
        var services = Create($"gateway:\n  api_port: {OverviewScene.Port}\n");
        using var strip = new StatusStripViewModel(services, new FakeFreshness(), new TickingClock(), action => action(), new CommandActivity(services.Cli, action => action()));
        var vm = new OverviewPanelViewModel(services);

        vm.ApplyObservabilityPlan(new ObservabilityPlanRead(ObservabilityPlanStatus.Failed, null, "the command exited 1", DateTimeOffset.UtcNow));

        Assert.Equal("per-route (unavailable)", services.StatusFacts.Redaction);
        Assert.Equal("Redaction: per-route (unavailable)", strip.Chip(StripChipKey.Redaction).Text);
        Assert.Equal("Neutral", strip.Chip(StripChipKey.Redaction).Tone);
    }

    [Fact]
    public void The_strip_shows_the_overviews_label_and_warns_when_it_says_unredacted()
    {
        var services = Create($"gateway:\n  api_port: {OverviewScene.Port}\n");
        using var strip = new StatusStripViewModel(services, new FakeFreshness(), new TickingClock(), action => action(), new CommandActivity(services.Cli, action => action()));
        var vm = new OverviewPanelViewModel(services);

        vm.ApplyObservabilityPlan(Ok("runtime-95159fd/cli/observability-plan.json"));

        Assert.Equal("Redaction: per-route · unredacted", strip.Chip(StripChipKey.Redaction).Text);
        Assert.Equal("Warn", strip.Chip(StripChipKey.Redaction).Tone);
    }
}
