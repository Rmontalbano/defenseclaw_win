using System.Globalization;

namespace DefenseClaw.Core.AiRuntime;

/// <summary>
/// How one runtime plane is doing, in the words an operator acts on. The gateway reports two booleans and a reason; this is what
/// they mean together. Every state but <see cref="Up"/> is "this plane is not delivering everything it could", so none of them
/// may be drawn as a calm result.
/// </summary>
public enum AiRuntimePlaneState
{
    /// <summary>Running, with nothing to add: the plane delivers all it can on this host.</summary>
    Up,

    /// <summary>
    /// Running, and the gateway states a limit (Plane B without an elevated token sees only this process's own sockets; Plane C with
    /// process events and no file events). Partial coverage: the half that works is reported, the missing half is named.
    /// </summary>
    Partial,

    /// <summary>The platform can run it and it is not running (a source stopped, a table unreadable, a poll not made yet).</summary>
    Idle,

    /// <summary>The plane cannot be observed on this host at all; its reason says what to change.</summary>
    Blind,

    /// <summary>
    /// Not selected in the configuration (<c>ai_discovery.runtime.planes</c>, or Plane C without <c>enable_host_plane</c>). The one
    /// expected gap, and the gateway itself leaves it out of its degraded reasons; it is still a plane nobody is watching through.
    /// </summary>
    Off,
}

/// <summary>
/// One detection plane, as the gateway reports it on every poll. Emitted whether the plane is up or down: a plane that died would
/// otherwise leave no trace, and absence is the hardest thing to notice.
/// </summary>
/// <param name="Id">The plane letter: <c>a</c> (inference heartbeat), <c>b</c> (shadow egress), <c>c</c> (agent actions).</param>
/// <param name="Name">The plane's name as the gateway words it.</param>
/// <param name="Available">The platform can observe it.</param>
/// <param name="Running">It is observing now.</param>
/// <param name="Mechanism">How, when it is running (<c>Toolhelp32 snapshot, GetProcessTimes, GetProcessMemoryInfo</c>).</param>
/// <param name="Reason">Why not, or what is missing; display-redacted.</param>
public sealed record AiRuntimePlane(string Id, string Name, bool Available, bool Running, string Mechanism, string Reason)
{
    /// <summary>The state, from the two flags and the reason. See <see cref="AiRuntimePlaneState"/>.</summary>
    public AiRuntimePlaneState State => Classify(Available, Running, Reason);

    /// <summary>True only for <see cref="AiRuntimePlaneState.Up"/>.</summary>
    public bool IsFullyUp => State == AiRuntimePlaneState.Up;

    /// <summary>The short word on the plane's chip: <c>up</c>, <c>partial</c>, <c>idle</c>, <c>blind</c> or <c>off</c>.</summary>
    public string Badge => State switch
    {
        AiRuntimePlaneState.Up => "up",
        AiRuntimePlaneState.Partial => "partial",
        AiRuntimePlaneState.Idle => "idle",
        AiRuntimePlaneState.Off => "off",
        _ => "blind",
    };

    /// <summary>"A · inference heartbeat": the letter and the name, so the three chips read apart.</summary>
    public string DisplayName => Id.Length == 1 && Name.Length > 0
        ? $"{Id.ToUpperInvariant()} · {Name}"
        : Name.Length > 0 ? Name : Id;

    /// <summary>
    /// The reason, or the sentence that says there was none: a plane that is not delivering and does not say why is the thing this
    /// panel exists to make visible.
    /// </summary>
    public string ReasonText => Reason.Length > 0 ? Reason : "no reason reported";

    /// <summary>One line an operator can act on; every state that is not "up" carries its full reason.</summary>
    public string Summary => State switch
    {
        AiRuntimePlaneState.Up => $"{DisplayName}: up via {(Mechanism.Length > 0 ? Mechanism : "an unnamed mechanism")}",
        AiRuntimePlaneState.Partial => $"{DisplayName}: partial coverage{(Mechanism.Length > 0 ? " via " + Mechanism : string.Empty)}. {ReasonText}",
        AiRuntimePlaneState.Idle => $"{DisplayName}: available but not running. {ReasonText}",
        AiRuntimePlaneState.Off => $"{DisplayName}: off. {ReasonText}",
        _ => $"{DisplayName}: unavailable. {ReasonText}",
    };

    /// <summary>
    /// The gateway's own test for a plane that was left out on purpose, word for word
    /// (<c>planeIsDeselected</c> in <c>internal/sensor/service.go</c>): a reason that says it was not selected, or names the
    /// <c>enable_host_plane</c> opt-in. A plane that is running is never "off".
    /// </summary>
    public static bool IsDeselected(bool running, string reason) =>
        !running &&
        (reason.Contains("not selected", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("enable_host_plane", StringComparison.OrdinalIgnoreCase));

    /// <summary>The state for the three fields the gateway sends.</summary>
    public static AiRuntimePlaneState Classify(bool available, bool running, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (IsDeselected(running, reason))
        {
            return AiRuntimePlaneState.Off;
        }

        if (running)
        {
            // A reason on a running plane is a stated limit (GAP-1377 in the CLI): partial coverage, not "up".
            return reason.Length > 0 ? AiRuntimePlaneState.Partial : AiRuntimePlaneState.Up;
        }

        return available ? AiRuntimePlaneState.Idle : AiRuntimePlaneState.Blind;
    }
}

/// <summary>One weighted signal that contributed to a finding's score.</summary>
/// <param name="Id">The signal id (<c>agent_kill_chain</c>, <c>inference_heartbeat</c>, ...).</param>
/// <param name="Title">What it means, in a phrase.</param>
/// <param name="Detail">The evidence (an executable, a sequence); display-redacted.</param>
/// <param name="Weight">What it added to the score.</param>
public sealed record AiRuntimeSignal(string Id, string Title, string Detail, int Weight)
{
    /// <summary>Id and detail: two signals with one id and different evidence are two signals.</summary>
    public string Identity => Id + "|" + Detail;
}

/// <summary>One attributed egress peer of a finding.</summary>
/// <param name="Hostname">The peer's name; empty when it was attributed by address alone.</param>
/// <param name="Address">The peer's address.</param>
/// <param name="Port">The peer's port.</param>
/// <param name="Category">The provider's category (<c>sanctioned</c>, a provider class); empty when uncategorised.</param>
/// <param name="Confidence">How directly the peer was named: 0.95 a captured DNS answer, 0.75 an address-index hit, 0.6 reverse DNS.</param>
/// <param name="AttributionSource">Which of those it was.</param>
public sealed record AiRuntimeProvider(string Hostname, string Address, int Port, string Category, double Confidence, string AttributionSource)
{
    /// <summary>
    /// Hostname, address and port. The port is part of the identity, not decoration: a local model server on two ports, or one host
    /// reached over 443 and a proxy port, are two peers with one name.
    /// </summary>
    public string Identity => $"{Hostname}|{Address}|{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The name to show: the hostname, else the address, with the port when there is one.</summary>
    public string Display
    {
        get
        {
            var host = Hostname.Length > 0 ? Hostname : Address;
            return Port > 0 && host.Length > 0 ? $"{host}:{Port.ToString(CultureInfo.InvariantCulture)}" : host;
        }
    }

    /// <summary>"uncategorised, reverse DNS (60%)": what the inspector prints after the name.</summary>
    public string Detail
    {
        get
        {
            var source = AttributionSource.Length > 0 ? AttributionSource : "unknown source";
            var percent = Confidence > 0 ? $" ({Math.Round(Confidence * 100).ToString("0", CultureInfo.InvariantCulture)}%)" : string.Empty;
            return $"{(Category.Length > 0 ? Category : "uncategorised")}, {source}{percent}";
        }
    }
}

/// <summary>What the discovery inventory had to say about one runtime finding. Always present, including when it had nothing to say.</summary>
/// <param name="Verdict"><c>accounted</c>, <c>unaccounted</c> or <c>unobserved</c> (empty when the gateway sent none).</param>
/// <param name="Reason">Why; display-redacted.</param>
/// <param name="MatchedSignalIds">The inventory signals that accounted for it.</param>
/// <param name="Categories">The categories those came from.</param>
public sealed record AiRuntimeCorrelation(string Verdict, string Reason, IReadOnlyList<string> MatchedSignalIds, IReadOnlyList<string> Categories)
{
    /// <summary>A fresh, complete inventory found nothing that explains this: the more interesting reading, not the neutral one.</summary>
    public bool IsUnaccounted => Verdict.Equals("unaccounted", StringComparison.OrdinalIgnoreCase);

    /// <summary>There was no usable inventory. Never spent as evidence or as an all-clear.</summary>
    public bool IsUnobserved => Verdict.Equals("unobserved", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verdict, or "unknown" when the gateway sent none; never blank, so "unobserved" and "missing" cannot read as agreement.</summary>
    public string VerdictText => Verdict.Length > 0 ? Verdict : "unknown";
}

/// <summary>One scored runtime finding.</summary>
public sealed record AiRuntimeFinding(
    string FindingId,
    int Pid,
    string Process,
    string Cmdline,
    string User,
    string AgentName,
    int Score,
    string Severity,
    IReadOnlyList<AiRuntimeSignal> Signals,
    IReadOnlyList<AiRuntimeProvider> Providers,
    AiRuntimeCorrelation Correlation,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen)
{
    /// <summary>The id rows are matched by across refreshes: the gateway's, else the pid and process.</summary>
    public string Id => FindingId.Length > 0 ? FindingId : $"{Pid.ToString(CultureInfo.InvariantCulture)}-{Process}";

    /// <summary>The severity in the gateway's own lower-case vocabulary (<c>critical</c> ... <c>info</c>); anything else as sent.</summary>
    public string SeverityKey => Severity.ToLowerInvariant();

    /// <summary>0 for critical down to 4 for info, and for a band this app does not know: unknown ranks last, never promoted.</summary>
    public int SeverityRank => SeverityKey switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4,
    };

    /// <summary>"Critical", "High" ...: the word a cell shows.</summary>
    public string SeverityLabel => Severity.Length == 0
        ? "Info"
        : char.ToUpperInvariant(Severity[0]) + Severity[1..].ToLowerInvariant();

    /// <summary>
    /// The observed sequence when this finding is a chain, as the gateway wrote it. Rendered as a sequence rather than a set because
    /// the order is the finding: reading a credential is a lead; reading one, minting an identity and uploading is an incident.
    /// </summary>
    public string Chain => Signals.FirstOrDefault(s => s.Id == "agent_kill_chain")?.Detail ?? string.Empty;

    /// <summary>The providers' names, comma separated, or a dash.</summary>
    public string ProviderSummary => Providers.Count == 0 ? "—" : string.Join(", ", Providers.Select(p => p.Display));

    /// <summary>
    /// The filter box's test: every word of <paramref name="filter"/> must be somewhere in the process, command line, user, agent,
    /// severity, providers or inventory verdict (case-insensitive). An empty filter matches everything.
    /// </summary>
    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var haystack = string.Join(
            ' ',
            Process,
            Cmdline,
            User,
            AgentName,
            Severity,
            string.Join(' ', Providers.Select(p => p.Hostname + " " + p.Address)),
            Correlation.Verdict);

        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The runtime-plane snapshot: what the gateway last polled. Coverage travels with the findings rather than in a separate call,
/// because a reader who sees only the finding count cannot tell a quiet host from a blind sensor.
/// </summary>
public sealed record AiRuntimeSnapshot(
    bool Enabled,
    bool Polled,
    DateTimeOffset? ScannedAt,
    IReadOnlyList<AiRuntimePlane> Planes,
    IReadOnlyList<AiRuntimeFinding> Findings,
    int FindingsNotShown,
    int UnreadableEntries,
    int ProcessesObserved,
    int ProcessesSkipped,
    int ConnectionsObserved,
    int ConnectionsUnattributed,
    long HostPlaneObservations,
    long HostPlaneGated,
    bool Degraded,
    IReadOnlyList<string> DegradedReasons)
{
    /// <summary>The share of connections at which the unattributed warning appears (the macOS companion's and the CLI's 50 percent).</summary>
    public const double UnattributedWarningShare = 0.5;

    /// <summary>The planes the gateway always reports, in order.</summary>
    public static IReadOnlyList<string> KnownPlaneIds { get; } = ["a", "b", "c"];

    /// <summary>Share of observed connections with no attributable owner, 0 to 1; 0 when none were observed.</summary>
    public double UnattributedShare => ConnectionsObserved > 0
        ? Math.Clamp((double)ConnectionsUnattributed / ConnectionsObserved, 0, 1)
        : 0;

    /// <summary>True when half or more of the connections could not be attributed to a process: egress is then mostly unnamed.</summary>
    public bool HasUnattributedWarning => ConnectionsObserved > 0 && UnattributedShare >= UnattributedWarningShare;

    /// <summary>The warning's sentence, with the percentage; empty when <see cref="HasUnattributedWarning"/> is false.</summary>
    public string UnattributedWarning => HasUnattributedWarning
        ? $"{Math.Floor(UnattributedShare * 100).ToString("0", CultureInfo.InvariantCulture)}% of connections could not be attributed to a process. " +
          "Run the gateway elevated for machine-wide egress attribution."
        : string.Empty;

    /// <summary>The planes that are not fully up, in the gateway's order.</summary>
    public IReadOnlyList<AiRuntimePlane> PlanesNotUp => Planes.Where(p => !p.IsFullyUp).ToArray();

    /// <summary>Known planes (a, b, c) the gateway did not list. It always sends all three; a missing one is a gap, not good news.</summary>
    public IReadOnlyList<string> MissingPlaneIds => KnownPlaneIds
        .Where(id => !Planes.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    /// <summary>
    /// The one-line coverage statement beside the finding count: "212 processes, 3 not fully readable, 148 connections, 6 with no owner".
    /// </summary>
    public string CoverageSummary
    {
        get
        {
            var parts = new List<string> { Count(ProcessesObserved, "process", "processes") };
            if (ProcessesSkipped > 0)
            {
                parts.Add($"{ProcessesSkipped.ToString("N0", CultureInfo.InvariantCulture)} not fully readable");
            }

            parts.Add(Count(ConnectionsObserved, "connection", "connections"));
            if (ConnectionsUnattributed > 0)
            {
                parts.Add($"{ConnectionsUnattributed.ToString("N0", CultureInfo.InvariantCulture)} with no owner");
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>The host plane's own counters, as the CLI prints them: classified kernel events and those left out of non-agent lineage.</summary>
    public string HostPlaneSummary =>
        $"host plane: {HostPlaneObservations.ToString("N0", CultureInfo.InvariantCulture)} kernel events classified, " +
        $"{HostPlaneGated.ToString("N0", CultureInfo.InvariantCulture)} excluded outside AI-agent lineage";

    /// <summary>What the planes add up to. See <see cref="AiRuntimeCoverage"/>.</summary>
    public AiRuntimeCoverage Coverage => AiRuntimeCoverage.Of(this);

    /// <summary>
    /// The degraded reasons that say something the plane list does not: the gateway composes each plane's line from the plane's own
    /// reason, so a line that contains a plane's reason word for word only restates it.
    /// </summary>
    public IReadOnlyList<string> ExtraDegradedReasons => DegradedReasons
        .Where(line => !Planes.Any(p => p.Reason.Length > 0 && line.Contains(p.Reason, StringComparison.Ordinal)))
        .ToArray();

    private static string Count(int value, string singular, string plural) =>
        $"{value.ToString("N0", CultureInfo.InvariantCulture)} {(value == 1 ? singular : plural)}";
}

/// <summary>
/// What the planes of one snapshot add up to, and the reasons it is less than everything. <b>There is no "clean" in it.</b> The
/// strongest thing it says is that all three planes are reporting; whether the host is clean is not a thing a runtime sensor can
/// say, and this type exists so no screen says it by accident: "no findings" is only ever shown beside this.
/// </summary>
/// <param name="IsComplete">All three planes are up, nothing is reported degraded, nothing was unreadable and attribution is not mostly missing.</param>
/// <param name="FullyUpPlanes">How many planes are fully up.</param>
/// <param name="Headline">One sentence: what the planes are doing.</param>
/// <param name="Gaps">One line for each reason coverage is less than everything; empty exactly when <paramref name="IsComplete"/>.</param>
public sealed record AiRuntimeCoverage(bool IsComplete, int FullyUpPlanes, string Headline, IReadOnlyList<string> Gaps)
{
    /// <summary>The standing caveat printed with every empty findings list.</summary>
    public const string NoFindingsCaveat =
        "No findings is not a clean host. It means nothing the planes could see reached the reporting floor; what they could not see is listed above.";

    /// <summary>Judges <paramref name="snapshot"/>.</summary>
    public static AiRuntimeCoverage Of(AiRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var gaps = new List<string>();
        if (snapshot.Planes.Count == 0)
        {
            gaps.Add("Plane health unavailable: the gateway reported no planes.");
        }
        else
        {
            foreach (var id in snapshot.MissingPlaneIds)
            {
                gaps.Add($"Plane {id.ToUpperInvariant()} was not reported by the gateway.");
            }
        }

        gaps.AddRange(snapshot.PlanesNotUp.Select(p => p.Summary));
        gaps.AddRange(snapshot.ExtraDegradedReasons);
        if (snapshot.Degraded && snapshot.DegradedReasons.Count == 0)
        {
            gaps.Add("The gateway reports degraded coverage and gave no reason.");
        }

        if (snapshot.HasUnattributedWarning)
        {
            gaps.Add(snapshot.UnattributedWarning);
        }

        if (snapshot.UnreadableEntries > 0)
        {
            gaps.Add($"{snapshot.UnreadableEntries.ToString("N0", CultureInfo.InvariantCulture)} entr{(snapshot.UnreadableEntries == 1 ? "y" : "ies")} in the gateway's answer could not be read.");
        }

        var up = snapshot.Planes.Count(p => p.IsFullyUp);
        var complete = gaps.Count == 0 && snapshot.Planes.Count >= AiRuntimeSnapshot.KnownPlaneIds.Count;
        var headline = complete
            ? "All three planes are reporting."
            : snapshot.Planes.Count == 0
                ? "Coverage is unknown: no plane health was reported."
                : $"Partial coverage: {up} of {Math.Max(snapshot.Planes.Count, AiRuntimeSnapshot.KnownPlaneIds.Count)} planes fully reporting.";

        return new AiRuntimeCoverage(complete, up, headline, gaps);
    }
}
