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
/// View-model for the Tools panel: the tool catalog (<c>GET /tools/catalog</c>) plus the
/// tool-shaped slice of the enforcement lists, with block/allow/unblock mutations that
/// shell out to <c>defenseclaw tool ...</c> through a confirm-first dialog.
/// <para>
/// <b>Live-verified behaviour.</b> On this install <c>/tools/catalog</c> answers the same
/// <c>{"error":"gateway: not connected"}</c> shape as <c>/skills</c> - a normal standalone
/// state, tracked separately from the enforcement lists below it (which are real, currently
/// empty arrays and stay visible even while the catalog itself is not connected).
/// </para>
/// <para>
/// <see cref="ToolCatalogEntry"/> is an UNVERIFIED guess at the payload shape. Every catalog
/// row keeps the exact JSON element it was parsed from (via
/// <see cref="GatewayClient.GetRawJsonAsync"/>) so an unexpected shape still renders
/// something behind the row's expander.
/// </para>
/// </summary>
public sealed partial class ToolsPanelViewModel : PanelViewModelBase
{
    private const string ToolsCatalogPath = "tools/catalog";
    private const string EnforcementKind = "tool";
    private const string AllConnectorsLabel = "All configured connectors";

    private static readonly JsonSerializerOptions RowJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogLoadingState))]
    [NotifyPropertyChangedFor(nameof(IsCatalogNotConnectedState))]
    [NotifyPropertyChangedFor(nameof(IsCatalogEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsCatalogErrorState))]
    [NotifyPropertyChangedFor(nameof(IsCatalogLoadedState))]
    [NotifyPropertyChangedFor(nameof(IsCatalogNotLoading))]
    [NotifyPropertyChangedFor(nameof(ShowCatalogBanner))]
    [NotifyPropertyChangedFor(nameof(CatalogBannerSeverity))]
    private GovernListState _catalogState = GovernListState.Loading;

    [ObservableProperty] private string? _catalogBannerMessage;

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

    private Func<Task>? _pendingAction;

    public ToolsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Tools";

    public override string Description => "Tool catalog with capability classes and enforcement decisions.";

    public ObservableCollection<ToolRow> Catalog { get; } = new();

    public ObservableCollection<EnforcementRow> Blocked { get; } = new();

    public ObservableCollection<EnforcementRow> Allowed { get; } = new();

    public ObservableCollection<string> Connectors { get; } = new();

    public bool IsCatalogLoadingState => CatalogState == GovernListState.Loading;

    public bool IsCatalogNotConnectedState => CatalogState == GovernListState.NotConnected;

    public bool IsCatalogEmptyState => CatalogState == GovernListState.Empty;

    public bool IsCatalogErrorState => CatalogState == GovernListState.Error;

    public bool IsCatalogLoadedState => CatalogState == GovernListState.Loaded;

    public bool IsCatalogNotLoading => CatalogState != GovernListState.Loading;

    public bool IsIdle => !IsBusy;

    public bool NoBlockedItems => !HasBlockedItems;

    public bool NoAllowedItems => !HasAllowedItems;

    public bool ShowCatalogBanner => CatalogState is GovernListState.NotConnected or GovernListState.Empty or GovernListState.Error;

    public InfoBarSeverity CatalogBannerSeverity => CatalogState == GovernListState.Error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BuildConnectorList();
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private void BlockTool(ToolRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = NameOf(row);
        var argv = BuildArgv("block", name);
        BeginConfirm($"Block tool “{name}”?", argv, () => RunMutationAsync(argv, $"Blocked “{name}”."));
    }

    [RelayCommand]
    private void AllowTool(ToolRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = NameOf(row);
        var argv = BuildArgv("allow", name);
        BeginConfirm($"Allow tool “{name}”?", argv, () => RunMutationAsync(argv, $"Allowed “{name}”."));
    }

    [RelayCommand]
    private void UnblockEntry(EnforcementRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = row.DisplayName;
        var argv = new List<string> { "tool", "unblock", name };
        AppendConnectorScope(argv);
        BeginConfirm($"Remove enforcement entry for “{name}”?", argv, () => RunMutationAsync(argv, $"Removed the enforcement entry for “{name}”."));
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

    private static string NameOf(ToolRow row) => string.IsNullOrWhiteSpace(row.Name) ? row.DisplayName : row.Name;

    private List<string> BuildArgv(string verb, string name)
    {
        var argv = new List<string> { "tool", verb, name };

        if (!string.IsNullOrWhiteSpace(Reason))
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
            var catalog = await Services.Gateway.GetRawJsonAsync(ToolsCatalogPath, cancellationToken: cancellationToken).ConfigureAwait(true);
            ApplyCatalog(catalog);

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

    private void ApplyCatalog(GatewayResult<JsonDocument> result)
    {
        Catalog.Clear();

        switch (result.Status)
        {
            case GatewayStatus.Ok:
                using (var document = result.Value)
                {
                    foreach (var row in ParseRows(document!.RootElement))
                    {
                        Catalog.Add(row);
                    }
                }

                CatalogState = Catalog.Count == 0 ? GovernListState.Empty : GovernListState.Loaded;
                CatalogBannerMessage = Catalog.Count == 0 ? "The tool catalog is empty." : null;
                break;

            case GatewayStatus.NotConnected:
                result.Value?.Dispose();
                CatalogState = GovernListState.NotConnected;
                CatalogBannerMessage =
                    "The tool catalog appears here once a fleet connector is wired up. This install is running " +
                    "standalone - that is a normal, supported mode, not an error. The block/allow lists below are " +
                    "independent of the catalog and still work.";
                break;

            case GatewayStatus.Unauthorized:
                result.Value?.Dispose();
                CatalogState = GovernListState.Error;
                CatalogBannerMessage = $"The gateway rejected the request: no usable bearer token via {Services.Token.VariableName}.";
                break;

            case GatewayStatus.Unreachable:
                result.Value?.Dispose();
                CatalogState = GovernListState.Error;
                CatalogBannerMessage = "The gateway is not reachable. " + (result.ErrorMessage ?? string.Empty);
                break;

            default:
                result.Value?.Dispose();
                CatalogState = GovernListState.Error;
                CatalogBannerMessage = result.ErrorMessage ?? "The tool catalog could not be read.";
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

    private static IEnumerable<ToolRow> ParseRows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var element in root.EnumerateArray())
        {
            ToolCatalogEntry? typed = null;
            try
            {
                typed = element.Deserialize<ToolCatalogEntry>(RowJsonOptions);
            }
            catch (JsonException)
            {
                // Shape drifted from the guessed model; the row still renders from raw JSON.
            }

            yield return new ToolRow
            {
                DisplayName = typed?.DisplayName ?? BestEffortName(element),
                Name = typed?.Name,
                Server = typed?.Server,
                CapabilityClass = typed?.CapabilityClass,
                Description = typed?.Description,
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

    public sealed class ToolRow
    {
        public required string DisplayName { get; init; }

        public string? Name { get; init; }

        public string? Server { get; init; }

        public string? CapabilityClass { get; init; }

        public string? Description { get; init; }

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
