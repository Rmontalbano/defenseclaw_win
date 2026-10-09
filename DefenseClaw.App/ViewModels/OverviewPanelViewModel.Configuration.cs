using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>One line of the Configuration card: a setting and its value. <see cref="Alternate"/> paints the zebra stripe.</summary>
public sealed record ConfigRow
{
    public required string Label { get; init; }

    public required string Value { get; init; }

    /// <summary>Every other row of the card; set when the list is built, so a row that moves keeps the stripe of its new place.</summary>
    public bool Alternate { get; init; }

    /// <summary>Neutral, or Warn for a value that is not what was asked for (a hook fail mode that has drifted).</summary>
    public string ToneKey { get; init; } = "Neutral";

    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// The Configuration card (CUST-209): the Mac's zebra key/value list, four rows and "Show N more settings". It is built from what this
/// panel already holds (config.yaml's <see cref="ConfigDocument"/>, the gateway snapshot) plus ONE read-only <c>defenseclaw status --json</c>,
/// which is the only place the CLI says what each connector's hook fail mode <i>effectively</i> is, who decided it and where the sources
/// disagree, and what the deployment mode is. That read runs on the first visit, on Refresh and when the panel is opened after
/// <see cref="StatusRefreshInterval"/>; it is a CLI process, so it lands in the Activity panel like every command, and it never runs on a timer.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>Rows shown before "Show N more settings", the Mac's number.</summary>
    internal const int ConfigurationRowsShown = 4;

    /// <summary>How old <c>status --json</c> may be before opening the panel reads it again.</summary>
    internal static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>The argv of the one CLI read this card makes: read-only by <see cref="CommandTiers"/>, which a test holds.</summary>
    internal static readonly string[] StatusArgv = { "status", "--json" };

    private readonly List<ConfigRow> _allConfigurationRows = new();
    private DefenseClawStatus _status = DefenseClawStatus.Empty;
    private string? _statusProblem;
    private DateTimeOffset? _statusLoadedAt;
    private MonotonicStamp _statusStamp = MonotonicStamp.Never;
    private int _statusReading;

    /// <summary>The rows on screen: the first four, or all of them once expanded.</summary>
    public ObservableCollection<ConfigRow> ConfigurationRows { get; } = new();

    [ObservableProperty]
    private bool _configurationExpanded;

    [ObservableProperty]
    private string _configurationMoreText = string.Empty;

    [ObservableProperty]
    private bool _hasConfigurationOverflow;

    /// <summary>Where the fail-mode and deployment rows came from, or why they are config.yaml's alone: <c>from status --json, 3m ago</c>.</summary>
    [ObservableProperty]
    private string _configurationSourceNote = string.Empty;

    [RelayCommand]
    private void ToggleConfiguration()
    {
        ConfigurationExpanded = !ConfigurationExpanded;
        RenderConfigurationRows();
    }

    /// <summary>
    /// Reads <c>defenseclaw status --json</c> when it is due (or <paramref name="force"/>d). One at a time. A failure is data, not an
    /// exception: the card says the rows are config.yaml's alone, and keeps whatever an earlier read found.
    /// </summary>
    internal async Task RefreshStatusAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && !_statusStamp.HasElapsed(StatusRefreshInterval))
        {
            return;
        }

        if (Interlocked.Exchange(ref _statusReading, 1) == 1)
        {
            return;
        }

        try
        {
            _statusStamp = MonotonicStamp.Now();
            var invocation = await DiscoverCli.RunReadOnlyAsync(Services, StatusArgv, cancellationToken).ConfigureAwait(true);

            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                _statusProblem = $"status --json did not finish: {reason}";
            }
            else if (invocation.ExitCode != 0)
            {
                _statusProblem = $"status --json exited {invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "?"}";
            }
            else
            {
                _status = DefenseClawStatusReader.Parse(DiscoverCli.TrimToJson(DiscoverCli.Stdout(invocation)));
                _statusProblem = null;
                _statusLoadedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            _statusStamp = MonotonicStamp.Never;
            return;
        }
        catch (CliNotFoundException ex)
        {
            _statusProblem = $"'{ex.ExecutableName}' was not found, so the fail mode and deployment rows come from config.yaml alone";
        }
        catch (JsonException)
        {
            _statusProblem = "status --json did not return JSON";
        }
        catch (SecretInArgumentException ex)
        {
            _statusProblem = ex.Message;
        }
        finally
        {
            _ = Interlocked.Exchange(ref _statusReading, 0);
        }

        RebuildAfterStatus();
    }

    /// <summary>
    /// Takes a <c>status --json</c> read as the current one (what <see cref="RefreshStatusAsync"/> does with a run's output, and what a test
    /// hands in) and redraws everything that uses it: the connector table, the Configuration card and the Activity summary.
    /// </summary>
    internal void ApplyStatus(DefenseClawStatus status, DateTimeOffset? readAt = null)
    {
        ArgumentNullException.ThrowIfNull(status);

        _status = status;
        _statusProblem = null;
        _statusLoadedAt = readAt ?? DateTimeOffset.UtcNow;
        RebuildAfterStatus();
    }

    private void RebuildAfterStatus()
    {
        var snapshot = _snapshot;
        BuildServices(snapshot.Health);
        BuildConnectors(snapshot, snapshot.Health);
        BuildConfiguration();
        RenderActivitySummary();
    }

    /// <summary>Rebuilds the Configuration card from config.yaml, the snapshot, the last <c>status --json</c> and the scope. No I/O.</summary>
    internal void BuildConfiguration()
    {
        var scope = Services.ConnectorScope.Current;
        var rows = new List<(string Label, string Value, string Tone)>();

        if (scope is not null)
        {
            AddScopedRows(rows, scope);
            AddGlobalRows(rows, suffix: " (global)", scoped: true);
        }
        else
        {
            AddGlobalRows(rows, suffix: string.Empty, scoped: false);
        }

        _allConfigurationRows.Clear();
        for (var i = 0; i < rows.Count; i++)
        {
            _allConfigurationRows.Add(new ConfigRow { Label = rows[i].Label, Value = rows[i].Value, ToneKey = rows[i].Tone, Alternate = i % 2 == 1 });
        }

        ConfigurationSourceNote = _statusProblem is { Length: > 0 } problem
            ? problem
            : _statusLoadedAt is { } at ? $"Fail mode and deployment rows from defenseclaw status --json, read {Relative(at)}." : "Reading defenseclaw status --json…";

        RenderConfigurationRows();
    }

    private void RenderConfigurationRows()
    {
        var overflow = Math.Max(0, _allConfigurationRows.Count - ConfigurationRowsShown);
        HasConfigurationOverflow = overflow > 0;
        if (overflow == 0)
        {
            ConfigurationExpanded = false;
        }

        ConfigurationMoreText = ConfigurationExpanded ? "Show Fewer Settings" : $"Show {overflow} More Setting{(overflow == 1 ? string.Empty : "s")}";

        var wanted = ConfigurationExpanded ? _allConfigurationRows : _allConfigurationRows.Take(ConfigurationRowsShown).ToList();
        SyncByEquality(ConfigurationRows, wanted, static row => row.Label);
    }

    /// <summary>The connectors on this install: the gateway's roster, then config.yaml's, then the ones <c>status --json</c> adds.</summary>
    private List<string> Roster()
    {
        var names = new List<string>();
        foreach (var name in _snapshot.ActiveConnectors
                     .Concat(Services.Config.Config.Guardrail.Connectors.Keys)
                     .Concat(_status.Connectors.Select(static c => c.Name)))
        {
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private void AddGlobalRows(List<(string Label, string Value, string Tone)> rows, string suffix, bool scoped)
    {
        var config = Services.Config.Config;
        var roster = Roster();

        if (!scoped)
        {
            rows.Add(roster.Count > 1
                ? ("Agents", $"{roster.Count.ToString(CultureInfo.CurrentCulture)} active", "Neutral")
                : ("Agent", roster.Count == 1 ? roster[0] : "none configured", "Neutral"));
        }

        rows.Add(("Policy posture" + suffix, PolicyPosture(roster), "Neutral"));
        rows.Add(("Enforcement" + suffix, EnforcementLabel(roster), "Neutral"));

        if (!scoped)
        {
            foreach (var name in roster)
            {
                var (text, tone) = FailModeText(name);
                rows.Add((roster.Count > 1 ? $"Hook fail mode ({name})" : "Hook fail mode", text, tone));
            }

            rows.Add(("Guardrail", GuardrailText(config), "Neutral"));
        }

        // v8 has no global redaction switch: each route carries a profile, so this is the TUI's aggregate ("per-route · unredacted"), from the plan.
        rows.Add(("Redaction" + suffix, RedactionSummary, "Neutral"));

        rows.Add(("Deployment mode" + suffix, _status.DeploymentMode is { Length: > 0 } deployment ? deployment : "not set", "Neutral"));

        if (_status.Environment is { Length: > 0 } environment)
        {
            rows.Add(("Environment" + suffix, environment, "Neutral"));
        }

        if (_status.Scope is { Length: > 0 } statusScope)
        {
            rows.Add(("Scope" + suffix, statusScope, "Neutral"));
        }

        rows.Add(("AI discovery" + suffix, config.AiDiscovery.Enabled
            ? $"on · mode {config.AiDiscovery.Mode ?? "—"} · scan every {config.AiDiscovery.ScanIntervalMin.ToString(CultureInfo.CurrentCulture)} min"
            : "off", "Neutral"));

        var host = string.IsNullOrWhiteSpace(config.Gateway.Host) ? "127.0.0.1" : config.Gateway.Host.Trim();
        rows.Add(("Gateway API" + suffix, $"{host}:{Services.ApiPort.ToString(CultureInfo.InvariantCulture)}", "Neutral"));

        AddApplicationProtectionRow(rows, suffix);

        if (_status.SandboxAvailable is { } sandbox)
        {
            rows.Add(("Sandbox" + suffix, sandbox ? "available" : "not available", "Neutral"));
        }

        rows.Add(("Data directory" + suffix, DataDirectorySourceText.Length > 0 ? $"{DataDirectoryText}  ({DataDirectorySourceText})" : DataDirectoryText, "Neutral"));
        rows.Add(("Config file" + suffix, Services.Config.Path, "Neutral"));
        AddConfigReloadRow(rows, suffix);
    }

    private void AddScopedRows(List<(string Label, string Value, string Tone)> rows, string scope)
    {
        var connector = ConnectorRows.FirstOrDefault(r => string.Equals(r.Name, scope, StringComparison.OrdinalIgnoreCase));
        Services.Config.Config.Guardrail.Connectors.TryGetValue(scope, out var settings);
        var friendly = _status.Connector(scope)?.Friendly;

        rows.Add(("Connector", $"{(string.IsNullOrWhiteSpace(friendly) ? scope : friendly)} ({scope.ToLowerInvariant()})", "Neutral"));
        rows.Add(("Mode", connector?.Mode ?? settings?.Mode ?? "?", "Neutral"));
        rows.Add(("Rule pack", RulePackName(settings?.RulePackDir), "Neutral"));
        rows.Add(("Guardrail", Services.Config.Config.Guardrail.Enabled ? "enabled" : "disabled", "Neutral"));

        var (failMode, tone) = FailModeText(scope);
        rows.Add(("Hook fail mode", failMode, tone));
        rows.Add(("Status", connector?.StateText ?? "unknown", "Neutral"));
        rows.Add(("Last activity", connector is { LastActivity.Length: > 0 } ? connector.LastActivity.Replace("last activity ", string.Empty, StringComparison.Ordinal) : "none", "Neutral"));
    }

    /// <summary>
    /// The hook fail mode of one connector as a sentence, with the tone to draw it in. With <c>status --json</c>: the mode the hook effectively
    /// obeys, who decided it, what config.yaml asked for and where the sources disagree. Without it: what config.yaml or the gateway says, labelled
    /// as that (the hook may obey something else, which only the status read can tell).
    /// </summary>
    private (string Text, string Tone) FailModeText(string name)
    {
        if (_status.Connector(name)?.FailMode is { Effective.Length: > 0 } mode)
        {
            var parts = new List<string> { $"{mode.Effective} (effective" + (mode.Provenance is { Length: > 0 } provenance ? $", set by {provenance})" : ")") };
            if (mode.Configured is { Length: > 0 } configured && !string.Equals(configured, mode.Effective, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"config.yaml says {configured}");
            }

            if (mode.Drift.Count > 0)
            {
                parts.Add("drift: " + string.Join(", ", mode.Drift));
            }

            return (string.Join(" · ", parts), mode.HasDrift ? "Warn" : "Neutral");
        }

        Services.Config.Config.Guardrail.Connectors.TryGetValue(name, out var settings);
        _connectorModes.TryGetValue(name, out var runtime);
        var value = runtime?.HookFailMode ?? settings?.HookFailMode;
        return (value is { Length: > 0 } ? $"{value} ({(runtime?.HookFailMode is null ? "config.yaml" : "gateway")})" : "not set", "Neutral");
    }

    private string PolicyPosture(IReadOnlyList<string> roster)
    {
        var modes = roster.Select(ModeOf).Where(static m => m is { Length: > 0 }).Select(static m => m!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var packs = roster
            .Select(n => Services.Config.Config.Guardrail.Connectors.TryGetValue(n, out var s) ? RulePackName(s.RulePackDir) : "default")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roster.Count == 0 || modes.Count == 0)
        {
            return "not configured";
        }

        if (roster.Count == 1)
        {
            return $"{roster[0]}: {modes[0]} ({packs[0]})";
        }

        return modes.Count > 1 || packs.Count > 1
            ? "per-connector (see the connectors table)"
            : $"all connectors: {modes[0]} ({packs[0]})";
    }

    private string EnforcementLabel(IReadOnlyList<string> roster)
    {
        if (roster.Count == 0)
        {
            return "not configured";
        }

        if (roster.Count > 1)
        {
            return $"{roster.Count.ToString(CultureInfo.CurrentCulture)} connectors (hook observability)";
        }

        var name = roster[0].ToLowerInvariant();
        var mode = ModeOf(roster[0]) ?? "observe";
        return name is "openclaw" or "zeptoclaw" ? $"{name} proxy guardrail ({mode})" : $"{name} hook observability ({mode})";
    }

    /// <summary>The mode a connector runs in: the gateway's answer, else config.yaml's, else what <c>status --json</c> says.</summary>
    private string? ModeOf(string name)
    {
        if (_connectorModes.TryGetValue(name, out var runtime) && runtime.GuardrailMode is { Length: > 0 } live)
        {
            return live;
        }

        if (Services.Config.Config.Guardrail.Connectors.TryGetValue(name, out var settings) && settings.Mode is { Length: > 0 } configured)
        {
            return configured;
        }

        return _status.Connector(name)?.Mode is { Length: > 0 } fromStatus ? fromStatus : null;
    }

    private static string GuardrailText(DefenseClawConfig config) =>
        config.Guardrail.Enabled
            ? "enabled" + (config.Guardrail.ScannerMode is { Length: > 0 } scanner ? $" · scanner {scanner}" : string.Empty)
            : "disabled";

    /// <summary>The last segment of a rule-pack directory (<c>default</c> when none is named), as the table prints it.</summary>
    internal static string RulePackName(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return "default";
        }

        var trimmed = directory.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
    }
}
