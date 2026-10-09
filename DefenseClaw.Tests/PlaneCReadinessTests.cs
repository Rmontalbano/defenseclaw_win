using DefenseClaw.Core.AiRuntime;

namespace DefenseClaw.Tests;

/// <summary>
/// The Plane C readiness checks (CUST-324): each one says whose token it describes, and each has a state for "could not read" that is never
/// shown as a pass or as a fault. The probes are fakes; nothing here touches the machine.
/// </summary>
public sealed class PlaneCReadinessTests
{
    private sealed class Probes : IPlaneCProbes
    {
        public Func<ProbeReading<TokenElevation>> Elevation { get; set; } = () => ProbeReading<TokenElevation>.Ok(TokenElevation.Limited);

        public Func<ProbeReading<bool>> Readers { get; set; } = () => ProbeReading<bool>.Ok(false);

        public Func<ProbeReading<bool>> Channel { get; set; } = () => ProbeReading<bool>.Ok(false);

        public Func<ProbeReading<int?>> CommandLine { get; set; } = () => ProbeReading<int?>.Ok(1);

        public int Calls { get; private set; }

        public ProbeReading<TokenElevation> ReadElevation()
        {
            Calls++;
            return Elevation();
        }

        public ProbeReading<bool> ReadEventLogReadersMembership()
        {
            Calls++;
            return Readers();
        }

        public ProbeReading<bool> ReadSecurityChannelOpens()
        {
            Calls++;
            return Channel();
        }

        public ProbeReading<int?> ReadCommandLineAudit()
        {
            Calls++;
            return CommandLine();
        }
    }

    private static AiRuntimePlane PlaneC(bool available, bool running, string reason) =>
        new("c", "agent actions", available, running, "Security event log", reason);

    private static PlaneCCheck Find(IReadOnlyList<PlaneCCheck> checks, string id) => checks.Single(c => c.Id == id);

    [Fact]
    public void Six_checks_in_a_fixed_order_each_labelled_with_whose_token_it_describes()
    {
        var checks = PlaneCReadiness.Build(new Probes(), PlaneC(false, false, "needs an elevated token"));

        Assert.Equal(new[] { "elevation", "event-log-readers", "security-channel", "cmdline-audit", "audit-policy", "plane-c" }, checks.Select(c => c.Id).ToArray());
        Assert.Equal(
            new[] { PlaneCSubject.App, PlaneCSubject.App, PlaneCSubject.App, PlaneCSubject.App, PlaneCSubject.Machine, PlaneCSubject.Gateway },
            checks.Select(c => c.Subject).ToArray());
        Assert.Equal("This app's token", checks[0].SubjectText);
        Assert.Equal("The gateway (runtime snapshot)", checks[5].SubjectText);
        Assert.All(checks, c => Assert.Contains(c.SubjectText, c.AutomationName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TokenElevation.Limited, PlaneCCheckState.Note, "limited")]
    [InlineData(TokenElevation.Full, PlaneCCheckState.InPlace, "elevated")]
    [InlineData(TokenElevation.Standard, PlaneCCheckState.Note, "standard user")]
    public void Elevation_type_is_read_from_the_apps_token(TokenElevation type, PlaneCCheckState state, string value)
    {
        var checks = PlaneCReadiness.Build(new Probes { Elevation = () => ProbeReading<TokenElevation>.Ok(type) }, null);

        var check = Find(checks, "elevation");
        Assert.Equal(state, check.State);
        Assert.Contains(value, check.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, PlaneCCheckState.InPlace)]
    [InlineData(false, PlaneCCheckState.Note)]
    public void Event_log_readers_membership_is_a_fact_not_a_failure(bool member, PlaneCCheckState state)
    {
        var check = Find(PlaneCReadiness.Build(new Probes { Readers = () => ProbeReading<bool>.Ok(member) }, null), "event-log-readers");

        Assert.Equal(state, check.State);
    }

    [Theory]
    [InlineData(true, PlaneCCheckState.InPlace, "opens")]
    [InlineData(false, PlaneCCheckState.Attention, "access denied")]
    public void The_security_channel_either_opens_or_is_denied_for_the_apps_token(bool opens, PlaneCCheckState state, string value)
    {
        var check = Find(PlaneCReadiness.Build(new Probes { Channel = () => ProbeReading<bool>.Ok(opens) }, null), "security-channel");

        Assert.Equal(state, check.State);
        Assert.Equal(value, check.Value);
        Assert.Contains("app's token", check.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, PlaneCCheckState.InPlace, "1 (on)")]
    [InlineData(0, PlaneCCheckState.Attention, "0 (off)")]
    [InlineData(null, PlaneCCheckState.Attention, "not set")]
    public void The_command_line_audit_value_is_on_off_or_absent(int? value, PlaneCCheckState state, string text)
    {
        var check = Find(PlaneCReadiness.Build(new Probes { CommandLine = () => ProbeReading<int?>.Ok(value) }, null), "cmdline-audit");

        Assert.Equal(state, check.State);
        Assert.Equal(text, check.Value);
    }

    [Fact]
    public void Every_probe_has_a_could_not_read_state_for_a_failed_read_and_for_a_throwing_probe()
    {
        var failing = new Probes
        {
            Elevation = () => ProbeReading<TokenElevation>.Failed("no token"),
            Readers = () => ProbeReading<bool>.Failed("lookup failed"),
            Channel = () => ProbeReading<bool>.Failed("no such channel"),
            CommandLine = () => ProbeReading<int?>.Failed("not a DWORD"),
        };
        var throwing = new Probes
        {
            Elevation = () => throw new InvalidOperationException("boom"),
            Readers = () => throw new UnauthorizedAccessException(),
            Channel = () => throw new IOException(),
            CommandLine = () => throw new InvalidOperationException(),
        };

        foreach (var probes in new[] { failing, throwing })
        {
            var checks = PlaneCReadiness.Build(probes, null);
            foreach (var id in new[] { "elevation", "event-log-readers", "security-channel", "cmdline-audit" })
            {
                var check = Find(checks, id);
                Assert.Equal(PlaneCCheckState.CouldNotRead, check.State);
                Assert.Equal("could not read", check.StateText);
                Assert.Contains("Nothing is assumed", check.Detail, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_audit_policy_is_always_unreadable_unelevated_and_no_probe_exists_to_read_it()
    {
        var checks = PlaneCReadiness.Build(new Probes(), null);

        var audit = Find(checks, "audit-policy");
        Assert.Equal(PlaneCCheckState.CouldNotRead, audit.State);
        Assert.Contains("unreadable without elevation", audit.Value, StringComparison.Ordinal);
        Assert.Contains("never runs auditpol", audit.Detail, StringComparison.Ordinal);

        // The probe interface is the whole of what the card can look at: four reads, none of them the audit policy.
        Assert.Equal(
            new[] { "ReadCommandLineAudit", "ReadElevation", "ReadEventLogReadersMembership", "ReadSecurityChannelOpens" },
            typeof(IPlaneCProbes).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray());

        Assert.All(PlaneCReadiness.VerifyLines, l => Assert.True(l.StartsWith("auditpol /get ", StringComparison.Ordinal) || l == "wevtutil gl Security"));
    }

    [Theory]
    [InlineData(true, true, "", PlaneCCheckState.InPlace, "up")]
    [InlineData(true, true, "the event source is missing file events", PlaneCCheckState.Attention, "partial")]
    [InlineData(false, false, "needs an elevated token", PlaneCCheckState.Attention, "blind")]
    [InlineData(true, false, "a source stopped", PlaneCCheckState.Attention, "idle")]
    [InlineData(true, false, "not selected in ai_discovery.runtime.planes", PlaneCCheckState.Note, "off")]
    public void Plane_c_is_the_gateways_state_from_the_snapshot_with_its_reason(bool available, bool running, string reason, PlaneCCheckState state, string badge)
    {
        var check = Find(PlaneCReadiness.Build(new Probes(), PlaneC(available, running, reason)), "plane-c");

        Assert.Equal(PlaneCSubject.Gateway, check.Subject);
        Assert.Equal(state, check.State);
        Assert.Equal(badge, check.Value);
        if (reason.Length > 0)
        {
            Assert.Contains(reason, check.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Without_a_plane_c_in_the_snapshot_the_gateways_line_says_it_could_not_read()
    {
        var check = Find(PlaneCReadiness.Build(new Probes(), null), "plane-c");

        Assert.Equal(PlaneCCheckState.CouldNotRead, check.State);
    }

    [Fact]
    public void Capture_reads_each_probe_once_and_replays_the_answers()
    {
        var probes = new Probes { Channel = () => ProbeReading<bool>.Ok(true) };

        var captured = PlaneCReadiness.Capture(probes);
        Assert.Equal(4, probes.Calls);

        _ = PlaneCReadiness.Build(captured, null);
        var again = PlaneCReadiness.Build(captured, PlaneC(true, true, string.Empty));

        Assert.Equal(4, probes.Calls);
        Assert.Equal("opens", Find(again, "security-channel").Value);
    }

    [Fact]
    public void The_copy_only_blocks_hold_the_grant_revert_and_group_lines_and_nothing_that_runs()
    {
        Assert.Equal(4, PlaneCReadiness.RevertLines.Count);
        Assert.Contains(PlaneCReadiness.RevertLines, l => l.Contains("/d 0 /f", StringComparison.Ordinal));
        Assert.Contains(PlaneCReadiness.EventLogReadersLines, l => l.Contains("Event Log Readers", StringComparison.Ordinal));
        Assert.Contains(PlaneCReadiness.VerifyLines, l => l.Contains("Special Logon", StringComparison.Ordinal));
        Assert.DoesNotContain(PlaneCReadiness.VerifyLines, l => l.Contains("/set", StringComparison.Ordinal));
    }
}
