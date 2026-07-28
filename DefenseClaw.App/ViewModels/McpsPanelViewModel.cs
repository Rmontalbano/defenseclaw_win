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
/// View-model for the MCPs panel: configured MCP servers (<c>GET /mcps</c>) plus the
/// mcp-shaped slice of the enforcement lists, with block/allow/set/unset mutations that
/// shell out to <c>defenseclaw mcp ...</c> through a confirm-first dialog.
/// <para>
/// <b>Live-verified behaviour.</b> Unlike <c>/skills</c>, <c>/mcps</c> answers a real
/// <c>[]</c> on this install (empty configuration, not a disconnected subsystem) - so
/// <see cref="GovernListState.Empty"/> and <see cref="GovernListState.NotConnected"/> are
/// kept distinct and read different banners.
/// </para>
/// <para>
/// <see cref="McpEntry"/> is an UNVERIFIED guess at the payload shape. Every row keeps the
/// exact JSON element it was parsed from (via <see cref="GatewayClient.GetRawJsonAsync"/>)
/// so an unexpected shape still renders something behind the row's expander.
/// </para>
/// </summary>
public sealed partial class McpsPanelViewModel : PanelViewModelBase
{
    private const string McpsPath = "mcps";
    private const string EnforcementKind = "mcp";
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

    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmHeading = string.Empty;
    [ObservableProperty] private string _confirmCommandText = string.Empty;

    [ObservableProperty] private bool _isResultOpen;
    [ObservableProperty] private string _resultTitle = string.Empty;
    [ObservableProperty] private string _resultMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;

    // "Set MCP server" mini form.
    [ObservableProperty] private bool _isSetFormOpen;
    [ObservableProperty] private string _setName = string.Empty;
    [ObservableProperty] private string _setCommand = string.Empty;
    [ObservableProperty] private string _setArgs = string.Empty;
    [ObservableProperty] private string _setUrl = string.Empty;
    [ObservableProperty] private string _setTransport = string.Empty;
    [ObservableProperty] private string _setEnv = string.Empty;
    [ObservableProperty] private bool _setSkipScan;

    private Func<Task>? _pendingAction;

    public McpsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "MCPs";

    public override string Description => "Configured MCP servers, their transports and scan results.";

    public ObservableCollection<McpRow> Mcps { get; } = new();

    public ObservableCollection<EnforcementRow> Blocked { get; } = new();

    public ObservableCollection<EnforcementRow> Allowed { get; } = new();

    public ObservableCollection<string> Connectors { get; } = new();

    public bool IsLoadingState => State == GovernListState.Loading;

    public bool IsNotConnectedState => State == GovernListState.NotConnected;

    public bool IsEmptyState => State == GovernListState.Empty;

    public bool IsErrorState => State == GovernListState.Error;

    public bool IsLoadedState => State == GovernListState.Loaded;

    public bool IsNotLoading => State != GovernListState.Loading;

    public bool IsIdle => !IsBusy;

    public bool NoBlockedItems => !HasBlockedItems;

    public bool NoAllowedItems => !HasAllowedItems;

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
    private void ToggleSetForm() => IsSetFormOpen = !IsSetFormOpen;

    [RelayCommand]
    private void BlockMcp(McpRow? row)
    {
        if (row is null)
        {
            return;
        }

        var target = TargetOf(row);
        var argv = BuildScopedArgv("mcp", "block", target, includeReason: true);
        BeginConfirm($"Block MCP server “{target}”?", argv, () => RunMutationAsync(argv, $"Blocked “{target}”."));
    }

    [RelayCommand]
    private void AllowMcp(McpRow? row)
    {
        if (row is null)
        {
            return;
        }

        var target = TargetOf(row);
        var argv = BuildScopedArgv("mcp", "allow", target, includeReason: true);
        BeginConfirm($"Allow MCP server “{target}”?", argv, () => RunMutationAsync(argv, $"Allowed “{target}”."));
    }

    [RelayCommand]
    private void UnsetMcp(McpRow? row)
    {
        if (row is null)
        {
            return;
        }

        var target = TargetOf(row);
        var argv = BuildScopedArgv("mcp", "unset", target, includeReason: false);
        BeginConfirm($"Remove MCP server “{target}” from connector config?", argv, () => RunMutationAsync(argv, $"Removed “{target}”."));
    }

    [RelayCommand]
    private void SubmitSetForm()
    {
        if (string.IsNullOrWhiteSpace(SetName))
        {
            ShowResult("Name required", "Enter a server name before saving.", InfoBarSeverity.Warning);
            return;
        }

        var argv = new List<string> { "mcp", "set", SetName.Trim() };

        if (!string.IsNullOrWhiteSpace(SetCommand))
        {
            argv.Add("--command");
            argv.Add(SetCommand.Trim());
        }

        if (!string.IsNullOrWhiteSpace(SetArgs))
        {
            argv.Add("--args");
            argv.Add(SetArgs.Trim());
        }

        if (!string.IsNullOrWhiteSpace(SetUrl))
        {
            argv.Add("--url");
            argv.Add(SetUrl.Trim());
        }

        if (!string.IsNullOrWhiteSpace(SetTransport))
        {
            argv.Add("--transport");
            argv.Add(SetTransport.Trim());
        }

        if (!string.IsNullOrWhiteSpace(SetEnv))
        {
            argv.Add("--env");
            argv.Add(SetEnv.Trim());
        }

        if (SetSkipScan)
        {
            argv.Add("--skip-scan");
        }

        AppendConnectorScope(argv);

        var name = SetName.Trim();
        BeginConfirm($"Add or update MCP server “{name}”?", argv, () => RunMutationAsync(argv, $"Saved “{name}”."));
    }

    [RelayCommand]
    private async Task ConfirmYesAsync()
    {
        IsConfirmOpen = false;
        var action = _pendingAction;
        _pendingAction = null;

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
    }

    private static string TargetOf(McpRow row) => string.IsNullOrWhiteSpace(row.Name) ? row.DisplayName : row.Name;

    private List<string> BuildScopedArgv(string group, string verb, string target, bool includeReason)
    {
        var argv = new List<string> { group, verb, target };

        if (includeReason && !string.IsNullOrWhiteSpace(Reason))
        {
            argv.Add("--reason");
            argv.Add(Reason.Trim());
        }

        AppendConnectorScope(argv);
        return argv;
    }

    private void AppendConnectorScope(List<string> argv)
    {
        if (!string.IsNullOrWhiteSpace(SelectedConnector) && !string.Equals(SelectedConnector, AllConnectorsLabel, StringComparison.Ordinal))
        {
            argv.Add("--connector");
            argv.Add(SelectedConnector);
        }
    }

    private void BeginConfirm(string heading, IReadOnlyList<string> argv, Func<Task> action)
    {
        ConfirmHeading = heading;
        ConfirmCommandText = "defenseclaw " + string.Join(' ', argv.Select(QuoteForDisplay));
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
                IsSetFormOpen = false;
            }
            else
            {
                var errorLine = invocation.OutputLines.LastOrDefault(l => l.Stream == CliStream.StandardError);
                var detail = invocation.FailureReason ?? errorLine?.Text ?? $"Exit code {invocation.ExitCode?.ToString() ?? "unknown"}.";
                ShowResult("Command failed", detail, InfoBarSeverity.Error);
            }
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
            var mcps = await Services.Gateway.GetRawJsonAsync(McpsPath, cancellationToken: cancellationToken).ConfigureAwait(true);
            ApplyMcps(mcps);

            var blocked = await Services.Gateway.GetEnforceBlockedAsync(cancellationToken).ConfigureAwait(true);
            ApplyEnforcement(Blocked, blocked);
            HasBlockedItems = Blocked.Count > 0;

            var allowed = await Services.Gateway.GetEnforceAllowedAsync(cancellationToken).ConfigureAwait(true);
            ApplyEnforcement(Allowed, allowed);
            HasAllowedItems = Allowed.Count > 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyMcps(GatewayResult<JsonDocument> result)
    {
        Mcps.Clear();

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                using (var document = result.Value)
                {
                    foreach (var row in ParseRows(document!.RootElement))
                    {
                        Mcps.Add(row);
                    }
                }

                State = Mcps.Count == 0 ? GovernListState.Empty : GovernListState.Loaded;
                BannerMessage = Mcps.Count == 0 ? "No MCP servers are configured for the connector(s) in scope." : null;
                break;

            case GatewayStatus.NotConnected:
                result.Value?.Dispose();
                State = GovernListState.NotConnected;
                BannerMessage =
                    "MCP governance data appears here once a fleet connector is wired up. " +
                    "This install is running standalone - that is a normal, supported mode, not an error.";
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
                BannerMessage = result.ErrorMessage ?? "MCP servers could not be read.";
                break;
        }
    }

    private void ApplyEnforcement(ObservableCollection<EnforcementRow> target, GatewayResult<IReadOnlyList<EnforcementEntry>> result)
    {
        target.Clear();

        if (!result.IsOk || result.Value is null)
        {
            return;
        }

        foreach (var entry in result.Value)
        {
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
    }

    private static IEnumerable<McpRow> ParseRows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var element in root.EnumerateArray())
        {
            McpEntry? typed = null;
            try
            {
                typed = element.Deserialize<McpEntry>(RowJsonOptions);
            }
            catch (JsonException)
            {
                // Shape drifted from the guessed model; the row still renders from raw JSON.
            }

            yield return new McpRow
            {
                DisplayName = typed?.DisplayName ?? BestEffortName(element),
                Name = typed?.Name,
                Status = typed?.Status,
                Connector = typed?.Connector,
                Enabled = typed?.Enabled,
                Command = typed?.Command,
                Transport = typed?.Transport,
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

    public sealed class McpRow
    {
        public required string DisplayName { get; init; }

        public string? Name { get; init; }

        public string? Status { get; init; }

        public string? Connector { get; init; }

        public bool? Enabled { get; init; }

        public string? Command { get; init; }

        public string? Transport { get; init; }

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
