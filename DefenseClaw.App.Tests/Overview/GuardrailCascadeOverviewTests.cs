using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.FirstRun;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The guardrail cascade on the Overview (runtime 1.0.10): the "Cascade" row of the Effective policy card (<c>default</c>, <c>set (see Policies)</c>,
/// <c>set, invalid</c>), and the attention rows for an invalid cascade or a reload refused because of it (<c>/health</c> <c>policy</c>). The owner's machine
/// (no cascade, a healthy policy block) shows no new row and no warning; an <c>opa:</c> fallback or another refusal shows nothing; a 1.0.9 runtime's card
/// has exactly the rows it had. Synthetic config and health only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GuardrailCascadeOverviewTests : IDisposable
{
    private const string Owner =
        "gateway:\n  api_port: 39872\nguardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n  judge:\n    enabled: false\n";

    private const string SetConfig = Owner + "  cascade:\n    unknown: block\n";

    private const string Resolved =
        "{\"profiles_configured\":true,\"user\":\"operator\",\"connector\":\"codex\",\"profile\":\"engineers\",\"match\":\"default\"," +
        "\"effective\":{\"mode\":\"action\",\"block_at\":\"HIGH\",\"alert_at\":\"LOW\",\"rule_pack_dir\":\"default\"},\"warnings\":[]}";

    private const string HealthyPolicy = "{\"effective_digest\":\"sha256:live\",\"generation\":12,\"config_generation\":12,\"component_count\":7}";

    private const string Weaker =
        "guardrail.cascade is invalid: unknown: sideways is not one of pack, alert, confirm, block; the guardrail decides with the rule pack defaults " +
        "(no guardrail.cascade settings) until the configuration is fixed. This is WEAKER than the configuration you wrote: unknown";

    private const string NotWeaker =
        "guardrail.cascade is invalid: judge.max_action: x; the guardrail decides with the last-known-good cascade until the configuration is fixed";

    private const string Refused =
        "config reload refused: guardrail.cascade is invalid: judge.max_action: x; the running configuration stays until it is fixed (defenseclaw guardrail cascade explain)";

    private readonly List<TempDirectory> _temps = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        foreach (var temp in _temps)
        {
            temp.Dispose();
        }
    }

    private AppServices Open(string version, string config)
    {
        var temp = new TempDirectory();
        _temps.Add(temp);
        var services = TestServices.Create(temp, config, FirstRunFailModeTests.Versioned(version));
        _services.Add(services);
        _ = services.Runtime.RefreshAsync().GetAwaiter().GetResult();
        return services;
    }

    private static GatewayHealth HealthWith(string? policy) =>
        JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}" + (policy is null ? string.Empty : ",\"policy\":" + policy) + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string PolicyJson(string? invalid = null, string? reload = null) =>
        "{\"effective_digest\":\"sha256:live\",\"generation\":12" +
        (invalid is null ? string.Empty : ",\"cascade_invalid\":" + JsonSerializer.Serialize(invalid)) +
        (reload is null ? string.Empty : ",\"last_reload_error\":" + JsonSerializer.Serialize(reload)) + "}";

    private static GatewaySnapshot Snapshot(GatewayHealth? health) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = health,
        ActiveConnectors = ["codex"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static bool IsCascadeRow(AttentionRow row) =>
        row.Title.StartsWith("Guardrail cascade", StringComparison.Ordinal) || row.Title.StartsWith("Config reload refused", StringComparison.Ordinal);

    private static Task<GatewayResult<JsonDocument>> Fetch(string path, int max, CancellationToken token) =>
        Task.FromResult(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(
            path.StartsWith("api/v1/guardrail/profiles/resolve", StringComparison.Ordinal) ? Resolved : "{\"state\":\"running\",\"policy\":" + HealthyPolicy + "}")));

    private static async Task<OverviewPanelViewModel> WithCard(AppServices services, GatewayHealth? health = null)
    {
        var vm = new OverviewPanelViewModel(services) { EffectivePolicyFetch = Fetch };
        if (health is not null)
        {
            vm.Apply(Snapshot(health));
        }

        await vm.RefreshEffectivePolicyAsync(force: true, CancellationToken.None);
        return vm;
    }

    // ---- the card's row --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_no_cascade_set_the_row_says_default()
    {
        var vm = await WithCard(Open("1.0.10", Owner));

        var row = Assert.Single(vm.EffectivePolicyRows, r => r.Label == "Cascade");
        Assert.Equal("default", row.Value);
        Assert.Equal("Neutral", row.ToneKey);
    }

    [Fact]
    public async Task With_a_cascade_set_the_row_points_at_Policies()
    {
        var vm = await WithCard(Open("1.0.10", SetConfig));

        Assert.Equal("set (see Policies)", Assert.Single(vm.EffectivePolicyRows, r => r.Label == "Cascade").Value);
    }

    [Fact]
    public async Task A_health_that_reports_an_invalid_cascade_makes_the_row_say_so()
    {
        var vm = await WithCard(Open("1.0.10", SetConfig), HealthWith(PolicyJson(invalid: NotWeaker)));

        var row = Assert.Single(vm.EffectivePolicyRows, r => r.Label == "Cascade");
        Assert.Equal("set, invalid", row.Value);
        Assert.Equal("Warn", row.ToneKey);
    }

    [Fact]
    public async Task A_1_0_9_runtime_shows_exactly_the_rows_it_showed_whatever_the_config_holds()
    {
        var plain = await WithCard(Open("1.0.9", Owner));
        var withCascade = await WithCard(Open("1.0.9", SetConfig));

        Assert.DoesNotContain(withCascade.EffectivePolicyRows, r => r.Label == "Cascade");
        Assert.Equal(plain.EffectivePolicyRows.Select(r => r.ToString()), withCascade.EffectivePolicyRows.Select(r => r.ToString()));
        Assert.Equal(plain.EffectivePolicyRows.Select(r => r.Alternate), withCascade.EffectivePolicyRows.Select(r => r.Alternate));
    }

    [Fact]
    public async Task The_cascade_row_is_added_after_the_runtimes_rows_with_the_next_stripe()
    {
        var vm = await WithCard(Open("1.0.10", Owner));

        var rows = vm.EffectivePolicyRows.ToArray();
        Assert.Equal("Cascade", rows[^1].Label);
        Assert.Equal(rows.Length % 2 == 0, rows[^1].Alternate);
    }

    // ---- the attention rows ----------------------------------------------------------------------------------------------------

    private OverviewPanelViewModel Panel(string version, string config)
    {
        return new OverviewPanelViewModel(Open(version, config));
    }

    [Fact]
    public void The_owners_machine_with_a_healthy_policy_block_has_no_cascade_row()
    {
        var vm = Panel("1.0.10", Owner);

        vm.Apply(Snapshot(HealthWith(HealthyPolicy)));

        Assert.DoesNotContain(vm.Attention, IsCascadeRow);
    }

    [Fact]
    public void A_runtime_with_no_policy_block_has_no_cascade_row()
    {
        var vm = Panel("1.0.9", Owner);

        vm.Apply(Snapshot(HealthWith(null)));

        Assert.DoesNotContain(vm.Attention, IsCascadeRow);
    }

    [Fact]
    public void An_invalid_cascade_with_a_weaker_fallback_is_a_high_row_in_the_gateways_words()
    {
        var vm = Panel("1.0.10", SetConfig);

        vm.Apply(Snapshot(HealthWith(PolicyJson(invalid: Weaker))));

        var row = Assert.Single(vm.Attention, IsCascadeRow);
        Assert.Equal("Guardrail cascade is invalid", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.StartsWith(Weaker, row.Detail, StringComparison.Ordinal);
        Assert.EndsWith("Check it with: defenseclaw guardrail cascade explain", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_cascade_that_is_not_weaker_is_a_medium_row()
    {
        var vm = Panel("1.0.10", SetConfig);

        vm.Apply(Snapshot(HealthWith(PolicyJson(invalid: NotWeaker))));

        var row = Assert.Single(vm.Attention, IsCascadeRow);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Contains("last-known-good", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reload_refused_because_of_the_cascade_is_a_medium_row_that_says_the_running_configuration_stays()
    {
        var vm = Panel("1.0.10", SetConfig);

        vm.Apply(Snapshot(HealthWith(PolicyJson(reload: Refused))));

        var row = Assert.Single(vm.Attention, IsCascadeRow);
        Assert.Equal("Config reload refused: guardrail.cascade is invalid", row.Title);
        Assert.Equal("Medium", row.SeverityKey);
        Assert.Contains("the running configuration stays until it is fixed", row.Detail, StringComparison.Ordinal);
        Assert.Contains("defenseclaw guardrail cascade explain", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_show_when_both_are_there_and_differ_and_one_when_they_are_the_same_text()
    {
        var vm = Panel("1.0.10", SetConfig);

        vm.Apply(Snapshot(HealthWith(PolicyJson(invalid: NotWeaker, reload: Refused))));
        Assert.Equal(2, vm.Attention.Count(IsCascadeRow));

        vm.Apply(Snapshot(HealthWith(PolicyJson(invalid: Refused, reload: Refused))));
        Assert.Single(vm.Attention, IsCascadeRow);
    }

    [Theory]
    [InlineData("opa: opa binary not found")]
    [InlineData("config reload refused: gateway.api_port is invalid")]
    [InlineData("scanner pin mismatch")]
    public void Other_reload_errors_and_the_opa_fallback_add_no_row(string error)
    {
        var vm = Panel("1.0.10", Owner);

        vm.Apply(Snapshot(HealthWith(PolicyJson(reload: error))));

        Assert.DoesNotContain(vm.Attention, IsCascadeRow);
    }

    [Fact]
    public void The_row_goes_when_the_gateway_stops_reporting_it()
    {
        var vm = Panel("1.0.10", SetConfig);

        vm.Apply(Snapshot(HealthWith(PolicyJson(invalid: Weaker))));
        Assert.Single(vm.Attention, IsCascadeRow);

        vm.Apply(Snapshot(HealthWith(HealthyPolicy)));
        Assert.DoesNotContain(vm.Attention, IsCascadeRow);
    }
}
