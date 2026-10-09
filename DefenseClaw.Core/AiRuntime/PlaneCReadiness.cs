using System.Globalization;

namespace DefenseClaw.Core.AiRuntime;

/// <summary>How much of the administrator's power the token carries (Win32 <c>TokenElevationType</c>).</summary>
public enum TokenElevation
{
    /// <summary>A filtered administrator token: the user is an administrator and this process runs without that power. The usual case.</summary>
    Limited,

    /// <summary>An elevated administrator token.</summary>
    Full,

    /// <summary>No split: a standard user, or a machine without UAC filtering.</summary>
    Standard,
}

/// <summary>One probe's answer, or the honest statement that it could not be had. A failed read is never a "no".</summary>
/// <typeparam name="T">What was read.</typeparam>
public sealed record ProbeReading<T>
{
    private ProbeReading(bool read, T? value, string problem)
    {
        Read = read;
        Value = value;
        Problem = problem;
    }

    /// <summary>True when the probe got an answer (the answer may itself be "absent").</summary>
    public bool Read { get; }

    public T? Value { get; }

    /// <summary>Why there is no answer; empty when <see cref="Read"/>.</summary>
    public string Problem { get; }

    public static ProbeReading<T> Ok(T? value) => new(true, value, string.Empty);

    public static ProbeReading<T> Failed(string problem) => new(false, default, string.IsNullOrWhiteSpace(problem) ? "no reason given" : problem.Trim());
}

/// <summary>
/// What the Plane C readiness card may look at on this PC. Four reads, all Win32 or .NET APIs, none of which starts a process, elevates, or
/// changes anything. There is deliberately no member that runs <c>auditpol</c> or <c>reg</c>: the audit policy needs an elevated reader and
/// is shown as unreadable, with a line to copy.
/// </summary>
public interface IPlaneCProbes
{
    /// <summary>The elevation type of THIS app's token.</summary>
    ProbeReading<TokenElevation> ReadElevation();

    /// <summary>Whether THIS app's token carries the Event Log Readers group (S-1-5-32-573).</summary>
    ProbeReading<bool> ReadEventLogReadersMembership();

    /// <summary>Whether an open-only query of the Security channel succeeds for THIS app's token (no event is read). False is "access denied".</summary>
    ProbeReading<bool> ReadSecurityChannelOpens();

    /// <summary>The <c>ProcessCreationIncludeCmdLine_Enabled</c> DWORD; a null value is "not set".</summary>
    ProbeReading<int?> ReadCommandLineAudit();
}

/// <summary>Whose token or process a check describes. The app's and the gateway's tokens differ, and mixing them up is the mistake this card prevents.</summary>
public enum PlaneCSubject
{
    /// <summary>This app's own process token, read here.</summary>
    App,

    /// <summary>The gateway, as its runtime snapshot reports it. The authority on plane C.</summary>
    Gateway,

    /// <summary>The machine's audit policy, which neither token tells.</summary>
    Machine,
}

/// <summary>The outcome of one check, in words that never turn "could not read" into "fine" or into "missing".</summary>
public enum PlaneCCheckState
{
    /// <summary>What plane C needs is in place.</summary>
    InPlace,

    /// <summary>Not in place, or limiting.</summary>
    Attention,

    /// <summary>Information only; nothing to change.</summary>
    Note,

    /// <summary>The probe failed or the snapshot does not say. Nothing is assumed either way.</summary>
    CouldNotRead,
}

/// <summary>One line of the card.</summary>
/// <param name="Id">Stable key (<c>elevation</c>, <c>event-log-readers</c>, <c>security-channel</c>, <c>cmdline-audit</c>, <c>audit-policy</c>, <c>plane-c</c>).</param>
/// <param name="Subject">Whose token this describes.</param>
/// <param name="Title">What was checked.</param>
/// <param name="State">The outcome.</param>
/// <param name="Value">The short answer.</param>
/// <param name="Detail">What it means for plane C.</param>
public sealed record PlaneCCheck(string Id, PlaneCSubject Subject, string Title, PlaneCCheckState State, string Value, string Detail)
{
    /// <summary>"This app's token", "The gateway (runtime snapshot)", "This PC's audit policy".</summary>
    public string SubjectText => Subject switch
    {
        PlaneCSubject.App => "This app's token",
        PlaneCSubject.Gateway => "The gateway (runtime snapshot)",
        _ => "This PC's audit policy",
    };

    public string StateText => State switch
    {
        PlaneCCheckState.InPlace => "in place",
        PlaneCCheckState.Attention => "not in place",
        PlaneCCheckState.Note => "note",
        _ => "could not read",
    };

    public string ToneKey => State switch
    {
        PlaneCCheckState.InPlace => "Ok",
        PlaneCCheckState.Attention => "Warn",
        PlaneCCheckState.CouldNotRead => "Warn",
        _ => "Neutral",
    };

    public string AutomationName => $"{SubjectText}: {Title}, {StateText}. {Value}. {Detail}";
}

/// <summary>
/// The Plane C readiness checks (CUST-324, from the CUST-316 spike). At the pinned runtime plane C is a poll of the Security event log (there is
/// no ETW session), so what matters is whether a token can read that channel and whether the machine writes the events at all. Pure: the probes
/// are handed in, and the snapshot's plane is data. Nothing here starts a process.
/// </summary>
public static class PlaneCReadiness
{
    public const string AppTokenNote =
        "The first three checks describe THIS app's token. The gateway runs under its own token, which can differ; its state is the plane C line, from its runtime snapshot.";

    /// <summary>The copy-only verify lines: the three subcategories the grant enables, the three the source also consumes, and the channel's ACL.</summary>
    public static IReadOnlyList<string> VerifyLines { get; } =
    [
        "auditpol /get /subcategory:\"Process Creation\"",
        "auditpol /get /subcategory:\"User Account Management\"",
        "auditpol /get /subcategory:\"Sensitive Privilege Use\"",
        "auditpol /get /subcategory:\"Process Termination\"",
        "auditpol /get /subcategory:\"Security Group Management\"",
        "auditpol /get /subcategory:\"Special Logon\"",
        "wevtutil gl Security",
    ];

    /// <summary>Undoes the grant. Clearly labelled on the card: it also clears a value IT may have set, because the runtime never records what was there.</summary>
    public static IReadOnlyList<string> RevertLines { get; } =
    [
        "auditpol /set /subcategory:\"Process Creation\" /success:disable /failure:disable",
        "auditpol /set /subcategory:\"User Account Management\" /success:disable /failure:disable",
        "auditpol /set /subcategory:\"Sensitive Privilege Use\" /success:disable /failure:disable",
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\\Audit\" /v ProcessCreationIncludeCmdLine_Enabled /t REG_DWORD /d 0 /f",
    ];

    /// <summary>The optional, untested route: membership in Event Log Readers lets a non-elevated token read the Security channel.</summary>
    public static IReadOnlyList<string> EventLogReadersLines { get; } =
    [
        "net localgroup \"Event Log Readers\" %USERNAME% /add",
    ];

    /// <summary>Runs the four probes and reads plane C out of the snapshot. A probe that throws is "could not read".</summary>
    public static IReadOnlyList<PlaneCCheck> Build(IPlaneCProbes probes, AiRuntimePlane? planeC)
    {
        ArgumentNullException.ThrowIfNull(probes);

        return
        [
            Elevation(Guard(probes.ReadElevation)),
            EventLogReaders(Guard(probes.ReadEventLogReadersMembership)),
            SecurityChannel(Guard(probes.ReadSecurityChannelOpens)),
            CommandLine(Guard(probes.ReadCommandLineAudit)),
            AuditPolicy(),
            Plane(planeC),
        ];
    }

    /// <summary>Reads every probe once and returns probes that answer with what was read, so the checks can be rebuilt against a newer snapshot without reading the machine again.</summary>
    public static IPlaneCProbes Capture(IPlaneCProbes probes)
    {
        ArgumentNullException.ThrowIfNull(probes);
        return new Captured(
            Guard(probes.ReadElevation),
            Guard(probes.ReadEventLogReadersMembership),
            Guard(probes.ReadSecurityChannelOpens),
            Guard(probes.ReadCommandLineAudit));
    }

    private sealed class Captured(
        ProbeReading<TokenElevation> elevation,
        ProbeReading<bool> readers,
        ProbeReading<bool> channel,
        ProbeReading<int?> commandLine) : IPlaneCProbes
    {
        public ProbeReading<TokenElevation> ReadElevation() => elevation;

        public ProbeReading<bool> ReadEventLogReadersMembership() => readers;

        public ProbeReading<bool> ReadSecurityChannelOpens() => channel;

        public ProbeReading<int?> ReadCommandLineAudit() => commandLine;
    }

    private static ProbeReading<T> Guard<T>(Func<ProbeReading<T>> probe)
    {
        try
        {
            return probe() ?? ProbeReading<T>.Failed("the probe returned nothing");
        }
#pragma warning disable CA1031 // A failed probe is a state of the check, never an unhandled exception.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ProbeReading<T>.Failed(ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    private static PlaneCCheck Unread(string id, PlaneCSubject subject, string title, string problem) =>
        new(id, subject, title, PlaneCCheckState.CouldNotRead, "could not read", problem + ". Nothing is assumed either way.");

    private static PlaneCCheck Elevation(ProbeReading<TokenElevation> reading)
    {
        const string title = "Token elevation";
        if (!reading.Read)
        {
            return Unread("elevation", PlaneCSubject.App, title, "The token could not be inspected (" + reading.Problem + ")");
        }

        return reading.Value switch
        {
            TokenElevation.Full => new("elevation", PlaneCSubject.App, title, PlaneCCheckState.InPlace, "elevated",
                "This app is running elevated. That is not needed by the app itself; it is the gateway's token that decides plane C."),
            TokenElevation.Limited => new("elevation", PlaneCSubject.App, title, PlaneCCheckState.Note, "limited (filtered administrator)",
                "The normal state for this app. It cannot read the audit policy, and it never elevates itself."),
            _ => new("elevation", PlaneCSubject.App, title, PlaneCCheckState.Note, "standard user",
                "No administrator token exists for this user, so elevation means another account's credentials, which is a different profile and data folder."),
        };
    }

    private static PlaneCCheck EventLogReaders(ProbeReading<bool> reading)
    {
        const string title = "Event Log Readers membership";
        if (!reading.Read)
        {
            return Unread("event-log-readers", PlaneCSubject.App, title, "Group membership could not be read (" + reading.Problem + ")");
        }

        return reading.Value
            ? new("event-log-readers", PlaneCSubject.App, title, PlaneCCheckState.InPlace, "member",
                "This token can read the Security channel without elevation. Any process running as this user can then read every process command line in it.")
            : new("event-log-readers", PlaneCSubject.App, title, PlaneCCheckState.Note, "not a member",
                "Without it, reading the Security channel needs an elevated token. Joining the group is an untested route (see the commands below).");
    }

    private static PlaneCCheck SecurityChannel(ProbeReading<bool> reading)
    {
        const string title = "Security event log opens";
        if (!reading.Read)
        {
            return Unread("security-channel", PlaneCSubject.App, title, "The channel could not be queried (" + reading.Problem + ")");
        }

        return reading.Value
            ? new("security-channel", PlaneCSubject.App, title, PlaneCCheckState.InPlace, "opens",
                "An open-only query of the Security channel succeeded for this app's token. No event was read.")
            : new("security-channel", PlaneCSubject.App, title, PlaneCCheckState.Attention, "access denied",
                "This app's token cannot open the Security channel. That says nothing about the gateway's token.");
    }

    private static PlaneCCheck CommandLine(ProbeReading<int?> reading)
    {
        const string title = "Command-line auditing (ProcessCreationIncludeCmdLine_Enabled)";
        if (!reading.Read)
        {
            return Unread("cmdline-audit", PlaneCSubject.App, title, "The registry value could not be read (" + reading.Problem + ")");
        }

        return reading.Value switch
        {
            1 => new("cmdline-audit", PlaneCSubject.App, title, PlaneCCheckState.InPlace, "1 (on)",
                "Process-creation events carry the command line. A machine-wide setting, readable without elevation."),
            { } other => new("cmdline-audit", PlaneCSubject.App, title, PlaneCCheckState.Attention, other.ToString(CultureInfo.InvariantCulture) + " (off)",
                "Plane C's lineage works, but argument-based detections do not."),
            _ => new("cmdline-audit", PlaneCSubject.App, title, PlaneCCheckState.Attention, "not set",
                "Plane C's lineage works, but argument-based detections do not."),
        };
    }

    private static PlaneCCheck AuditPolicy() =>
        new("audit-policy", PlaneCSubject.Machine, "Advanced Audit Policy", PlaneCCheckState.CouldNotRead, "unreadable without elevation",
            "Only an elevated prompt can read the audit policy, and this app never runs auditpol. Verify it yourself with the lines below; Group Policy may also overwrite a local setting.");

    private static PlaneCCheck Plane(AiRuntimePlane? plane)
    {
        const string title = "Plane C (agent actions)";
        if (plane is null)
        {
            return Unread("plane-c", PlaneCSubject.Gateway, title, "The runtime snapshot does not list plane C, or there is no snapshot");
        }

        var state = plane.State;
        var detail = state switch
        {
            AiRuntimePlaneState.Up => $"Running via {(plane.Mechanism.Length > 0 ? plane.Mechanism : "an unnamed mechanism")}.",
            AiRuntimePlaneState.Partial => "Running with a stated limit: " + plane.ReasonText + ". File events are expected to stay missing on Windows.",
            _ => plane.ReasonText,
        };
        return new("plane-c", PlaneCSubject.Gateway, title,
            state == AiRuntimePlaneState.Up ? PlaneCCheckState.InPlace : state == AiRuntimePlaneState.Off ? PlaneCCheckState.Note : PlaneCCheckState.Attention,
            plane.Badge, detail);
    }
}
