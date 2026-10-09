using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Time;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Enforcement card's four tiles (CUST-205): Hook Calls, Blocks, Findings and Guardrail. Each is a button that opens the panel where
/// the number comes from, exactly as the Mac's Overview does (<c>OverviewView.enforcementTilesCard</c>), through
/// <see cref="ShellNavigation"/>.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>
    /// How often the poll loop re-reads the all-time hook-call and block totals. The totals are an index group count plus the rows after a
    /// watermark (a covering-index walk of ~0.3 s on the 10 GB audit.db), so this is a minute, not the 15 s the 500-row window used to take.
    /// </summary>
    internal static readonly TimeSpan MetricsRefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>How soon the next read follows one that did not finish the first block scan; only until the tally has caught up once.</summary>
    internal static readonly TimeSpan MetricsCatchUpInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long one totals read may run before the tiles say audit.db did not answer (5 s; see <see cref="AppServices.ReaderTimeouts"/>).</summary>
    private TimeSpan MetricsTimeout => Services.ReaderTimeouts.HookTotals;

    /// <summary>"Updated just now" until a read is this old; after that the age is spelled out.</summary>
    private static readonly TimeSpan JustNow = TimeSpan.FromSeconds(10);

    /// <summary>The TUI's all-time totals (<c>connector_hook_event_stats</c>): hook calls exact, blocks tallied incrementally.</summary>
    private readonly ConnectorHookTotalsReader _metricsReader;

    private ConnectorHookTotals? _metrics;
    private string? _metricsProblem;
    private DateTimeOffset? _metricsAt;
    private MonotonicStamp _metricsStamp = MonotonicStamp.Never;
    private int _metricsReading;

    /// <summary>The audit window reader (how many times it has read is what the idle-cost tests hold still).</summary>
    internal ConnectorHookTotalsReader MetricsReader => _metricsReader;

    /// <summary>The hourly reader; see <see cref="MetricsReader"/>.</summary>
    internal HourlyActivityReader HourlyReader => _hourlyReader;

    [ObservableProperty]
    private string _enforcementUpdatedText = string.Empty;

    /// <summary>The four tiles, in reading order: Hook Calls, Blocks, Findings, Guardrail.</summary>
    public IReadOnlyList<EnforcementTile> EnforcementCards { get; private set; } = Array.Empty<EnforcementTile>();

    public EnforcementTile HookCallsTile => EnforcementCards[0];

    public EnforcementTile BlocksTile => EnforcementCards[1];

    public EnforcementTile FindingsTile => EnforcementCards[2];

    public EnforcementTile GuardrailTile => EnforcementCards[3];

    /// <summary>Hook Calls opens Logs on the hook stream; until the Logs panel takes a preset, on the panel itself.</summary>
    [RelayCommand]
    private void OpenHookCalls() => RequestNavigation("logs", new LogsPreset("hooks"));

    /// <summary>Blocks opens Audit on the blocks preset.</summary>
    [RelayCommand]
    private void OpenBlocks() => RequestNavigation("audit", new AuditPreset("blocks"));

    /// <summary>Findings opens Alerts on every unacknowledged finding.</summary>
    [RelayCommand]
    private void OpenFindings() => RequestNavigation("alerts", new AlertsFilter(Kind: AlertsFilter.KindAll));

    /// <summary>Guardrail opens Setup, where the guardrail is configured (the Setup panel has no guardrail anchor to scroll to yet).</summary>
    [RelayCommand]
    private void OpenGuardrail() => RequestNavigation("setup");

    private void BuildEnforcementCards()
    {
        EnforcementCards = new[]
        {
            new EnforcementTile(
                "Hook Calls",
                OpenHookCallsCommand,
                "Opens Logs filtered to hook calls.",
                "Open the hook logs"),
            new EnforcementTile(
                "Blocks",
                OpenBlocksCommand,
                "Opens Audit filtered to blocked decisions.",
                "Open the blocked audit events"),
            new EnforcementTile(
                "Findings",
                OpenFindingsCommand,
                "Opens Alerts on every unacknowledged finding.",
                "Open the alerts"),
            new EnforcementTile(
                "Guardrail",
                OpenGuardrailCommand,
                "Opens Setup, where the guardrail is configured.",
                "Open guardrail setup"),
        };

        RenderEnforcementCards();
    }

    /// <summary>
    /// Re-derives all four tiles from what is in hand: the last audit window, the alert counts, the config and the scope. No I/O, so it is
    /// called from every poll, every scope change and every finished read.
    /// </summary>
    internal void RenderEnforcementCards()
    {
        if (EnforcementCards.Count != 4)
        {
            return;
        }

        var scope = Services.ConnectorScope.Current;
        var roster = Math.Max(1, Services.ConnectorScope.Connectors.Count);

        // ---- Hook Calls and Blocks: persisted all-time totals, as the TUI counts them; the captions read the newest 500 hook rows.
        var hooks = HookCallsTile;
        var blocks = BlocksTile;
        if (_metrics is { Status: ConnectorHookTotalsStatus.Ok } metrics)
        {
            var fleet = metrics.Fleet;
            var counts = scope is null ? fleet : metrics.For(scope);
            var recent = scope is null ? metrics.Recent : metrics.RecentFor(scope);

            var callParts = new List<string>();
            if (recent.Total > 0)
            {
                callParts.Add($"recent a{recent.Allow} w{recent.Alert} b{recent.Block}");
                if (recent.TopHook.Length > 0)
                {
                    callParts.Add($"top: {Shorten(recent.TopHook, 22)}");
                }
            }
            else if (fleet.Calls == 0)
            {
                callParts.Add("no hook calls yet");
            }

            if (scope is not null)
            {
                callParts.Add($"fleet {Count(fleet.Calls)}");
            }

            var blockParts = new List<string>();
            if (!metrics.BlocksComplete)
            {
                var percent = metrics.HookRows <= 0 ? 0 : (int)Math.Min(99, 100 * metrics.BlocksScanned / metrics.HookRows);
                blockParts.Add($"counting… {percent}%");
            }
            else if (recent.TopBlockedTarget.Length > 0)
            {
                blockParts.Add($"top: {Shorten(recent.TopBlockedTarget, 22)} ×{recent.TopBlockedCount}");
            }
            else
            {
                blockParts.Add(counts.Blocks > 0 ? $"{Count(counts.Blocks)} persisted" : "no blocks yet");
            }

            if (scope is not null)
            {
                blockParts.Add($"fleet {Count(fleet.Blocks)}");
            }

            hooks.Set(
                scope is null ? $"Hook Calls ({roster} connector{(roster == 1 ? string.Empty : "s")})" : $"Hook Calls ({scope})",
                Count(counts.Calls),
                "Accent",
                string.Join(" · ", callParts));
            blocks.Set(
                scope is null ? "Blocks" : $"Blocks ({scope})",
                Count(counts.Blocks) + (metrics.BlocksComplete ? string.Empty : "+"),
                counts.Blocks > 0 ? "Bad" : "Neutral",
                string.Join(" · ", blockParts));
        }
        else
        {
            var caption = _metrics is { Status: ConnectorHookTotalsStatus.NoDatabase }
                ? "No audit database yet"
                : _metricsProblem ?? "Reading audit.db…";
            var zero = _metrics is { Status: ConnectorHookTotalsStatus.NoDatabase } ? "0" : "—";
            hooks.Set(scope is null ? $"Hook Calls ({roster} connector{(roster == 1 ? string.Empty : "s")})" : $"Hook Calls ({scope})", zero, "Neutral", caption);
            blocks.Set(scope is null ? "Blocks" : $"Blocks ({scope})", zero, "Neutral", caption);
        }

        // ---- Findings: the one unacknowledged-findings number the badge, the tray and Alerts share.
        var findings = FindingsTile;
        var service = Services.AlertCounts;
        if (service.HasData)
        {
            var counts = service.Current;
            var value = scope is null ? counts.Total : counts.TallyFor(Services.ConnectorScope.Allows).Total;
            var text = Count(value) + (counts.HasMore ? "+" : string.Empty);
            var tally = scope is null ? counts.Tally : counts.TallyFor(Services.ConnectorScope.Allows);
            var findingParts = new List<string> { $"C{tally.Critical} H{tally.High} M{tally.Medium} L{tally.Low}" };
            if (TopFinding(counts, scope) is { } top)
            {
                findingParts.Add($"top: {Shorten(top.Target ?? string.Empty, 18)} {top.Severity.ToString()[0]}");
            }

            if (scope is not null)
            {
                findingParts.Add($"fleet {AlertCountPresentation.Compact(counts)}");
            }

            findings.Set(
                scope is null ? "Findings" : $"Findings ({scope})",
                text,
                value > 0 ? "High" : "Neutral",
                string.Join(" · ", findingParts));
        }
        else
        {
            findings.Set(scope is null ? "Findings" : $"Findings ({scope})", "—", "Neutral", service.Unavailable ?? "Reading the alert queue…");
        }

        // ---- Guardrail: what config.yaml says, the Mac's "Current configuration".
        var guardrail = Services.Config.Config.Guardrail;
        var mode = GuardrailModeText();
        GuardrailTile.Set(
            mode is null ? "Guardrail" : $"Guardrail - {mode}",
            guardrail.Enabled ? "ON" : "OFF",
            guardrail.Enabled ? "Ok" : "Neutral",
            "Current configuration");

        RenderEnforcementUpdated();
    }

    /// <summary>"Updated just now" / "Updated 45s ago": the age of the audit counts, re-derived on each poll (no timer).</summary>
    private void RenderEnforcementUpdated()
    {
        EnforcementUpdatedText = _metricsAt is not { } at
            ? string.Empty
            : DateTimeOffset.UtcNow - at < JustNow ? "Updated just now" : $"Updated {Relative(at)}";
    }

    /// <summary>The mode the guardrail runs in: the gateway's own <c>policy_mode</c> when it answered, else config.yaml's for the primary connector.</summary>
    private string? GuardrailModeText()
    {
        if (_snapshot.Health?.Guardrail?.DetailString("policy_mode") is { Length: > 0 } live)
        {
            return live;
        }

        var configured = Services.Config.Config.Guardrail;
        var connector = Services.ConnectorScope.Current ?? configured.Connector ?? Services.Config.Config.Claw.Mode;
        return connector is { Length: > 0 } && configured.Connectors.TryGetValue(connector, out var settings) && settings.Mode is { Length: > 0 } mode
            ? mode
            : null;
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Cuts <paramref name="text"/> to <paramref name="max"/> characters with an ellipsis, as the TUI's tile details do.</summary>
    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>The highest-severity queued finding (the newest of equals) in scope: the TUI's "top: target S"; null when none has a target.</summary>
    private AlertQueueItem? TopFinding(AlertCounts counts, string? scope)
    {
        AlertQueueItem? best = null;
        foreach (var item in counts.Newest)
        {
            if (string.IsNullOrWhiteSpace(item.Target) || (scope is not null && !Services.ConnectorScope.Allows(item.Connector)))
            {
                continue;
            }

            if (best is null || item.Severity > best.Severity)
            {
                best = item;
            }
        }

        return best;
    }

    /// <summary>
    /// Reads the hook-call and block counts when they are due (<see cref="MetricsRefreshInterval"/>), or at once when forced. One read at a
    /// time; a failure keeps the tile honest ("—" and the reason) instead of showing the last number as current. Stoppable: the panel's
    /// activation token ends a running statement.
    /// </summary>
    internal async Task RefreshMetricsAsync(bool force, CancellationToken cancellationToken)
    {
        // A first block scan that has not caught up yet is read again soon (each read does a bounded slice); once it has, once a minute.
        var catchingUp = _metrics is { Status: ConnectorHookTotalsStatus.Ok, BlocksComplete: false };
        if (!force && !_metricsStamp.HasElapsed(catchingUp ? MetricsCatchUpInterval : MetricsRefreshInterval))
        {
            return;
        }

        if (Interlocked.Exchange(ref _metricsReading, 1) == 1)
        {
            return;
        }

        try
        {
            _metricsStamp = MonotonicStamp.Now();
            if (!catchingUp)
            {
                _ = RefreshSilentBypassAsync(cancellationToken);
            }

            _metrics = await _metricsReader.ReadAsync(MetricsTimeout, cancellationToken).ConfigureAwait(true);
            _metricsProblem = null;
            _metricsAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // The panel went away mid-read; the next activation reads again.
            _metricsStamp = MonotonicStamp.Never;
            return;
        }
#pragma warning disable CA1031 // A locked or half-written database degrades the tiles, not the panel.
        catch (Exception ex) when (ex is SqliteException or IOException or TimeoutException or InvalidOperationException)
#pragma warning restore CA1031
        {
            _metrics = null;
            _metricsProblem = ex is TimeoutException ? "audit.db did not answer in time" : "audit.db could not be read";
        }
        finally
        {
            _ = Interlocked.Exchange(ref _metricsReading, 0);
        }

        RenderEnforcementCards();
    }
}

/// <summary>
/// One interactive tile of the Enforcement card: a caption, a big coloured number, a caption and a chevron, as one button. It changes in
/// place (the tile object outlives every refresh), so hover and keyboard focus survive a poll.
/// </summary>
public sealed partial class EnforcementTile : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _value = "—";

    /// <summary>The tone of the number: Accent (the style's accent: a count that is neither good nor bad news), Bad (red), High (amber), Ok (green), Neutral (ink).</summary>
    [ObservableProperty]
    private string _toneKey = "Neutral";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    private string _caption = string.Empty;

    /// <param name="title">The caption until the first render gives it the connector count.</param>
    /// <param name="command">What a click does: open the panel the number comes from.</param>
    /// <param name="hint">What a screen reader says the button does (<c>AutomationProperties.HelpText</c>).</param>
    /// <param name="toolTip">The tooltip.</param>
    public EnforcementTile(string title, System.Windows.Input.ICommand command, string hint, string toolTip)
    {
        _title = title;
        Command = command;
        AutomationHint = hint;
        ToolTipText = toolTip;
    }

    public System.Windows.Input.ICommand Command { get; }

    public string AutomationHint { get; }

    public string ToolTipText { get; }

    /// <summary>"Hook Calls (1 connector): 43. Latest 500 audit events": the button's spoken name.</summary>
    public string AutomationName => $"{Title}: {Value}. {Caption}".TrimEnd('.', ' ') + ".";

    internal void Set(string title, string value, string toneKey, string caption)
    {
        Title = title;
        Value = value;
        ToneKey = toneKey;
        Caption = caption;
    }

    public override string ToString() => AutomationName;
}
