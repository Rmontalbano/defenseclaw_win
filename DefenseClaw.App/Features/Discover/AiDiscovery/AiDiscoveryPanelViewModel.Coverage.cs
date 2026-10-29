using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.ViewModels;

/// <summary>One "plane" of the runtime coverage snapshot: what is being watched and whether it is.</summary>
public sealed record RuntimePlaneRow(string Name, string StateText, string StateKey, string? Reason, string? Mechanism)
{
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);

    public override string ToString() =>
        $"{Name} plane: {StateText}.{(HasReason ? " " + Reason : string.Empty)}";
}

/// <summary>One runtime finding (a process the runtime plane flagged).</summary>
public sealed record RuntimeFindingRow(
    string Severity,
    string SeverityKey,
    double? Score,
    string Process,
    long? Pid,
    string? Agent,
    string? Providers)
{
    public string ScoreDisplay => Score is { } s ? s.ToString("0.##", CultureInfo.InvariantCulture) : "—";

    public string PidDisplay => Pid is { } p ? p.ToString(CultureInfo.InvariantCulture) : "—";

    public override string ToString() =>
        $"{Severity} finding: {Process}, pid {PidDisplay}, score {ScoreDisplay}{(string.IsNullOrWhiteSpace(Agent) ? string.Empty : ", agent " + Agent)}";
}

/// <summary>The sidecar's live answer from <c>defenseclaw agent discovery status --json</c> (shape read from a live 0.8.10 run).</summary>
internal sealed record LiveDiscoveryStatus(
    bool Reachable,
    bool? Enabled,
    string? Error,
    bool? Drift,
    DateTimeOffset? ScannedAt,
    string? Source,
    string? Result,
    long? DurationMs,
    long? FilesScanned,
    long? TotalSignals,
    long? ActiveSignals,
    long? NewSignals,
    long? ChangedSignals,
    long? GoneSignals,
    long? Errors);

/// <summary>
/// AI Discovery: coverage-first framing, the enable/disable switch, and the runtime section.
/// <para>
/// <b>Coverage.</b> "Nothing found" and "nothing was looked at" are different results, so before any
/// component card the panel says what discovery is configured to look at (from the in-memory
/// config.yaml — no I/O), what it is not looking at, and what the last scan actually covered and when
/// (from one read-only <c>agent discovery status --json</c> call per load, cached with an "as of" time).
/// </para>
/// <para>
/// <b>Runtime.</b> The macOS companion's Runtime view reads <c>GET /api/v1/ai-usage/runtime</c>. On
/// DefenseClaw 0.8.10 the CLI has no <c>agent discovery runtime</c> group (its help lists only
/// disable, enable, scan, setup and status) and that route answers 404, so the section renders an explicit
/// "not supported by this DefenseClaw version" state instead of an error. If a newer gateway serves the
/// route, its snapshot (planes, counts, findings) is shown.
/// </para>
/// </summary>
public sealed partial class AiDiscoveryPanelViewModel
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private const int MaxRuntimeFindings = 50;

    public const string CoverageCaveat =
        "Nothing found does not mean nothing was checked, and it does not prove a machine is clean: only the sources listed here were examined.";

    private bool _loadRunning;
    private DateTimeOffset? _loadedAt;
    private bool _signalSourceAvailable;
    private DateTimeOffset? _signalCacheUpdatedAt;

    [ObservableProperty]
    private string _coverageBadgeText = "Checking…";

    [ObservableProperty]
    private string _coverageBadgeKey = "Neutral";

    [ObservableProperty]
    private string _coverageHeadline = string.Empty;

    [ObservableProperty]
    private string _coverageLooksAt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverageGaps))]
    private string? _coverageGaps;

    [ObservableProperty]
    private string _coverageLastScan = "Asking the gateway what its last scan covered…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverageNote))]
    private string? _coverageNote;

    [ObservableProperty]
    private bool _isLiveLoading;

    [ObservableProperty]
    private bool _isDiscoveryEnabledInConfig;

    [ObservableProperty]
    private string _toggleDiscoveryText = "Turn on AI Discovery…";

    [ObservableProperty]
    private bool _restartOnToggle = true;

    // ---- Runtime section -------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isRuntimeLoading;

    [ObservableProperty]
    private string _runtimeBadgeText = "Checking…";

    [ObservableProperty]
    private string _runtimeBadgeKey = "Neutral";

    [ObservableProperty]
    private string _runtimeTitle = "Runtime coverage";

    [ObservableProperty]
    private string _runtimeDetail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeSnapshot))]
    private bool _runtimeHasSnapshot;

    public bool HasCoverageGaps => !string.IsNullOrWhiteSpace(CoverageGaps);

    public bool HasCoverageNote => !string.IsNullOrWhiteSpace(CoverageNote);

    public bool HasRuntimeSnapshot => RuntimeHasSnapshot;

    public string CoverageCaveatText => CoverageCaveat;

    public ObservableCollection<RuntimePlaneRow> RuntimePlanes { get; } = new();

    public ObservableCollection<RuntimeFindingRow> RuntimeFindings { get; } = new();

    /// <summary>Esc closes the review dialog. True when it consumed the key. (The model inspector closes on the bubbling Esc, so an open drop-down keeps its own.)</summary>
    public bool HandleEscape() => Tuning.HandleEscape() || (Review.IsOpen && Review.HandleEscape());

    // ---- Enable / disable ------------------------------------------------------------------------

    /// <summary>
    /// <c>agent discovery enable|disable --yes [--no-restart [--no-scan]]</c> (flags from each verb's help). Both
    /// verbs restart the gateway by default; the "Restart the gateway now" box turns that off, in which case
    /// the change is only written to config.yaml and the running gateway keeps its old setting until it is
    /// restarted. <c>enable</c> keeps every other ai_discovery setting as it is (its knobs default to the existing
    /// config) and, by default, asks for a first scan once the gateway is back.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeData))]
    private void ToggleDiscovery()
    {
        var enable = !IsDiscoveryEnabledInConfig;
        var restart = RestartOnToggle;

        var argv = new List<string> { "agent", "discovery", enable ? "enable" : "disable", "--yes" };
        if (!restart)
        {
            argv.Add("--no-restart");
            if (enable)
            {
                // Without a restart the sidecar has no discovery service to answer a scan.
                argv.Add("--no-scan");
            }
        }

        var explanation = enable
            ? "Sets ai_discovery.enabled to true in config.yaml and leaves your other AI discovery settings (mode, " +
              "scan roots, cadence, detectors) as they are. " +
              (restart
                  ? "It then restarts the gateway so it starts the discovery service, and asks for a first scan."
                  : "The gateway is not restarted, so nothing starts scanning until it is restarted.")
            : "Sets ai_discovery.enabled to false in config.yaml. " +
              (restart
                  ? "It then restarts the gateway so the discovery service stops. Components already found stay listed until you clear them."
                  : "The gateway is not restarted, so it keeps scanning until it is restarted.");

        Review.Open(
            enable ? "Turn on AI Discovery?" : "Turn off AI Discovery?",
            explanation,
            new[]
            {
                new DiscoverStep(
                    argv,
                    enable ? "Enable the sidecar AI discovery service." : "Disable the sidecar AI discovery service.",
                    CommandTier.StateChanging,
                    TimeSpan.FromMinutes(5)),
            },
            result => AfterRunAsync(result, argv),
            restartsGateway: restart,
            primaryText: enable ? "Turn on" : "Turn off");
    }

    // ---- Coverage ----------------------------------------------------------------------------------

    private async Task LoadLiveStatusAsync(CancellationToken cancellationToken)
    {
        IsLiveLoading = true;
        LiveDiscoveryStatus? live = null;
        string? problem = null;

        try
        {
            // "status" is a read-only verb (CommandTiers): it runs without a confirmation.
            var invocation = await DiscoverCli.RunReadOnlyAsync(
                Services, new[] { "agent", "discovery", "status", "--json" }, cancellationToken).ConfigureAwait(true);

            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                problem = $"'defenseclaw agent discovery status' did not complete: {reason}";
            }
            else if (invocation.ExitCode != 0)
            {
                var firstLine = DiscoverCli.Stderr(invocation)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                problem = firstLine is null
                    ? $"'defenseclaw agent discovery status' exited {invocation.ExitCode}."
                    : $"'defenseclaw agent discovery status' exited {invocation.ExitCode}: {firstLine}";
            }
            else
            {
                live = ParseLiveStatus(DiscoverCli.Stdout(invocation), out problem);
            }
        }
        catch (CliNotFoundException ex)
        {
            problem = $"'defenseclaw' was not found on PATH: {ex.Message}";
        }
        finally
        {
            IsLiveLoading = false;
        }

        ApplyLiveStatus(live, problem);
    }

    /// <summary>The gateway's answer (or why there is none) reaches the coverage card and the line of counts above the lists.</summary>
    internal void ApplyLiveStatus(LiveDiscoveryStatus? live, string? problem)
    {
        _liveStatus = live;
        BuildCoverage(live, problem);
        RefreshHeader();
        UpdateEmptyState();
    }

    internal static LiveDiscoveryStatus? ParseLiveStatus(string stdout, out string? problem)
    {
        problem = null;

        try
        {
            using var document = JsonDocument.Parse(DiscoverCli.TrimToJson(stdout));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("live", out var live) ||
                live.ValueKind != JsonValueKind.Object)
            {
                problem = "'defenseclaw agent discovery status --json' did not return the expected 'live' section.";
                return null;
            }

            var summary = live.TryGetProperty("summary", out var s) ? s : default;
            return new LiveDiscoveryStatus(
                Reachable: JsonFlag(live, "reachable") ?? false,
                Enabled: JsonFlag(live, "enabled"),
                Error: JsonText(live, "error"),
                Drift: JsonFlag(root, "drift"),
                ScannedAt: ParseTime(JsonText(summary, "scanned_at")),
                Source: JsonText(summary, "source"),
                Result: JsonText(summary, "result"),
                DurationMs: JsonNumber(summary, "duration_ms"),
                FilesScanned: JsonNumber(summary, "files_scanned"),
                TotalSignals: JsonNumber(summary, "total_signals"),
                ActiveSignals: JsonNumber(summary, "active_signals"),
                NewSignals: JsonNumber(summary, "new_signals"),
                ChangedSignals: JsonNumber(summary, "changed_signals"),
                GoneSignals: JsonNumber(summary, "gone_signals"),
                Errors: JsonNumber(summary, "errors"));
        }
        catch (JsonException ex)
        {
            problem = $"'defenseclaw agent discovery status --json' did not return valid JSON: {ex.Message}";
            return null;
        }
    }

    private static bool? JsonFlag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => (bool?)null,
            }
            : (bool?)null;

    private static string? JsonText(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static long? JsonNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static DateTimeOffset? ParseTime(string? raw) =>
        raw is not null && DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Composes the coverage card from config.yaml (what discovery is set up to do) and, when the gateway
    /// answered, what its last scan actually did. <paramref name="live"/> null with no problem means the
    /// gateway has not been asked yet.
    /// </summary>
    private void BuildCoverage(LiveDiscoveryStatus? live, string? liveProblem)
    {
        var ai = Services.Config.Config.AiDiscovery;
        var configOn = ai.Enabled;
        IsDiscoveryEnabledInConfig = configOn;
        ToggleDiscoveryText = configOn ? "Turn off AI Discovery…" : "Turn on AI Discovery…";

        // ---- state badge and drift note
        string badgeText;
        string badgeKey;
        string? note = null;

        if (live is { Reachable: true })
        {
            var liveOn = live.Enabled == true;
            if (configOn && liveOn)
            {
                (badgeText, badgeKey) = ("On", "Ok");
            }
            else if (!configOn && !liveOn)
            {
                (badgeText, badgeKey) = ("Off", "Neutral");
            }
            else
            {
                (badgeText, badgeKey) = (configOn ? "On in config, off in gateway" : "Off in config, on in gateway", "Warn");
                note = configOn
                    ? "config.yaml has AI discovery on but the running gateway reports it off. Restarting the gateway applies the config."
                    : "config.yaml has AI discovery off but the running gateway is still running it. Restarting the gateway applies the config.";
            }

            if (!string.IsNullOrWhiteSpace(live.Error))
            {
                note = (note is null ? string.Empty : note + " ") + $"The gateway reported: {live.Error}";
            }
        }
        else if (live is not null || liveProblem is not null)
        {
            (badgeText, badgeKey) = configOn ? ("On in config, gateway not confirming", "Warn") : ("Off", "Neutral");
            note = live is { Error: { } gatewayError } && !string.IsNullOrWhiteSpace(gatewayError)
                ? $"The gateway did not answer ({gatewayError}), so the last-scan line below is from disk, if there is one."
                : liveProblem is null
                    ? "The gateway did not answer, so the last-scan line below is from disk, if there is one."
                    : $"{liveProblem} The last-scan line below is from disk, if there is one.";
        }
        else
        {
            (badgeText, badgeKey) = configOn ? ("On", "Neutral") : ("Off", "Neutral");
        }

        CoverageBadgeText = badgeText;
        CoverageBadgeKey = badgeKey;
        CoverageNote = note;

        // ---- what it looks at / does not
        if (configOn)
        {
            CoverageHeadline = $"AI discovery is on ({(string.IsNullOrWhiteSpace(ai.Mode) ? "default" : ai.Mode)} mode).";
            CoverageLooksAt = "Looks at: " + string.Join("; ", DescribeSources(ai)) + ".";

            var off = new List<string>();
            if (!ai.IncludeShellHistory) { off.Add("shell history"); }
            if (!ai.IncludePackageManifests) { off.Add("package manifests"); }
            if (!ai.IncludeEnvVarNames) { off.Add("environment variable names"); }
            if (!ai.IncludeNetworkDomains) { off.Add("network domains"); }

            CoverageGaps = off.Count == 0
                ? null
                : "Turned off, so it cannot find what only these would show: " + string.Join(", ", off) + ".";
        }
        else
        {
            CoverageHeadline = "AI discovery is off.";
            CoverageLooksAt = "Nothing is being scanned while it is off. Any components listed below are from the last scan before it was turned off.";
            CoverageGaps = null;
        }

        // ---- what the last scan actually did
        if (live is { ScannedAt: { } scannedAt })
        {
            CoverageLastScan = DescribeLastScan(live, scannedAt, ai);
        }
        else if (_signalCacheUpdatedAt is { } cachedAt)
        {
            CoverageLastScan =
                $"The last result on disk was written {cachedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}. " +
                "The gateway has not said what that scan covered.";
        }
        else
        {
            CoverageLastScan = live is null && liveProblem is null
                ? "Asking the gateway what its last scan covered…"
                : "No scan has been recorded yet.";
        }
    }

    private static IEnumerable<string> DescribeSources(AiDiscoverySection ai)
    {
        yield return ai.ProcessIntervalS > 0
            ? $"running processes (every {ai.ProcessIntervalS.ToString(CultureInfo.InvariantCulture)} s)"
            : "running processes";

        var roots = ai.ScanRoots.Count == 0 ? "no folders" : string.Join(", ", ai.ScanRoots);
        var cadence = ai.ScanIntervalMin > 0 ? $"a full scan every {ai.ScanIntervalMin.ToString(CultureInfo.InvariantCulture)} min" : "a periodic full scan";
        var limits = ai.MaxFilesPerScan > 0
            ? $", at most {ai.MaxFilesPerScan.ToString("N0", CultureInfo.InvariantCulture)} files of up to {FormatBytes(ai.MaxFileBytes)} each"
            : string.Empty;
        yield return $"files under {roots} ({cadence}{limits})";

        if (ai.IncludeShellHistory) { yield return "shell history"; }
        if (ai.IncludePackageManifests) { yield return "package manifests"; }
        if (ai.IncludeEnvVarNames) { yield return "environment variable names"; }
        if (ai.IncludeNetworkDomains) { yield return "network domains"; }
    }

    private static string DescribeLastScan(LiveDiscoveryStatus live, DateTimeOffset scannedAt, AiDiscoverySection ai)
    {
        static string N(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";

        var text =
            $"Last scan {scannedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}: " +
            $"source {live.Source ?? "unknown"}, result {live.Result ?? "unknown"}, {N(live.DurationMs)} ms, " +
            $"{N(live.FilesScanned)} files read, {N(live.TotalSignals)} signals ({N(live.ActiveSignals)} active; " +
            $"{N(live.NewSignals)} new, {N(live.ChangedSignals)} changed, {N(live.GoneSignals)} gone)";

        if (live.Errors is > 0)
        {
            text += $", {N(live.Errors)} errors";
        }

        text += ".";

        // A process-only poll reads no files; say so, so "0 files" is not mistaken for "nothing to read".
        if ((live.Source ?? string.Empty).Contains("process", StringComparison.OrdinalIgnoreCase) && live.FilesScanned is 0)
        {
            text += " That was a process-list check, which reads no files" +
                (ai.ScanIntervalMin > 0
                    ? $"; the full file scan runs every {ai.ScanIntervalMin.ToString(CultureInfo.InvariantCulture)} min."
                    : ".");
        }

        return text;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{(bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture)} MiB",
        >= 1024 => $"{(bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KiB",
        _ => $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes",
    };

    /// <summary>
    /// Says why the view on screen shows nothing. The Products view can be empty because discovery is off, no scan has been
    /// recorded, the latest scan found no products (only models, perhaps) or the search hides every card; the Models view for the same
    /// reasons, or because the filters do.
    /// </summary>
    private void UpdateEmptyState()
    {
        HasNoComponents = false;
        HasNoModels = false;
        ShowModelTable = false;
        ShowNoMatch = false;
        EmptyTitle = string.Empty;
        EmptyDetail = string.Empty;

        if (IsModelsView)
        {
            if (_allModels.Count == 0)
            {
                HasNoModels = true;
                (EmptyTitle, EmptyDetail) = NothingFound(
                    "No local models identified",
                    "The latest scan did not identify a local AI model: no model file in the folders it examined and no local model " +
                    "server listing any. That is not the same as none being installed; the Coverage card above lists exactly what it examined.");
            }
            else if (!ModelsView.Cast<object>().Any())
            {
                ShowNoMatch = true;
                NoMatchTitle = "No model matches";
                var hints = new List<string>();
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    hints.Add("clear or change the search text above");
                }

                if (HasActiveModelFilters)
                {
                    hints.Add("reset the filters");
                }

                if (_hiddenByScope > 0)
                {
                    hints.Add("turn on Show all models");
                }

                NoMatchDetail = hints.Count == 0 ? "Nothing in the list passes the current view." : "To see more, " + string.Join(", or ", hints) + ".";
            }
            else
            {
                ShowModelTable = true;
            }

            return;
        }

        if (_allCards.Count == 0)
        {
            HasNoComponents = true;
            (EmptyTitle, EmptyDetail) = _allModels.Count > 0
                ? ("No AI products detected",
                    $"The latest scan found {Plural(_allModels.Count, "local model")} and no AI products. The models are listed under Models.")
                : NothingFound(
                    "Nothing detected in the latest scan",
                    "The scan ran and found no AI components. That is not the same as nothing having been looked at: " +
                    "the Coverage card above lists exactly what it examined.");
        }
        else if (!CardsView.Cast<object>().Any())
        {
            ShowNoMatch = true;
            NoMatchTitle = "No component matches the search";
            NoMatchDetail = "Clear or change the search text above.";
        }
    }

    /// <summary>The reason for an empty list when the cause is the state of discovery itself: it is off, or it has not written a result; otherwise the caller's words for "it ran and found none".</summary>
    private (string Title, string Detail) NothingFound(string foundNothingTitle, string foundNothingDetail)
    {
        if (!IsDiscoveryEnabledInConfig)
        {
            return ("AI Discovery is turned off", "Nothing is being scanned, so there is nothing to list. Turn it on above, then run a scan.");
        }

        if (!_signalSourceAvailable)
        {
            return (
                "No scan result on disk yet",
                "AI Discovery is on, but no scan has written a result yet. Run a scan above, or wait for the gateway's next scheduled one.");
        }

        return (foundNothingTitle, foundNothingDetail);
    }

    // ---- Runtime -------------------------------------------------------------------------------------

    private async Task LoadRuntimeAsync(CancellationToken cancellationToken)
    {
        IsRuntimeLoading = true;

        try
        {
            // A plain GET: no CLI, nothing written. 404/405 is how a gateway without this route answers.
            var result = await Services.Gateway
                .GetRawJsonAsync("api/v1/ai-usage/runtime", requiresAuth: true, cancellationToken)
                .ConfigureAwait(true);

            try
            {
                ApplyRuntime(result);
            }
            finally
            {
                result.Value?.Dispose();
            }
        }
#pragma warning disable CA1031 // The runtime section is supplementary; any failure becomes its own state, never the page's.
        catch (Exception ex)
        {
            Trace.TraceError($"Runtime coverage read failed: {ex}");
            SetRuntime("Error", "Bad", "Runtime coverage could not be read", ex.Message, snapshot: false);
        }
#pragma warning restore CA1031
        finally
        {
            IsRuntimeLoading = false;
        }
    }

    /// <summary>
    /// Turns the gateway's answer into the runtime section's state. Two worlds meet here: 0.8.10, whose gateway has no
    /// such route at all (no <c>/api/v1/ai-usage/runtime</c> in its binary; the route is in upstream main since
    /// c97e652fc7, 2026-09-11, and in no release so far), so the answer is a 404 and the section says "not supported";
    /// and a newer gateway whose reply is <c>aiRuntimeResponse</c> (internal/gateway/ai_runtime_api.go:31-97 upstream):
    /// <c>enabled</c>, <c>scanned_at</c>, <c>findings[]</c>, <c>planes[]</c>, the process/connection counts, and
    /// <c>degraded</c> with <c>degraded_reasons[]</c>.
    /// </summary>
    internal void ApplyRuntime(GatewayResult<JsonDocument> result)
    {
        RuntimePlanes.Clear();
        RuntimeFindings.Clear();

        switch (result.Status)
        {
            case GatewayStatus.Ok when result.Value is { } document:
                ApplyRuntimeSnapshot(document.RootElement);
                break;

            case GatewayStatus.Unreachable:
                SetRuntime(
                    "Unknown",
                    "Warn",
                    "Runtime coverage: gateway not reachable",
                    "The gateway is not answering, so this app cannot tell what the runtime plane is watching. " +
                    "That is not a clean-host result.",
                    snapshot: false);
                break;

            case GatewayStatus.Unauthorized:
                SetRuntime(
                    "Unknown",
                    "Warn",
                    "Runtime coverage: token not accepted",
                    "The gateway refused this app's API token, so runtime coverage cannot be read. That is not a clean-host result.",
                    snapshot: false);
                break;

            case GatewayStatus.NotConnected:
                SetRuntime(
                    "Not connected",
                    "Neutral",
                    "Runtime coverage: not connected",
                    "The gateway is running but the runtime coverage service is not wired up.",
                    snapshot: false);
                break;

            default:
                if (result.HttpStatusCode is 404 or 405)
                {
                    SetRuntime(
                        "Not supported",
                        "Neutral",
                        "Runtime coverage is not supported by this DefenseClaw version",
                        "This version's gateway does not serve /api/v1/ai-usage/runtime, and its command line has no " +
                        "'agent discovery runtime' commands. That is a limit of the version, not a problem with this machine. " +
                        "It also means nothing is watching for shadow AI network traffic or unattributed agent actions, so the " +
                        "absence of runtime findings says nothing about the host. A newer DefenseClaw adds this; the section " +
                        "fills in by itself when the gateway supports it.",
                        snapshot: false);
                }
                else
                {
                    SetRuntime(
                        "Error",
                        "Bad",
                        "Runtime coverage could not be read",
                        result.ErrorMessage ?? "The gateway returned an error.",
                        snapshot: false);
                }

                break;
        }
    }

    private void ApplyRuntimeSnapshot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || JsonFlag(root, "enabled") is not { } enabled)
        {
            SetRuntime(
                "Incomplete",
                "Warn",
                "Runtime coverage response is incomplete",
                "The gateway answered, but its reply is missing the fields this app reads (enabled, planes, findings). " +
                "Nothing here should be read as a clean host.",
                snapshot: false);
            return;
        }

        if (!enabled)
        {
            SetRuntime(
                "Off",
                "Neutral",
                "Runtime coverage is turned off",
                "Nothing is watching for shadow AI network traffic or unattributed agent actions on this gateway.",
                snapshot: false);
            return;
        }

        if (root.TryGetProperty("planes", out var planes) && planes.ValueKind == JsonValueKind.Array)
        {
            foreach (var plane in planes.EnumerateArray())
            {
                var available = JsonFlag(plane, "available") ?? false;
                var running = JsonFlag(plane, "running") ?? false;
                var (state, key) = running ? ("up", "Ok") : available ? ("idle", "Warn") : ("blind", "Bad");
                RuntimePlanes.Add(new RuntimePlaneRow(
                    JsonText(plane, "name") ?? JsonText(plane, "plane") ?? "plane",
                    state,
                    key,
                    running ? null : JsonText(plane, "reason"),
                    JsonText(plane, "mechanism")));
            }
        }

        var findingCount = 0;
        if (root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
        {
            var rows = new List<RuntimeFindingRow>();
            foreach (var finding in findings.EnumerateArray())
            {
                findingCount++;
                var severity = JsonText(finding, "severity") ?? "unknown";
                var providers = finding.TryGetProperty("providers", out var p) && p.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", p.EnumerateArray()
                        // aiRuntimeProvider: {hostname, address?, port?, category?, confidence?, attribution_source?}. The
                        // hostname can be empty when a peer was attributed by address alone, so the address is the fallback.
                        .Select(item => item.ValueKind == JsonValueKind.Object
                            ? JsonText(item, "hostname") ?? JsonText(item, "host") ?? JsonText(item, "name") ?? JsonText(item, "address")
                            : item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                        .Where(v => !string.IsNullOrWhiteSpace(v)))
                    : null;

                rows.Add(new RuntimeFindingRow(
                    severity,
                    severity.ToLowerInvariant() switch
                    {
                        "critical" => "Critical",
                        "high" => "High",
                        "medium" => "Medium",
                        _ => "Neutral",
                    },
                    finding.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : null,
                    JsonText(finding, "process") ?? "(unknown process)",
                    JsonNumber(finding, "pid"),
                    JsonText(finding, "agent_name"),
                    providers));
            }

            foreach (var row in rows
                .OrderBy(r => SeverityRank(r.SeverityKey))
                .ThenByDescending(r => r.Score ?? 0)
                .Take(MaxRuntimeFindings))
            {
                RuntimeFindings.Add(row);
            }
        }

        static string N(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "?";

        var polled = ParseTime(JsonText(root, "scanned_at"));
        var degraded = JsonFlag(root, "degraded") == true;

        // One entry per plane the platform supports but that is not running; without it "Degraded" says nothing about why.
        var reasons = root.TryGetProperty("degraded_reasons", out var reasonList) && reasonList.ValueKind == JsonValueKind.Array
            ? reasonList.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                .Select(v => v.GetString()!.Trim())
                .ToArray()
            : Array.Empty<string>();

        var detail =
            $"{findingCount.ToString("N0", CultureInfo.InvariantCulture)} finding{(findingCount == 1 ? string.Empty : "s")}; " +
            $"{N(JsonNumber(root, "processes_observed"))} processes observed" +
            (JsonNumber(root, "processes_skipped") is > 0 ? $" ({N(JsonNumber(root, "processes_skipped"))} skipped)" : string.Empty) +
            $"; {N(JsonNumber(root, "connections_observed"))} connections" +
            (JsonNumber(root, "connections_unattributed") is > 0 ? $" ({N(JsonNumber(root, "connections_unattributed"))} not attributed to a process)" : string.Empty) +
            (polled is { } at ? $"; polled {at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}" : string.Empty) +
            "." +
            (degraded && reasons.Length > 0 ? $" Degraded: {string.Join("; ", reasons)}." : string.Empty) +
            " No findings is not the same as a clean host: a plane that is idle or blind saw nothing at all.";

        SetRuntime(
            degraded ? "Degraded" : "Watching",
            degraded ? "Warn" : "Ok",
            degraded ? "Runtime coverage is degraded" : "Runtime coverage",
            detail,
            snapshot: true);
    }

    private static int SeverityRank(string key) => key switch
    {
        "Critical" => 0,
        "High" => 1,
        "Medium" => 2,
        _ => 3,
    };

    private void SetRuntime(string badge, string key, string title, string detail, bool snapshot)
    {
        RuntimeBadgeText = badge;
        RuntimeBadgeKey = key;
        RuntimeTitle = title;
        RuntimeDetail = detail;
        RuntimeHasSnapshot = snapshot;
    }
}
