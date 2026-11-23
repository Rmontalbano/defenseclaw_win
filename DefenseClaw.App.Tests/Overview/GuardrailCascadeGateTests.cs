using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.FirstRun;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The cascade attention rows are drawn only on a runtime with the cascade (the 1.0.10 gate): a 1.0.9 runtime can carry <c>policy.last_reload_error</c> too, but it has
/// no <c>guardrail cascade explain</c> to point at, so it gets no row (and never had one). A long invalid-cascade text keeps its High severity. Synthetic only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GuardrailCascadeGateTests : IDisposable
{
    private const string Config =
        "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n  cascade:\n    unknown: block\n";

    private const string Refused =
        "config reload refused: guardrail.cascade is invalid: judge.max_action: x; the running configuration stays until it is fixed (defenseclaw guardrail cascade explain)";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string version)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, Config, FirstRunFailModeTests.Versioned(version));
        _ = _services.Runtime.RefreshAsync().GetAwaiter().GetResult();
        return new OverviewPanelViewModel(_services);
    }

    private static GatewaySnapshot Snapshot(string policy) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = JsonSerializer.Deserialize<GatewayHealth>(
            "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"},\"policy\":" + policy + "}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
        ActiveConnectors = ["codex"],
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static bool IsCascadeRow(AttentionRow row) =>
        row.Title.StartsWith("Guardrail cascade", StringComparison.Ordinal) || row.Title.StartsWith("Config reload refused", StringComparison.Ordinal);

    [Fact]
    public void A_1_0_9_runtime_with_a_refused_reload_or_an_invalid_cascade_gets_no_cascade_row()
    {
        var vm = Panel("1.0.9");

        vm.Apply(Snapshot("{\"generation\":3,\"cascade_invalid\":\"guardrail.cascade is invalid: x; WEAKER\",\"last_reload_error\":" + JsonSerializer.Serialize(Refused) + "}"));
        var withErrors = vm.Attention.Select(static r => r.Title).ToList();
        vm.Apply(Snapshot("{\"generation\":3}"));

        Assert.DoesNotContain(vm.Attention, IsCascadeRow);
        Assert.Equal(vm.Attention.Select(static r => r.Title), withErrors);
    }

    [Fact]
    public void The_same_health_on_1_0_10_draws_both_rows()
    {
        var vm = Panel("1.0.10");

        vm.Apply(Snapshot("{\"generation\":3,\"cascade_invalid\":\"guardrail.cascade is invalid: x; the guardrail decides with the last-known-good cascade until the configuration is fixed\",\"last_reload_error\":" + JsonSerializer.Serialize(Refused) + "}"));

        Assert.Equal(2, vm.Attention.Count(IsCascadeRow));
    }

    [Fact]
    public void A_long_invalid_cascade_text_is_high_and_says_weaker_even_though_the_gateways_last_sentence_was_cut()
    {
        var vm = Panel("1.0.10");
        var text = "guardrail.cascade is invalid: " + new string('x', 700) + "; the guardrail decides with the rule pack defaults until the configuration is fixed. This is WEAKER than the configuration you wrote: unknown";

        vm.Apply(Snapshot("{\"generation\":3,\"cascade_invalid\":" + JsonSerializer.Serialize(text) + "}"));

        var row = Assert.Single(vm.Attention, IsCascadeRow);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("This is WEAKER than the configuration you wrote.", row.Detail, StringComparison.Ordinal);
    }
}
