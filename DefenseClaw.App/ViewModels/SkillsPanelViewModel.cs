using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Skills panel: installed skills (<c>GET /skills</c>) plus the
/// skill-shaped slice of the enforcement lists (<c>GET /enforce/blocked</c>,
/// <c>GET /enforce/allowed</c>), with block/allow mutations that shell out to
/// <c>defenseclaw skill block|allow</c> through a confirm-first dialog.
/// <para>
/// <b>Live-verified behaviour.</b> On this 0.8.7 install <c>/skills</c> answers
/// <c>{"error":"gateway: not connected"}</c> until a fleet connector is wired up. That is
/// this app's normal standalone mode, not a failure, so <see cref="GovernListState.NotConnected"/>
/// renders a friendly explanation rather than an error banner.
/// </para>
/// <para>
/// <see cref="SkillEntry"/> is an UNVERIFIED guess at the payload shape - the real one has
/// never been observed. Every row keeps the exact JSON element it was parsed from (via
/// <see cref="GatewayClient.GetRawJsonAsync"/>, the same call used for the typed list) so an
/// unexpected shape still renders something behind the row's expander.
/// </para>
/// </summary>
public sealed partial class SkillsPanelViewModel : PanelViewModelBase
{
    private const string SkillsPath = "skills";
    private const string EnforcementKind = "skill";
    private const string AllConnectorsLabel = "All configured connectors";

    private static readonly JsonSerializerOptions RowJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadingState))]
    [NotifyPropertyChangedFor(nameof(IsNotConnectedState))]
    [NotifyPropertyChangedFor(nameof(IsEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsErrorState))]
    [NotifyPropertyChangedFor(nameof(IsLoadedState))]
    [NotifyPropertyChangedFor(nameof(IsNotLoading))]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    [NotifyPropertyChangedFor(nameof(BannerSeverity))]
    private GovernListState _state = GovernListState.Loading;

    [ObservableProperty] private string? _bannerMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty] private string? _selectedConnector;
    [ObservableProperty] private string _reason = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoBlockedItems))]
    private bool _hasBlockedItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoAllowedItems))]
    private bool _hasAllowedItems;

    // Null while /enforce/blocked (resp. /allowed) answers; otherwise "unavailable (<reason>)".
    // Kept apart from "empty": a list that could not be read is not a list with nothing in it.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockedUnavailable))]
    [NotifyPropertyChangedFor(nameof(NoBlockedItems))]
    [NotifyPropertyChangedFor(nameof(BlockedHeader))]
    private string? _blockedUnavailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAllowedUnavailable))]
    [NotifyPropertyChangedFor(nameof(NoAllowedItems))]
    [NotifyPropertyChangedFor(nameof(AllowedHeader))]
    private string? _allowedUnavailable;

    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmHeading = string.Empty;
    [ObservableProperty] private string _confirmCommandText = string.Empty;

    [ObservableProperty] private bool _isResultOpen;
    [ObservableProperty] private string _resultTitle = string.Empty;
    [ObservableProperty] private string _resultMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;

    private IReadOnlyList<string>? _pendingArgv;
    private Func<Task>? _pendingAction;

    public SkillsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Skills";

    public override string Description => "Installed skills, scan results and enforcement overrides.";

    /// <summary>Every skill from the last successful <c>GET /skills</c>.</summary>
    public ObservableCollection<SkillRow> Skills { get; } = new();

    /// <summary>Skill-kind rows from <c>GET /enforce/blocked</c>.</summary>
    public ObservableCollection<EnforcementRow> Blocked { get; } = new();

    /// <summary>Skill-kind rows from <c>GET /enforce/allowed</c>.</summary>
    public ObservableCollection<EnforcementRow> Allowed { get; } = new();

    /// <summary>Connector scope choices for the toolbar combo, plus mutation <c>--connector</c>.</summary>
    public ObservableCollection<string> Connectors { get; } = new();

    public bool IsLoadingState => State == GovernListState.Loading;

    public bool IsNotConnectedState => State == GovernListState.NotConnected;

    public bool IsEmptyState => State == GovernListState.Empty;

    public bool IsErrorState => State == GovernListState.Error;

    public bool IsLoadedState => State == GovernListState.Loaded;

    public bool IsNotLoading => State != GovernListState.Loading;

    public bool IsIdle => !IsBusy;

    /// <summary>True only when the list was read and is empty — never when it could not be read.</summary>
    public bool NoBlockedItems => !HasBlockedItems && !HasBlockedUnavailable;

    public bool NoAllowedItems => !HasAllowedItems && !HasAllowedUnavailable;

    public bool HasBlockedUnavailable => !string.IsNullOrEmpty(BlockedUnavailable);

    public bool HasAllowedUnavailable => !string.IsNullOrEmpty(AllowedUnavailable);

    /// <summary>"Blocked (3)", or "Blocked (unavailable)" — a count of 0 would claim a read that never happened.</summary>
    public string BlockedHeader => HasBlockedUnavailable ? "Blocked (unavailable)" : $"Blocked ({Blocked.Count})";

    public string AllowedHeader => HasAllowedUnavailable ? "Allowed (unavailable)" : $"Allowed ({Allowed.Count})";

    public bool ShowBanner => State is GovernListState.NotConnected or GovernListState.Empty or GovernListState.Error;

    public InfoBarSeverity BannerSeverity => State == GovernListState.Error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BuildConnectorList();
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private void BlockSkill(SkillRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = NameOf(row);
        var connector = EffectiveConnector(row.Connector);
        var argv = BuildArgv("skill", "block", name, connector);
        BeginConfirm($"Block skill “{name}” for {ScopeText(connector)}?", argv, () => RunMutationAsync(argv, $"Blocked “{name}”."));
    }

    [RelayCommand]
    private void AllowSkill(SkillRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = NameOf(row);
        var connector = EffectiveConnector(row.Connector);
        var argv = BuildArgv("skill", "allow", name, connector);
        BeginConfirm($"Allow skill “{name}” for {ScopeText(connector)}?", argv, () => RunMutationAsync(argv, $"Allowed “{name}”."));
    }

    [RelayCommand]
    private async Task ConfirmYesAsync()
    {
        IsConfirmOpen = false;
        var action = _pendingAction;
        _pendingAction = null;
        _pendingArgv = null;

        if (action is not null)
        {
            await action().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ConfirmNo()
    {
        IsConfirmOpen = false;
        _pendingAction = null;
        _pendingArgv = null;
    }

    private static string NameOf(SkillRow row) => string.IsNullOrWhiteSpace(row.Name) ? row.DisplayName : row.Name;

    /// <summary>
    /// The connector a row-level command should name: the row's own when it has one, since the
    /// operator clicked a specific connector's skill, then the toolbar's, then none. With none,
    /// <c>skill block|allow</c> is bare, which per the CLI help acts on every configured
    /// connector's copies — see <see cref="ScopeText"/>, which the confirm heading uses to say so.
    /// </summary>
    private string? EffectiveConnector(string? rowConnector)
    {
        if (!string.IsNullOrWhiteSpace(rowConnector))
        {
            return rowConnector.Trim();
        }

        return !string.IsNullOrWhiteSpace(SelectedConnector) && !string.Equals(SelectedConnector, AllConnectorsLabel, StringComparison.Ordinal)
            ? SelectedConnector
            : null;
    }

    private static string ScopeText(string? connector) =>
        connector is null ? "ALL configured connectors" : $"connector “{connector}”";

    private List<string> BuildArgv(string group, string verb, string name, string? connector)
    {
        var argv = new List<string> { group, verb, name };

        if (!string.IsNullOrWhiteSpace(Reason))
        {
            argv.Add("--reason");
            argv.Add(Reason.Trim());
        }

        if (connector is not null)
        {
            argv.Add("--connector");
            argv.Add(connector);
        }

        return argv;
    }

    private void BeginConfirm(string heading, IReadOnlyList<string> argv, Func<Task> action)
    {
        ConfirmHeading = heading;
        ConfirmCommandText = "defenseclaw " + string.Join(' ', argv.Select(QuoteForDisplay));
        _pendingArgv = argv;
        _pendingAction = action;
        IsConfirmOpen = true;
    }

    private static string QuoteForDisplay(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private async Task RunMutationAsync(IReadOnlyList<string> argv, string successMessage)
    {
        IsBusy = true;
        try
        {
            var invocation = await Services.Cli.RunAsync(argv).ConfigureAwait(true);
            if (invocation.ExitCode == 0)
            {
                ShowResult("Done", successMessage, InfoBarSeverity.Success);
            }
            else
            {
                var errorLine = invocation.OutputLines.LastOrDefault(l => l.Stream == CliStream.StandardError);
                var detail = invocation.FailureReason ?? errorLine?.Text ?? $"Exit code {invocation.ExitCode?.ToString() ?? "unknown"}.";
                ShowResult("Command failed", detail, InfoBarSeverity.Error);
            }
        }
        // CliRunner throws these two synchronously, before any process exists. Uncaught, they
        // reach the dispatcher's fault handler and replace the dashboard with an error dialog;
        // here they are just a failed command with a reason.
        catch (CliNotFoundException ex)
        {
            ShowResult("Command failed", $"The defenseclaw CLI could not be found. {ex.Message}", InfoBarSeverity.Error);
        }
        catch (SecretInArgumentException ex)
        {
            ShowResult("Command refused", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private void ShowResult(string title, string message, InfoBarSeverity severity)
    {
        ResultTitle = title;
        ResultMessage = message;
        ResultSeverity = severity;
        IsResultOpen = true;
    }

    private void BuildConnectorList()
    {
        Connectors.Clear();
        Connectors.Add(AllConnectorsLabel);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var config = Services.Config.Config;

        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var trimmed = name.Trim();
            if (seen.Add(trimmed))
            {
                Connectors.Add(trimmed);
            }
        }

        Add(config.Claw.Mode);
        Add(config.Guardrail.Connector);
        foreach (var key in config.Guardrail.Connectors.Keys)
        {
            Add(key);
        }

        SelectedConnector = AllConnectorsLabel;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var skills = await Services.Gateway.GetRawJsonAsync(SkillsPath, cancellationToken: cancellationToken).ConfigureAwait(true);
            ApplySkills(skills);

            var blocked = await Services.Gateway.GetEnforceBlockedAsync(cancellationToken).ConfigureAwait(true);
            BlockedUnavailable = ApplyEnforcement(Blocked, blocked);
            HasBlockedItems = Blocked.Count > 0;
            OnPropertyChanged(nameof(BlockedHeader));

            var allowed = await Services.Gateway.GetEnforceAllowedAsync(cancellationToken).ConfigureAwait(true);
            AllowedUnavailable = ApplyEnforcement(Allowed, allowed);
            HasAllowedItems = Allowed.Count > 0;
            OnPropertyChanged(nameof(AllowedHeader));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySkills(GatewayResult<JsonDocument> result)
    {
        Skills.Clear();

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                using (var document = result.Value)
                {
                    foreach (var row in ParseRows(document!.RootElement))
                    {
                        Skills.Add(row);
                    }
                }

                State = Skills.Count == 0 ? GovernListState.Empty : GovernListState.Loaded;
                BannerMessage = Skills.Count == 0 ? "No skills reported for the configured connector(s)." : null;
                break;

            case GatewayStatus.NotConnected:
                result.Value?.Dispose();
                State = GovernListState.NotConnected;
                BannerMessage =
                    "Skill governance data appears here once a fleet connector is wired up. " +
                    "This install is running standalone (no OpenClaw/ZeptoClaw upstream) - that is a normal, supported mode, not an error.";
                break;

            case GatewayStatus.Unauthorized:
                result.Value?.Dispose();
                State = GovernListState.Error;
                BannerMessage = $"The gateway rejected the request: no usable bearer token via {Services.Token.VariableName}.";
                break;

            case GatewayStatus.Unreachable:
                result.Value?.Dispose();
                State = GovernListState.Error;
                BannerMessage = "The gateway is not reachable. " + (result.ErrorMessage ?? string.Empty);
                break;

            default:
                result.Value?.Dispose();
                State = GovernListState.Error;
                BannerMessage = result.ErrorMessage ?? "Skills could not be read.";
                break;
        }
    }

    /// <summary>
    /// Fills <paramref name="target"/> from one enforcement list. Returns null when the list was
    /// read (empty or not), or "unavailable (&lt;reason&gt;)" when it was not — 401, gateway down,
    /// not connected — in which case the rows are cleared too, because rows from an earlier read
    /// would offer actions on entries nobody can currently see. The caller shows the reason where
    /// "No blocked skills." would otherwise have said the opposite.
    /// </summary>
    private string? ApplyEnforcement(ObservableCollection<EnforcementRow> target, GatewayResult<IReadOnlyList<EnforcementEntry>> result)
    {
        target.Clear();

        if (!result.IsOk || result.Value is null)
        {
            return $"unavailable ({DescribeEnforcementFailure(result.Status, result.ErrorMessage)})";
        }

        foreach (var entry in result.Value)
        {
            // The kind field is an unverified guess too; entries that omit it are shown
            // here rather than silently dropped from every panel.
            if (entry.Kind is not null && !string.Equals(entry.Kind, EnforcementKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            target.Add(new EnforcementRow
            {
                DisplayName = entry.DisplayName,
                Kind = entry.Kind,
                Reason = entry.Reason,
                Scope = entry.Scope,
                Connector = entry.Connector,
            });
        }

        return null;
    }

    /// <summary>Same wording the Overview panel uses for the same failures.</summary>
    private string DescribeEnforcementFailure(GatewayStatus status, string? message) => status switch
    {
        GatewayStatus.NotConnected => "the enforcement subsystem is not connected on this install",
        GatewayStatus.Unauthorized => $"the gateway needs a bearer token; none was found via {Services.Token.VariableName}",
        GatewayStatus.Unreachable => "the gateway is not answering",
        _ => message ?? "the gateway did not return the list",
    };

    private static IEnumerable<SkillRow> ParseRows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var element in root.EnumerateArray())
        {
            SkillEntry? typed = null;
            try
            {
                typed = element.Deserialize<SkillEntry>(RowJsonOptions);
            }
            catch (JsonException)
            {
                // Shape drifted from the guessed model; the row still renders from raw JSON.
            }

            yield return new SkillRow
            {
                DisplayName = typed?.DisplayName ?? BestEffortName(element),
                Name = typed?.Name,
                Status = typed?.Status,
                Severity = typed?.Severity,
                Connector = typed?.Connector,
                Enabled = typed?.Enabled,
                Blocked = typed?.Blocked,
                RawJson = FormatJson(element),
            };
        }
    }

    private static string BestEffortName(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "name", "id", "path" })
            {
                if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }

        return "(unnamed)";
    }

    private static string FormatJson(JsonElement element)
    {
        try
        {
            return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return element.GetRawText();
        }
    }

    public enum GovernListState
    {
        Loading,
        NotConnected,
        Empty,
        Error,
        Loaded,
    }

    public sealed class SkillRow
    {
        public required string DisplayName { get; init; }

        public string? Name { get; init; }

        public string? Status { get; init; }

        public string? Severity { get; init; }

        public string? Connector { get; init; }

        public bool? Enabled { get; init; }

        public bool? Blocked { get; init; }

        public string RawJson { get; init; } = string.Empty;
    }

    public sealed class EnforcementRow
    {
        public required string DisplayName { get; init; }

        public string? Kind { get; init; }

        public string? Reason { get; init; }

        public string? Scope { get; init; }

        public string? Connector { get; init; }
    }
}
