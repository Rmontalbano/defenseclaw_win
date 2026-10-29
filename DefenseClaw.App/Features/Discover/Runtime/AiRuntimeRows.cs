using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.AiRuntime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One plane on the strip. The chip says the state in a word; a plane that is anything but fully up also carries its whole reason as
/// text under the strip (<see cref="Reason"/>), not only in a tooltip: a blind plane drawn as a grey dot is the failure this panel exists
/// to prevent.
/// </summary>
public sealed partial class AiRuntimePlaneRow : ObservableObject
{
    /// <summary>Set by the panel while the snapshot on screen is stale (the last read failed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneKey), nameof(ChipText), nameof(AutomationName))]
    private bool _isStale;

    public AiRuntimePlaneRow(AiRuntimePlane plane, bool isStale = false)
    {
        Plane = plane ?? throw new ArgumentNullException(nameof(plane));
        _isStale = isStale;
    }

    public AiRuntimePlane Plane { get; }

    /// <summary>"A · inference heartbeat".</summary>
    public string Name => Plane.DisplayName;

    /// <summary><c>up</c>, <c>partial</c>, <c>idle</c>, <c>blind</c> or <c>off</c>.</summary>
    public string Badge => Plane.Badge;

    /// <summary>
    /// Tone key of the chip: Ok for up, Warn for partial and idle, Bad for blind, Neutral for off. While the snapshot is stale an "up" is not
    /// evidence of anything now, so it loses its green (Neutral); a plane that was not up keeps the colour of its problem.
    /// </summary>
    public string ToneKey => Plane.State switch
    {
        AiRuntimePlaneState.Up => IsStale ? "Neutral" : "Ok",
        AiRuntimePlaneState.Partial or AiRuntimePlaneState.Idle => "Warn",
        AiRuntimePlaneState.Blind => "Bad",
        _ => "Neutral",
    };

    /// <summary>The chip's text: "A · inference heartbeat: up", with "(last poll)" while the snapshot is stale.</summary>
    public string ChipText => $"{Name}: {Badge}{(IsStale ? " (last poll)" : string.Empty)}";

    /// <summary>The plane is not fully up, so it owes the reader its reason.</summary>
    public bool HasReason => !Plane.IsFullyUp;

    /// <summary>The full reason for a plane that is not fully up (a sentence even when the gateway sent none); empty for one that is.</summary>
    public string Reason => Plane.IsFullyUp ? string.Empty : Plane.ReasonText;

    /// <summary>How the plane runs, when it does (partial or up); empty otherwise.</summary>
    public string Mechanism => Plane.Running ? Plane.Mechanism : string.Empty;

    public bool HasMechanism => Mechanism.Length > 0;

    /// <summary>What a screen reader announces for the chip and its reason.</summary>
    public string AutomationName => IsStale ? Plane.Summary + " (as of the last successful poll)" : Plane.Summary;

    public override string ToString() => AutomationName;
}

/// <summary>One attributed peer in the inspector.</summary>
public sealed record AiRuntimeProviderRow(AiRuntimeProvider Provider)
{
    public string Display => Provider.Display;

    public string Detail => Provider.Detail;

    public override string ToString() => $"{Display} ({Detail})";
}

/// <summary>One weighted signal in the inspector.</summary>
public sealed record AiRuntimeSignalRow(AiRuntimeSignal Signal)
{
    /// <summary>"+28".</summary>
    public string WeightText => "+" + Signal.Weight.ToString(CultureInfo.InvariantCulture);

    public string Id => Signal.Id;

    public string Title => Signal.Title;

    public string Detail => Signal.Detail;

    public bool HasDetail => Signal.Detail.Length > 0;

    public bool HasTitle => Signal.Title.Length > 0;

    public override string ToString() => $"{WeightText} {Signal.Id}{(HasDetail ? " " + Signal.Detail : string.Empty)}";
}

/// <summary>
/// One finding as the table and the inspector show it. Immutable: a refresh that changes anything about a finding replaces its row
/// (<see cref="Signature"/> says whether it did), so a row that did not change keeps its visuals, its scroll position and its selection.
/// </summary>
public sealed record AiRuntimeFindingRow(AiRuntimeFinding Finding)
{
    public string Id => Finding.Id;

    public string Severity => Finding.SeverityLabel;

    /// <summary>The tone key of the severity badge: Critical, High, Medium, Low, or Info for the quiet look.</summary>
    public string SeverityTone => Finding.SeverityKey switch
    {
        "critical" => "Critical",
        "high" => "High",
        "medium" => "Medium",
        "low" => "Low",
        _ => "Info",
    };

    public int SeverityRank => Finding.SeverityRank;

    public int Score => Finding.Score;

    public string ScoreText => Finding.Score.ToString(CultureInfo.InvariantCulture);

    public string Process => Finding.Process.Length > 0 ? Finding.Process : "(unknown process)";

    public int Pid => Finding.Pid;

    public string PidText => Finding.Pid.ToString(CultureInfo.InvariantCulture);

    public string User => Finding.User.Length > 0 ? Finding.User : "unknown";

    public string Agent => Finding.AgentName.Length > 0 ? Finding.AgentName : "—";

    public bool HasAgent => Finding.AgentName.Length > 0;

    public string Providers => Finding.ProviderSummary;

    public string Cmdline => Finding.Cmdline;

    public bool HasCmdline => Finding.Cmdline.Length > 0;

    /// <summary>The observed sequence for a chain finding.</summary>
    public string Chain => Finding.Chain;

    public bool HasChain => Finding.Chain.Length > 0;

    /// <summary>The inventory verdict as a word; never blank ("unknown" when the gateway sent none).</summary>
    public string Verdict => Finding.Correlation.VerdictText;

    /// <summary>Warn for <c>unaccounted</c> (a complete inventory could not explain it: the interesting reading), neutral otherwise.</summary>
    public string VerdictTone => Finding.Correlation.IsUnaccounted ? "Warn" : "Neutral";

    public string VerdictReason => Finding.Correlation.Reason;

    public bool HasVerdictReason => Finding.Correlation.Reason.Length > 0;

    public string VerdictMatches => Finding.Correlation.MatchedSignalIds.Count == 0 ? string.Empty : string.Join(", ", Finding.Correlation.MatchedSignalIds);

    public bool HasVerdictMatches => Finding.Correlation.MatchedSignalIds.Count > 0;

    public string VerdictCategories => Finding.Correlation.Categories.Count == 0 ? string.Empty : string.Join(", ", Finding.Correlation.Categories);

    public bool HasVerdictCategories => Finding.Correlation.Categories.Count > 0;

    public IReadOnlyList<AiRuntimeProviderRow> ProviderRows { get; } = Finding.Providers.Select(p => new AiRuntimeProviderRow(p)).ToArray();

    public IReadOnlyList<AiRuntimeSignalRow> SignalRows { get; } = Finding.Signals.Select(s => new AiRuntimeSignalRow(s)).ToArray();

    public bool HasProviders => Finding.Providers.Count > 0;

    public bool HasSignals => Finding.Signals.Count > 0;

    /// <summary>"first seen 10:01:10, last seen 10:04:30" in local time; empty when the gateway sent neither.</summary>
    public string SeenText
    {
        get
        {
            var parts = new List<string>();
            if (Finding.FirstSeen is { } first)
            {
                parts.Add("first seen " + first.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }

            if (Finding.LastSeen is { } last)
            {
                parts.Add("last seen " + last.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }

            return string.Join(", ", parts);
        }
    }

    public bool HasSeen => SeenText.Length > 0;

    /// <summary>"Critical, score 100".</summary>
    public string Headline => $"{Severity}, score {ScoreText}";

    /// <summary>What a screen reader announces for the row.</summary>
    public string AutomationName =>
        $"{Severity} finding: {Process}, pid {PidText}, score {ScoreText}{(HasAgent ? ", agent " + Agent : string.Empty)}, inventory {Verdict}";

    /// <summary>Everything a row shows. Equal signatures mean a refresh has nothing to change in this row.</summary>
    public string Signature { get; } = string.Join(
        '\u001f',
        Finding.Id,
        Finding.Severity,
        Finding.Score,
        Finding.Pid,
        Finding.Process,
        Finding.Cmdline,
        Finding.User,
        Finding.AgentName,
        Finding.Correlation.Verdict,
        Finding.Correlation.Reason,
        string.Join('\u001e', Finding.Correlation.MatchedSignalIds),
        string.Join('\u001e', Finding.Correlation.Categories),
        string.Join('\u001e', Finding.Providers.Select(p => p.Identity + "/" + p.Category + "/" + p.AttributionSource + "/" + p.Confidence.ToString("R", CultureInfo.InvariantCulture))),
        string.Join('\u001e', Finding.Signals.Select(s => s.Identity + "/" + s.Title + "/" + s.Weight.ToString(CultureInfo.InvariantCulture))),
        Finding.FirstSeen?.UtcTicks,
        Finding.LastSeen?.UtcTicks);

    public override string ToString() => AutomationName;
}

/// <summary>One grant on the prerequisites card.</summary>
public sealed record AiRuntimeGrantRow(AiRuntimeGrant Grant)
{
    public string Plane => Grant.Plane;

    public string Needs => Grant.Needs;

    public string Why => Grant.Why;

    /// <summary>The runtime's own words on how to grant it; shown while the grant is open.</summary>
    public string How => Grant.State is AiRuntimeGrantState.Granted or AiRuntimeGrantState.Off ? string.Empty : Grant.How;

    public bool HasHow => How.Length > 0;

    public string StateText => Grant.StateText;

    /// <summary>Granted is Ok, missing is Bad, unknown is Warn (it cannot be verified from here: not a failure, not a pass), off is Neutral.</summary>
    public string ToneKey => Grant.State switch
    {
        AiRuntimeGrantState.Granted => "Ok",
        AiRuntimeGrantState.Missing => "Bad",
        AiRuntimeGrantState.Unknown => "Warn",
        _ => "Neutral",
    };

    /// <summary>The copy-only commands inside <see cref="How"/>, one per line; empty when there are none.</summary>
    public string CommandText => string.Join(Environment.NewLine, Grant.CommandLines);

    public bool HasCommands => Grant.CommandLines.Count > 0;

    public string AutomationName => $"{Plane}: {StateText}. Needs {Needs}.";

    public override string ToString() => AutomationName;
}
