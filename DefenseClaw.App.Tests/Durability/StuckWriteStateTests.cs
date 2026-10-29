using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Durability;

/// <summary>CUST-360: the stuck-write attention row tells, per write, what its hash check found. Pure: no disk is read.</summary>
public sealed class StuckWriteStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

    private static CasPendingWrite Write(string target, CasWriteState? state) =>
        state is { } s
            ? new CasPendingWrite("068ff1aa22bb33cc44dd55ee", @"C:\dc", new[] { @"C:\dc\.defenseclaw-cas-v2-068ff1aa22bb33cc44dd55ee.intent" }, Array.Empty<string>(), target, "staging", Now.AddMinutes(-12))
                .WithAssessment(new CasWriteAssessment(s, "test"))
            : new CasPendingWrite("068ff1aa22bb33cc44dd55ee", @"C:\dc", new[] { @"C:\dc\.defenseclaw-cas-v2-068ff1aa22bb33cc44dd55ee.intent" }, Array.Empty<string>(), target, "staging", Now.AddMinutes(-12));

    private static string Detail(params CasPendingWrite[] writes)
    {
        var snapshot = new GatewaySnapshot
        {
            State = AppGatewayState.Running,
            Install = InstallState.Running,
            Detail = "ok",
            Durability = new DurabilityFacts(null, writes, Array.Empty<ConnectorFailMode>(), null),
            PolledAt = Now,
        };
        return DurabilityAttention.Rows(snapshot, _ => null, Now).Single(r => r.Title.Contains("stuck", StringComparison.Ordinal) || r.Title.Contains("never finished", StringComparison.Ordinal)).Detail;
    }

    [Theory]
    [InlineData(CasWriteState.Committed, "config.toml \u2014 finished; only leftovers remain")]
    [InlineData(CasWriteState.NeverApplied, "config.toml \u2014 never applied")]
    [InlineData(CasWriteState.Superseded, "config.toml \u2014 rewritten since (the pending write no longer describes it; safe to set aside)")]
    [InlineData(CasWriteState.Unknown, "config.toml \u2014 could not verify \u2014 check before clearing")]
    public void Detail_names_the_file_and_its_state(CasWriteState state, string expected)
    {
        Assert.Contains(expected, Detail(Write(@"C:\Users\tester\.codex\config.toml", state)), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_adds_a_caution_and_keeps_the_nothing_is_deleted_text()
    {
        var detail = Detail(Write(@"C:\Users\tester\.claude\settings.json", CasWriteState.Unknown));
        Assert.Contains("could not be verified", detail, StringComparison.Ordinal);
        Assert.Contains("nothing is deleted", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_states_add_no_caution()
    {
        Assert.DoesNotContain("could not be verified", Detail(Write(@"C:\Users\tester\.claude\settings.json", CasWriteState.Superseded)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unclassified_write_adds_no_state_sentence()
    {
        Assert.DoesNotContain("Checked against the receipts", Detail(Write(@"C:\Users\tester\.codex\config.toml", null)), StringComparison.Ordinal);
    }

    private static AttentionAction ClearButton(params CasPendingWrite[] writes)
    {
        var snapshot = new GatewaySnapshot
        {
            State = AppGatewayState.GatewayStopped,
            Install = InstallState.GatewayStopped,
            Detail = "stopped",
            Durability = new DurabilityFacts(null, writes, Array.Empty<ConnectorFailMode>(), null),
            PolledAt = Now,
        };
        return DurabilityAttention.Rows(snapshot, _ => null, Now).SelectMany(r => r.Actions.Items).Single(a => a.Id == AttentionActionIds.ClearStuckWrite);
    }

    [Theory]
    [InlineData(CasWriteState.Committed)]
    [InlineData(CasWriteState.NeverApplied)]
    [InlineData(CasWriteState.Superseded)]
    public void A_verified_write_keeps_the_plain_button(CasWriteState state)
    {
        var button = ClearButton(Write(@"C:\Users\tester\.claude\settings.json", state));

        Assert.True(button.Enabled);
        Assert.DoesNotContain("could not be verified", button.Tip, StringComparison.Ordinal);
        var warning = Assert.Single(OverviewPanelViewModel.ClearWarnings(new[] { Write(@"C:\Users\tester\.claude\settings.json", state) }));
        Assert.Equal("A file operation, not a DefenseClaw command", warning.Title);
    }

    [Fact]
    public void An_unverified_write_keeps_the_button_with_a_stronger_tip_and_the_review_warns_with_the_reason()
    {
        var write = new CasPendingWrite("068ff1aa22bb33cc44dd55ee", @"C:\dc", new[] { @"C:\dc\.defenseclaw-cas-v2-068ff1aa22bb33cc44dd55ee.intent" }, Array.Empty<string>(),
                @"C:\Users\tester\.claude\settings.json", "staging", Now.AddMinutes(-12))
            .WithAssessment(new CasWriteAssessment(CasWriteState.Unknown, "the file could not be read or is too large to check"));

        var button = ClearButton(write);
        Assert.True(button.Enabled);
        Assert.Contains("could not be verified", button.Tip, StringComparison.Ordinal);
        Assert.Contains("Nothing is deleted", button.Tip, StringComparison.Ordinal);

        var warnings = OverviewPanelViewModel.ClearWarnings(new[] { write });
        Assert.Equal(2, warnings.Count);
        var strong = warnings[1];
        Assert.Equal("Not every pending write could be verified", strong.Title);
        Assert.Contains("settings.json: the file could not be read or is too large to check", strong.Message, StringComparison.Ordinal);
        Assert.Contains("reversible with Put back", strong.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\tester", strong.Message, StringComparison.Ordinal);
    }
}
