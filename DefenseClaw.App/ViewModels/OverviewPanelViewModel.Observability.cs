using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels;

/// <summary>One labelled fact under a destination's row (<c>Redaction</c> <c>unredacted (none)</c>), with the tone its value is drawn in.</summary>
public sealed record ObservabilityFact(string Label, string Value, string ToneKey = "Neutral");

/// <summary>
/// One telemetry destination or audit sink of the Observability card: where events go, what kind of place it is, whether it is delivering,
/// and which signals it takes. A record so an unchanged row keeps its visuals across a poll.
/// <para>
/// A row of a destination the compiled plan describes (CUST-272) also says what the plan sets for it - <see cref="Policy"/>, <see cref="Buckets"/>,
/// <see cref="Redaction"/>, <see cref="Limits"/> - what the gateway reports it is holding and has done - <see cref="Queue"/>, <see cref="LastResult"/> -
/// and where it sends - <see cref="Endpoint"/>, the host and port only. A row built from <c>/health</c> alone (a plan that could not be read, an audit
/// sink, a destination the plan does not list) leaves those empty, and the card draws it as it always did.
/// </para>
/// </summary>
public sealed record ObservabilityRow
{
    public required string Name { get; init; }

    /// <summary><c>otel</c> for a telemetry destination, <c>audit_sinks</c> for an audit sink, the Mac's words.</summary>
    public string Target { get; init; } = "otel";

    /// <summary>The destination's kind or preset: <c>sqlite</c>, <c>otlp</c>, <c>splunk_hec</c>…</summary>
    public string Kind { get; init; } = "—";

    /// <summary>
    /// What the gateway reports: <c>healthy</c>, <c>enabled</c>, <c>disabled</c>, <c>error</c>… For a destination the plan describes and the gateway
    /// does not report, <c>disabled</c> when the policy turns it off and <c>unavailable</c> when it is on and nothing answers for it.
    /// </summary>
    public string State { get; init; } = "unknown";

    /// <summary>Ok / Warn / Bad / Neutral.</summary>
    public string StateKey { get; init; } = "Neutral";

    public string Signals { get; init; } = "none";

    /// <summary>Delivery counters and the reason the destination is in its state, for the tooltip: <c>activated · 0 accepted · 0 delivered</c>.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The compiled plan's word for the destination: <c>enabled</c> or <c>disabled</c>. Empty when no plan describes the row.</summary>
    public string Policy { get; init; } = string.Empty;

    /// <summary>How many of the plan's buckets reach the destination: <c>13/14</c>, a fact under the row (the table is too narrow for a column of it). Empty without a plan.</summary>
    public string Buckets { get; init; } = string.Empty;

    /// <summary>What the destination sends its events as: <c>unredacted (none)</c>, <c>redacted: sensitive</c>, <c>mixed: none, strict</c>, <c>not-applicable</c>.</summary>
    public string Redaction { get; init; } = string.Empty;

    /// <summary>What the destination is holding now, from the gateway: <c>0/2048 items, 0 B/64.0 MiB, 0 dropped</c>; <c>unavailable</c> when it reports none.</summary>
    public string Queue { get; init; } = string.Empty;

    /// <summary>The queue and batch limits the plan sets: <c>queue=2048 items/64.0 MiB; batch=256 items/8.0 MiB; delay=1000ms</c>; <c>not-applicable</c> when it sets none.</summary>
    public string Limits { get; init; } = string.Empty;

    /// <summary>The last delivery and the last failure, from the gateway: <c>ok 14:03:07; error 13:58:02 (timeout)</c>; <c>unavailable</c> when it reports neither.</summary>
    public string LastResult { get; init; } = string.Empty;

    /// <summary>Warn when the destination's last word was a failure; else Neutral.</summary>
    public string LastResultKey { get; init; } = "Neutral";

    /// <summary>Where the destination sends: <c>collector.example.test:4318</c>. The host and port only - never userinfo, a path or a query. Empty when config.yaml names none.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>True when a plan describes the row, so the columns and facts that come from it are drawn.</summary>
    public bool HasPlan => Policy.Length > 0;

    /// <summary>The facts under the row's line (buckets, redaction, queue, limits, last result, endpoint), in the order the TUI prints its columns; one that has nothing to say is left out.</summary>
    public IReadOnlyList<ObservabilityFact> Facts
    {
        get
        {
            var facts = new List<ObservabilityFact>(6);
            void Add(string label, string value, string tone = "Neutral")
            {
                if (value.Length > 0 && value != "—")
                {
                    facts.Add(new ObservabilityFact(label, value, tone));
                }
            }

            Add("Buckets", Buckets);
            Add("Redaction", Redaction);
            Add("Queue", Queue);
            Add("Limits", Limits);
            Add("Last result", LastResult, LastResultKey);
            Add("Endpoint", Endpoint);
            return facts;
        }
    }

    /// <summary>True when there is a fact to draw under the row.</summary>
    public bool HasFacts => Facts.Count > 0;

    public override string ToString() => HasPlan
        ? ServiceRow.JoinSentences(
            $"{Name}, {Target} destination",
            Kind,
            $"policy {Policy}",
            State,
            $"signals {Signals}",
            $"buckets {Buckets}",
            $"redaction {Redaction}",
            $"queue {Queue}",
            $"limits {Limits}",
            $"last result {LastResult}",
            Endpoint.Length > 0 ? $"endpoint {Endpoint}" : string.Empty,
            Detail)
        : ServiceRow.JoinSentences($"{Name}, {Target} destination", Kind, State, $"signals {Signals}", Detail);
}

/// <summary>
/// The Local SQLite line of the Observability card: the local store the gateway always writes, as the TUI's Overview prints it - the retention window,
/// whether the reaper that enforces it is healthy, whether the raw LLM-judge text is kept, and the two files.
/// </summary>
/// <param name="Retention"><c>90 days</c>, <c>1 day</c>, <c>unbounded</c> (0), or <c>runtime default</c> when neither config.yaml nor the gateway says.</param>
/// <param name="Controller">The retention reaper's state from <c>/health</c> (<c>healthy</c>, <c>degraded (sqlite_busy)</c>); <c>unavailable</c> when the gateway reports none.</param>
/// <param name="JudgeCapture"><c>enabled</c> or <c>disabled</c> (<c>guardrail.retain_judge_bodies</c>).</param>
/// <param name="EventHistoryPath">The audit database: <c>observability.local.path</c>, else the data directory's <c>audit.db</c>.</param>
/// <param name="JudgeBodiesPath">The judge-body database: <c>observability.local.judge_bodies_path</c>, else the data directory's <c>judge_bodies.db</c>.</param>
/// <param name="NeedsAttention">True for a store with no retention limit or a reaper that is degraded or stopped.</param>
public sealed record LocalStorageInfo(
    string Retention,
    string Controller,
    string JudgeCapture,
    string EventHistoryPath,
    string JudgeBodiesPath,
    bool NeedsAttention)
{
    /// <summary><c>Local SQLite · retention=90 days · controller=healthy · judge capture=enabled</c>, the TUI's line.</summary>
    public string Summary => $"Local SQLite · retention={Retention} · controller={Controller} · judge capture={JudgeCapture}";

    /// <summary><c>Event history: …\audit.db · Judge bodies: …\judge_bodies.db</c>, the TUI's second line.</summary>
    public string Paths => $"Event history: {EventHistoryPath} · Judge bodies: {JudgeBodiesPath}";
}

/// <summary>
/// The Observability card (CUST-209): every destination and sink <c>/health</c> reports under <c>telemetry.details.destinations[]</c> (and
/// <c>sinks.details.sinks[]</c> on a build that has them), plus the gateway's own admission that it cannot keep its event history
/// (<c>event_history_failure</c>, for instance <c>sqlite_write_failed</c>), which the Services box used to hide behind a bare "error".
/// Re-derived from the snapshot on every poll; no I/O.
/// <para>
/// <b>The compiled plan (CUST-272).</b> The TUI's card is the effective v8 plan with <c>/health</c> merged in. This one gets the plan from
/// <c>defenseclaw observability plan --format json</c> (read-only, on a pool thread, time-boxed, and cached: it is read on the first visit, on
/// Refresh, when config.yaml changes and when it is five minutes old, never on a poll - <see cref="ObservabilityPlanReader"/>) and merges it
/// with <c>/health</c> by destination name. The plan says what a destination <i>should</i> do - enabled or not, which buckets, what redaction, what
/// limits; <c>/health</c> says what it is doing - its state, queue and last result; config.yaml says where it sends, and the local store's retention,
/// files and judge-body setting, which the plan document does not carry. A destination the plan lists and the gateway does not report is
/// <c>disabled</c> when its policy is off and <c>unavailable</c> when it is on: silence is never read as health.
/// </para>
/// <para>
/// <b>Failure degrades; it does not break.</b> A plan that could not be read (no CLI, a timeout, an exit code, output that is not a plan) leaves the
/// card exactly as it was before the plan existed - the rows <c>/health</c> gives - with a one-line note saying why. <b>No address is shown whole:</b>
/// an endpoint is reduced to host and port while config.yaml is read, and every sentence the gateway writes about a failure passes
/// <see cref="EndpointDisplay.ScrubText"/>, so a credential in a URL reaches neither the card, a tooltip nor a screen reader; and the plan the command
/// prints (which is what lands in Activity) names destinations, never addresses.
/// </para>
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private ObservabilityPlanReader? _planReader;
    private ObservabilityPlan? _observabilityPlan;
    private ObservabilityPlanRead? _planRead;
    private bool _planReading;
    private ConfigDocument? _factsSource;
    private ObservabilityConfigFacts _facts = ObservabilityConfigFacts.Empty;

    /// <summary>
    /// What reads the plan. Built on first use over the app's own runner (so the run is in Activity like every command) and the live config.yaml
    /// (so a plan is never served for a configuration it was not compiled from); a test swaps in a reader over a fake command.
    /// </summary>
    internal ObservabilityPlanReader PlanReader
    {
        get => _planReader ??= ObservabilityPlanReader.ForCli(Services.Cli, () => Services.Config);
        set => _planReader = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Where a <c>/health</c> build puts the failure: inside the telemetry details, beside them, or at the top level.</summary>
    private const string EventHistoryFailureKey = "event_history_failure";

    public ObservableCollection<ObservabilityRow> ObservabilityRows { get; } = new();

    /// <summary>The event-history failure the gateway reports, or empty; shown as a warning above the table.</summary>
    [ObservableProperty]
    private string _eventHistoryFailure = string.Empty;

    [ObservableProperty]
    private bool _hasEventHistoryFailure;

    [ObservableProperty]
    private string _observabilityEmptyText = "Nothing has been read from the gateway yet.";

    [ObservableProperty]
    private bool _hasObservabilityRows;

    /// <summary>True when the compiled plan is in hand, so the Policy column is drawn; false draws the table <c>/health</c> alone gives.</summary>
    [ObservableProperty]
    private bool _hasObservabilityPlan;

    /// <summary>
    /// The card's one-line note: where the plan columns come from and how old they are, or - when the plan could not be read - why the card shows only
    /// what the gateway reports. Empty before the first read has started.
    /// </summary>
    [ObservableProperty]
    private string _observabilityNote = string.Empty;

    [ObservableProperty]
    private bool _hasObservabilityNote;

    /// <summary>Neutral, or Warn when the plan could not be read.</summary>
    [ObservableProperty]
    private string _observabilityNoteToneKey = "Neutral";

    /// <summary>The Local SQLite line (<c>Local SQLite · retention=90 days · controller=healthy · judge capture=enabled</c>); empty without a plan.</summary>
    [ObservableProperty]
    private string _localStorageSummary = string.Empty;

    /// <summary>The Local SQLite files (<c>Event history: … · Judge bodies: …</c>); empty without a plan.</summary>
    [ObservableProperty]
    private string _localStoragePaths = string.Empty;

    /// <summary>Warn for a store with no retention limit or a reaper that is degraded or stopped; else Neutral.</summary>
    [ObservableProperty]
    private string _localStorageToneKey = "Neutral";

    [ObservableProperty]
    private bool _hasLocalStorage;

    /// <summary>
    /// The aggregate redaction label of the install (<c>per-route · unredacted</c>): the Configuration card's Redaction row, and what a status-strip chip
    /// would show. <c>per-route (loading)</c> until the plan has been read and <c>per-route (unavailable)</c> when it could not be.
    /// </summary>
    [ObservableProperty]
    private string _redactionSummary = ObservabilityRedaction.Loading;

    /// <summary>The Local SQLite facts behind <see cref="LocalStorageSummary"/>; null without a plan.</summary>
    internal LocalStorageInfo? LocalStorage { get; private set; }

    /// <summary>A destination <c>/health</c> reports, with what was read from its entry.</summary>
    /// <param name="Row">The row the entry makes on its own - what the card shows when no plan describes it.</param>
    /// <param name="Health">The entry reduced to closed tokens, counters and timestamps; null when it could not be.</param>
    /// <param name="Key">The name it is merged on.</param>
    /// <param name="ReportedKind">The preset or kind the entry names; null when it names neither.</param>
    internal sealed record LiveDestination(ObservabilityRow Row, DestinationHealth? Health, string Key, string? ReportedKind);

    private void BuildObservability(GatewayHealth? health)
    {
        var live = new List<LiveDestination>();
        var sinkRows = new List<ObservabilityRow>();
        var retention = RetentionHealth.None;
        string? failure = null;

        if (health is null)
        {
            ObservabilityEmptyText = "The gateway is not answering, so its destinations cannot be listed.";
        }
        else
        {
            if (health.Telemetry?.Details is { ValueKind: JsonValueKind.Object } telemetry)
            {
                retention = DestinationHealthReader.ReadRetention(telemetry);
                foreach (var item in Items(telemetry, "destinations"))
                {
                    if (Destination(item) is { } row)
                    {
                        live.Add(new LiveDestination(
                            row,
                            DestinationHealthReader.Read(item),
                            ObservabilityPlanParser.CleanName(Text(item, "name")),
                            First(Text(item, "preset"), Text(item, "kind"))));
                    }
                }
            }

            if (Service(health, "sinks")?.Details is { ValueKind: JsonValueKind.Object } sinks)
            {
                foreach (var item in Items(sinks, "sinks"))
                {
                    if (Sink(item) is { } row)
                    {
                        sinkRows.Add(row);
                    }
                }
            }

            failure = FindFailure(health);
            ObservabilityEmptyText = "No runtime-loaded destinations. Configure one in Setup, then restart the gateway.";
        }

        // Without a plan the card is what /health gives, exactly as before the plan existed; with one, the plan's destinations come first, each
        // merged with its /health entry, then the entries the plan does not list, then the sinks.
        var plan = _observabilityPlan;
        var rows = new List<ObservabilityRow>();
        if (plan is null)
        {
            rows.AddRange(live.Select(static l => l.Row));
        }
        else
        {
            rows.AddRange(MergeWithPlan(plan, live, ObservabilityFacts(), FormatResultTime));
        }

        rows.AddRange(sinkRows);

        SyncByEquality(ObservabilityRows, rows, static row => row.Target + "/" + row.Name);
        HasObservabilityRows = rows.Count > 0;
        HasObservabilityPlan = plan is not null;
        EventHistoryFailure = failure ?? string.Empty;
        HasEventHistoryFailure = failure is not null;

        BuildLocalStorage(plan, retention);
        BuildObservabilityNote();
        RedactionSummary = plan?.RedactionSummary ?? (_planRead is { IsOk: false } ? ObservabilityRedaction.Unavailable : ObservabilityRedaction.Loading);
    }

    /// <summary>
    /// The plan's destinations, each with what <c>/health</c> and config.yaml add to it, then the <c>/health</c> entries no plan destination claimed.
    /// A destination is claimed by the first unclaimed entry of the same name (without case), so two entries of one name never both stand for it.
    /// </summary>
    private static List<ObservabilityRow> MergeWithPlan(
        ObservabilityPlan plan,
        IReadOnlyList<LiveDestination> live,
        ObservabilityConfigFacts config,
        Func<DateTimeOffset, string> formatTime)
    {
        var rows = new List<ObservabilityRow>(plan.Destinations.Count + live.Count);
        var claimed = new bool[live.Count];

        foreach (var destination in plan.Destinations)
        {
            LiveDestination? entry = null;
            for (var i = 0; i < live.Count; i++)
            {
                if (!claimed[i] && string.Equals(live[i].Key, destination.Name, StringComparison.OrdinalIgnoreCase))
                {
                    claimed[i] = true;
                    entry = live[i];
                    break;
                }
            }

            rows.Add(MergedRow(plan, destination, entry, config.Destination(destination.Name), formatTime));
        }

        for (var i = 0; i < live.Count; i++)
        {
            if (!claimed[i])
            {
                rows.Add(live[i].Row);
            }
        }

        return rows;
    }

    private static ObservabilityRow MergedRow(
        ObservabilityPlan plan,
        PlanDestination destination,
        LiveDestination? entry,
        ConfiguredDestination? configured,
        Func<DateTimeOffset, string> formatTime)
    {
        var health = entry?.Health;

        // The gateway's word wins, even where it disagrees with the policy (a restart is due). Without one: off by policy, or on and silent.
        var state = health is { State.Length: > 0 } ? health.State : destination.Enabled ? "unavailable" : "disabled";

        var kind = entry?.ReportedKind
            ?? (configured is { Kind.Length: > 0 } ? configured.Kind : null)
            ?? (destination.Kind.Length > 0 ? destination.Kind : null)
            ?? "—";

        var signals = destination.Enabled && destination.Signals.Count > 0
            ? string.Join(", ", destination.Signals)
            : entry is { Row.Signals: not ("none" or "") } reported ? reported.Row.Signals : destination.Enabled ? "none" : "—";

        return new ObservabilityRow
        {
            Name = entry?.Row.Name ?? DisplayName(destination.Name),
            Target = "otel",
            Kind = kind,
            State = state,
            StateKey = ObservabilityTone(state),
            Signals = signals,
            Detail = entry?.Row.Detail ?? (destination.Enabled
                ? "The gateway does not report this destination: it is not running, or it has not loaded it yet."
                : "Off in the policy: the gateway does not run it."),
            Policy = destination.Enabled ? "enabled" : "disabled",
            Buckets = $"{destination.RoutedBuckets.ToString(CultureInfo.InvariantCulture)}/{plan.BucketCount.ToString(CultureInfo.InvariantCulture)}",
            Redaction = destination.RedactionLabel,
            Queue = health?.QueueLabel ?? DestinationHealth.Unavailable,
            Limits = destination.LimitsLabel,
            LastResult = health?.LastResultLabel(formatTime) ?? DestinationHealth.Unavailable,
            LastResultKey = health is { LastResultFailed: true } ? "Warn" : "Neutral",
            Endpoint = configured?.Endpoint ?? string.Empty,
        };
    }

    /// <summary>The config.yaml facts (retention, files, judge capture, endpoints), parsed once per parsed config and kept until it is replaced.</summary>
    private ObservabilityConfigFacts ObservabilityFacts()
    {
        var config = Services.Config;
        if (!ReferenceEquals(config, _factsSource))
        {
            _facts = ObservabilityConfigFacts.FromConfig(config);
            _factsSource = config;
        }

        return _facts;
    }

    /// <summary>
    /// The Local SQLite line, from config.yaml (retention, files, judge capture - what the TUI's compiled view is built from) and <c>/health</c>
    /// (whether the reaper is keeping up). Drawn only with the plan: it is part of the effective view the plan stands for, and the card without
    /// one is today's.
    /// </summary>
    private void BuildLocalStorage(ObservabilityPlan? plan, RetentionHealth retention)
    {
        if (plan is null)
        {
            LocalStorage = null;
            HasLocalStorage = false;
            LocalStorageSummary = string.Empty;
            LocalStoragePaths = string.Empty;
            LocalStorageToneKey = "Neutral";
            return;
        }

        var facts = ObservabilityFacts();

        // config.yaml's own value, else the window the running gateway reports (the runtime's default when config.yaml is silent), else nothing to say.
        var days = facts.RetentionDays ?? retention.Days;
        var unbounded = days == 0;

        var info = new LocalStorageInfo(
            days is { } window ? (window == 0 ? "unbounded" : window == 1 ? "1 day" : window.ToString(CultureInfo.InvariantCulture) + " days") : "runtime default",
            retention.ControllerLabel,
            facts.JudgeCapture ? "enabled" : "disabled",
            DisplayNames.Visible(Services.Paths.ResolveConfiguredFile(facts.LocalPath, Services.Paths.AuditDatabasePath)),
            DisplayNames.Visible(Services.Paths.ResolveConfiguredFile(facts.JudgeBodiesPath, Services.Paths.JudgeBodiesDatabasePath)),
            unbounded || retention.NeedsAttention);

        LocalStorage = info;
        HasLocalStorage = true;
        LocalStorageSummary = info.Summary;
        LocalStoragePaths = info.Paths;
        LocalStorageToneKey = info.NeedsAttention ? "Warn" : "Neutral";
    }

    /// <summary>The note under the card: where the plan columns come from, or why there are none.</summary>
    private void BuildObservabilityNote()
    {
        string note;
        var tone = "Neutral";
        if (_planRead is { IsOk: false } failed)
        {
            note = $"Observability plan unavailable: {failed.Message}. The card shows what the gateway reports.";
            tone = "Warn";
        }
        else if (_planRead is { Plan: not null } read)
        {
            note = $"Policy, buckets, redaction and limits are from defenseclaw observability plan, read {ReadAge(read.ReadAt)}; state, queue and last result are from the gateway.";
        }
        else
        {
            note = _planReading ? "Reading defenseclaw observability plan…" : string.Empty;
        }

        ObservabilityNote = note;
        HasObservabilityNote = note.Length > 0;
        ObservabilityNoteToneKey = tone;
    }

    /// <summary>
    /// Reads the plan when it is due (or <paramref name="force"/>d: Refresh) and redraws the card. The reader decides what is due - its answer is
    /// reused until five minutes old, until config.yaml is replaced, or until a read is forced - and runs the command on a pool thread, so this
    /// costs a comparison on every call but the ones that read. A failure is data (<see cref="ObservabilityPlanRead"/>), never an exception; a
    /// cancelled wait (the panel left the screen) changes nothing.
    /// </summary>
    internal async Task RefreshObservabilityPlanAsync(bool force, CancellationToken cancellationToken)
    {
        var first = _planRead is null;
        if (first && !_planReading)
        {
            _planReading = true;
            BuildObservabilityNote();
        }

        ObservabilityPlanRead read;
        try
        {
            read = await PlanReader.GetAsync(force, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _planReading = false;
        }

        ApplyObservabilityPlan(read);
    }

    /// <summary>
    /// Takes a read of the plan as the current one (what <see cref="RefreshObservabilityPlanAsync"/> does with a run's answer, and what a test hands in)
    /// and redraws the card and the Configuration card's Redaction row. The same answer twice is not redrawn.
    /// </summary>
    internal void ApplyObservabilityPlan(ObservabilityPlanRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (ReferenceEquals(read, _planRead))
        {
            return;
        }

        _planRead = read;
        _observabilityPlan = read.Plan;
        BuildObservability(_snapshot.Health);
        BuildConfiguration();
    }

    /// <summary>How long ago a plan was read, as the note says it: <c>just now</c> for the first few seconds (the poll's clock would say <c>0s ago</c>), then <c>3m ago</c>.</summary>
    private static string ReadAge(DateTimeOffset at) => DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(10) ? "just now" : Relative(at);

    /// <summary>A time the gateway reported, for the Last result fact: the clock for today's, the date and clock for an older one.</summary>
    internal static string FormatResultTime(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : local.ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>The name a destination is shown under: Galileo's preset has a proper name; every other destination is its own.</summary>
    private static string DisplayName(string name) => string.Equals(name, "galileo", StringComparison.OrdinalIgnoreCase) ? "Galileo" : name;

    private static ObservabilityRow? Destination(JsonElement item)
    {
        if (Text(item, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        var enabled = item.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        var state = Text(item, "state");
        var stateText = state.Length > 0 ? state : enabled ? "enabled" : "disabled";

        return new ObservabilityRow
        {
            Name = DisplayName(name),
            Target = "otel",
            Kind = First(Text(item, "preset"), Text(item, "kind")) ?? "otlp",
            State = stateText,
            StateKey = ObservabilityTone(stateText),
            Signals = SignalsOf(item),
            Detail = DetailOf(item),
        };
    }

    private static ObservabilityRow? Sink(JsonElement item)
    {
        if (Text(item, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        var enabled = item.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
        var state = Text(item, "state");
        var stateText = state.Length > 0 ? state : enabled ? "enabled" : "disabled";

        return new ObservabilityRow
        {
            Name = name,
            Target = "audit_sinks",
            Kind = First(Text(item, "kind"), Text(item, "preset")) ?? "unknown",
            State = stateText,
            StateKey = ObservabilityTone(stateText),
            Signals = "audit-events",
            Detail = DetailOf(item),
        };
    }

    /// <summary>The tone of a destination's state: green for one that is delivering, red for one that is failing, amber for one that is in between, grey for one that is off.</summary>
    internal static string ObservabilityTone(string state) => state.Trim().ToUpperInvariant() switch
    {
        "HEALTHY" or "ENABLED" or "OK" or "RUNNING" or "ACTIVE" => "Ok",
        "ERROR" or "FAILED" or "FAILING" or "UNHEALTHY" => "Bad",
        "DEGRADED" or "PENDING" or "STARTING" or "RETRYING" => "Warn",
        _ => "Neutral",
    };

    /// <summary>The signals a destination takes: a JSON array or a string, joined; <c>none</c> when there are none.</summary>
    private static string SignalsOf(JsonElement item)
    {
        if (!item.TryGetProperty("signals", out var signals))
        {
            return "none";
        }

        var text = signals.ValueKind switch
        {
            JsonValueKind.Array => string.Join(", ", signals.EnumerateArray().Where(static s => s.ValueKind == JsonValueKind.String).Select(static s => s.GetString()).Where(static s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.String => signals.GetString() ?? string.Empty,
            _ => string.Empty,
        };

        return text.Length == 0 ? "none" : text;
    }

    /// <summary>The reason and the delivery counters, when there are any: what the gateway says about how it is going.</summary>
    private static string DetailOf(JsonElement item)
    {
        // Both are sentences the gateway built from a request that may have carried a credential (an address with a token in it, a header): shown
        // only after every URL in them is cut to its host and anything that looks like a credential is masked.
        var parts = new List<string>();
        if (EndpointDisplay.ScrubText(Text(item, "reason")) is { Length: > 0 } reason)
        {
            parts.Add(reason);
        }

        if (EndpointDisplay.ScrubText(Text(item, "last_error")) is { Length: > 0 } error)
        {
            parts.Add("last error: " + error);
        }

        if (item.TryGetProperty("counters", out var counters) && counters.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in counters.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value))
                {
                    parts.Add($"{value.ToString("N0", CultureInfo.CurrentCulture)} {property.Name}");
                }
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The event-history failure, if the gateway reports one: a string (<c>sqlite_write_failed</c>) or an object whose members are listed. Looked
    /// for where the builds put it (the telemetry details, the telemetry block, the top level); null when there is none.
    /// </summary>
    internal static string? FindFailure(GatewayHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        JsonElement? found = null;
        if (health.Telemetry?.Details is { ValueKind: JsonValueKind.Object } details && details.TryGetProperty(EventHistoryFailureKey, out var inDetails))
        {
            found = inDetails;
        }
        else if (health.Telemetry?.AdditionalData is { } block && block.TryGetValue(EventHistoryFailureKey, out var inBlock))
        {
            found = inBlock;
        }
        else if (health.AdditionalData is { } top && top.TryGetValue(EventHistoryFailureKey, out var inTop))
        {
            found = inTop;
        }

        return found is { } value ? Describe(value) : null;
    }

    private static string? Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { Length: > 0 } text ? text : null,
        JsonValueKind.Object => DescribeObject(value),
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => null,
        JsonValueKind.True => "reported",
        _ => value.ToString(),
    };

    private static string? DescribeObject(JsonElement value)
    {
        var parts = value.EnumerateObject()
            .Where(static p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            .Select(static p => p.Value.ValueKind == JsonValueKind.String ? (p.Name is "reason" or "error" or "code" or "message" ? p.Value.GetString() : $"{p.Name}: {p.Value.GetString()}") : $"{p.Name}: {p.Value}")
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static ServiceState? Service(GatewayHealth health, string key)
    {
        if (health.AdditionalData is not { } extra || !extra.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ServiceState>(element.GetRawText());
        }
        catch (JsonException)
        {
            // Keys the health model does not know stay untyped until here, so a block of the wrong shape ("state": 5, "since": "yesterday")
            // is first parsed in this method. Treat it as absent: letting it throw would abort Apply before it re-derives the gateway
            // action, the enforcement cards and the doctor card, and the poll's refresh that follows Apply.
            return null;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object)
            : Enumerable.Empty<JsonElement>();

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;

    private static string? First(params string[] candidates) => candidates.FirstOrDefault(static c => c.Length > 0);
}
