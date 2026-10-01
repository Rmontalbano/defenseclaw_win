using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// "Silent bypass: N allowed LLM-shaped egress in the last 5 min" (CUST-218; the Mac's <c>silentBypassCount</c> and its overview notice):
/// a row of "What needs attention" while the count is above zero, none while it is zero, and a database that cannot answer neither
/// invents a quiet number nor faults the panel.
/// </summary>
public sealed class SilentBypassAttentionTests : IDisposable
{
    private const string GuardrailOnConfig = "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static GatewaySnapshot Snapshot() => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        PolledAt = DateTimeOffset.UtcNow,
    };

    private (OverviewPanelViewModel Panel, AlertQueueDatabase Database) Panel()
    {
        _services = TestServices.Create(_temp, GuardrailOnConfig);
        return (new OverviewPanelViewModel(_services), new AlertQueueDatabase(_services.Paths.AuditDatabasePath));
    }

    [Fact]
    public async Task Allowed_llm_shaped_egress_in_the_last_five_minutes_is_a_row_and_the_title_carries_the_count()
    {
        var (vm, database) = Panel();
        var now = DateTimeOffset.UtcNow;
        database.AddEgress("a", now.AddSeconds(-30), "allow", "passthrough", looksLikeLlm: true);
        database.AddEgress("b", now.AddSeconds(-90), "allowed", "shape", looksLikeLlm: false);
        database.AddEgress("quiet", now.AddSeconds(-10), "allow", "passthrough", looksLikeLlm: false);
        database.AddEgress("blocked", now.AddSeconds(-10), "block", "passthrough", looksLikeLlm: true);
        database.AddEgress("old", now.AddMinutes(-20), "allow", "passthrough", looksLikeLlm: true);
        vm.Apply(Snapshot());
        Assert.DoesNotContain(vm.Attention, r => r.Title.StartsWith("Silent bypass", StringComparison.Ordinal));

        await vm.RefreshSilentBypassAsync(CancellationToken.None);

        Assert.Equal(2, vm.SilentBypassCount);
        var row = Assert.Single(vm.Attention, r => r.Title.StartsWith("Silent bypass", StringComparison.Ordinal));
        Assert.Equal("Silent bypass: 2 allowed LLM-shaped egress in the last 5 min", row.Title);
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("Egress", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_nothing_to_count_there_is_no_row_and_it_goes_away_when_the_count_does()
    {
        var (vm, database) = Panel();
        vm.Apply(Snapshot());

        await vm.RefreshSilentBypassAsync(CancellationToken.None);
        Assert.DoesNotContain(vm.Attention, r => r.Title.StartsWith("Silent bypass", StringComparison.Ordinal));

        database.AddEgress("a", DateTimeOffset.UtcNow.AddSeconds(-5), "allow", "passthrough", looksLikeLlm: true);
        await vm.RefreshSilentBypassAsync(CancellationToken.None);
        Assert.Contains(vm.Attention, r => r.Title.StartsWith("Silent bypass", StringComparison.Ordinal));

        // The window moves on: the same event six minutes later is out of it.
        var reader = new DefenseClaw.Core.Audit.NetworkEgressReader(_services!.Paths.AuditDatabasePath);
        Assert.Equal(0, await reader.CountSilentBypassAsync(DateTimeOffset.UtcNow.AddMinutes(6)));
    }

    [Fact]
    public async Task A_database_that_cannot_be_read_keeps_the_last_number_and_throws_nothing()
    {
        var (vm, database) = Panel();
        database.AddEgress("a", DateTimeOffset.UtcNow.AddSeconds(-5), "allow", "passthrough", looksLikeLlm: true);
        vm.Apply(Snapshot());
        await vm.RefreshSilentBypassAsync(CancellationToken.None);
        Assert.Equal(1, vm.SilentBypassCount);

        vm.EgressReader = new DefenseClaw.Core.Audit.NetworkEgressReader(_temp.WriteFile("not-a-database.db", "plain text, not a SQLite file, long enough to be read as a header. "));
        await vm.RefreshSilentBypassAsync(CancellationToken.None);

        Assert.Equal(1, vm.SilentBypassCount);
    }
}
