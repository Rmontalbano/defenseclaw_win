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

    /// <summary>True only when the list was read and is empty — never when it could not be read.</summary>
    public bool NoBlockedItems => !HasBlockedItems && !HasBlockedUnavailable;

    public bool NoAllowedItems => !HasAllowedItems && !HasAllowedUnavailable;

    public bool HasBlockedUnavailable => !string.IsNullOrEmpty(BlockedUnavailable);

    public bool HasAllowedUnavailable => !string.IsNullOrEmpty(AllowedUnavailable);

    /// <summary>"Blocked (3)", or "Blocked (unavailable)" — a count of 0 would claim a read that never happened.</summary>
    public string BlockedHeader => HasBlockedUnavailable ? "Blocked (unavailable)" : $"Blocked ({Blocked.Count})";

    public string AllowedHeader => HasAllowedUnavailable ? "Allowed (unavailable)" : $"Allowed ({Allowed.Count})";

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
        var connector = EffectiveConnector(row.Connector);
        var argv = BuildArgv("block", name, connector);
        BeginConfirm($"Block tool “{name}” for {ScopeText(connector)}?", argv, () => RunMutationAsync(argv, $"Blocked “{name}”."));
    }

    [RelayCommand]
    private void AllowTool(ToolRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = NameOf(row);
        var connector = EffectiveConnector(row.Connector);
        var argv = BuildArgv("allow", name, connector);
        BeginConfirm($"Allow tool “{name}” for {ScopeText(connector)}?", argv, () => RunMutationAsync(argv, $"Allowed “{name}”."));
    }

    /// <summary>
    /// Removes exactly the entry the row is. The row — not the toolbar — decides the scope, because
    /// the row IS a stored entry: a connector-scoped one (<c>@hermes/delete_file</c>) is removed
    /// with <c>--connector hermes</c>, and a global one with no flag. The toolbar's scope is
    /// deliberately not applied: <c>tool unblock delete_file --connector hermes</c> against a
    /// global row would target a different (probably absent) entry and leave the row in place.
    /// A bare <c>tool unblock</c> removes the fallback entry AND every connector-specific override
    /// for the tool (<c>tool unblock --help</c>), so a row with no connector says it affects ALL of
    /// them.
    /// </summary>
    [RelayCommand]
    private void UnblockEntry(EnforcementRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = row.DisplayName;
        var connector = row.Connector;
        var argv = new List<string> { "tool", "unblock", name };
        if (connector is not null)
        {
            argv.Add("--connector");
            argv.Add(connector);
        }

        var heading = connector is null
            ? $"Remove “{name}” from the block/allow lists for ALL configured connectors? This clears the fallback entry and every connector-specific override for this tool."
            : $"Remove the entry for “{name}” scoped to connector “{connector}”?";

        BeginConfirm(heading, argv, () => RunMutationAsync(argv, $"Removed the enforcement entry for “{name}”."));
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

    private List<string> BuildArgv(string verb, string name, string? connector)
    {
        var argv = new List<string> { "tool", verb, name };

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

    /// <summary>
    /// The connector a catalog-row command should name: the row's own when it has one, then the
    /// toolbar's, then none — which the CLI treats as the fallback tier covering every configured
    /// connector, and which the confirm heading says via <see cref="ScopeText"/>.
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
            var catalog = await Services.Gateway.GetRawJsonAsync(ToolsCatalogPath, cancellationToken: cancellationToken).ConfigureAwait(true);
            ApplyCatalog(catalog);

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

    /// <summary>
    /// Fills <paramref name="target"/> from one enforcement list. Returns null when the list was
    /// read (empty or not), or "unavailable (&lt;reason&gt;)" when it was not — 401, gateway down,
    /// not connected — in which case the rows are cleared too, because rows from an earlier read
    /// would offer actions on entries nobody can currently see. The caller shows the reason where
    /// "No blocked tools." would otherwise have said the opposite.
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
            if (entry.Kind is not null && !string.Equals(entry.Kind, EnforcementKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (tool, connector) = SplitScopedName(entry.DisplayName, entry.Connector);
            target.Add(new EnforcementRow
            {
                DisplayName = tool,
                Kind = entry.Kind,
                Reason = entry.Reason,
                Scope = entry.Scope,
                Connector = connector,
            });
        }

        return null;
    }

    /// <summary>
    /// Separates a tool entry into the tool name and the connector it is scoped to (null =
    /// global, the fallback tier). The CLI stores a connector-scoped tool row as
    /// <c>@&lt;connector&gt;/&lt;tool&gt;</c> (<c>cmd_tool._parse_target</c>), so a payload that
    /// hands that key over as the name is decoded here; a payload that carries the connector in
    /// its own field is trusted, and its <c>@connector/</c> prefix stripped if the name repeats it.
    /// Passing the undecoded key to <c>tool unblock</c> would build <c>@hermes/@hermes/tool</c>.
    /// </summary>
    private static (string Tool, string? Connector) SplitScopedName(string name, string? connectorField)
    {
        var connector = NormalizeConnector(connectorField);

        if (connector is not null)
        {
            var prefix = "@" + connector + "/";
            return (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name, connector);
        }

        if (name.Length > 3 && name[0] == '@')
        {
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            if (slash > 1 && slash < name.Length - 1)
            {
                return (name[(slash + 1)..], name[1..slash]);
            }
        }

        return (name, null);
    }

    /// <summary>Blank, "*", "all" and "global" all mean "no specific connector": the CLI's own word for the fallback tier is "connector=all".</summary>
    private static string? NormalizeConnector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed is "*" ||
               trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    /// <summary>Same wording the Overview panel uses for the same failures.</summary>
    private string DescribeEnforcementFailure(GatewayStatus status, string? message) => status switch
    {
        GatewayStatus.NotConnected => "the enforcement subsystem is not connected on this install",
        GatewayStatus.Unauthorized => $"the gateway needs a bearer token; none was found via {Services.Token.VariableName}",
        GatewayStatus.Unreachable => "the gateway is not answering",
        _ => message ?? "the gateway did not return the list",
    };

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
                Connector = typed?.Connector,
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

        /// <summary>The connector the catalog lists this tool under, when the payload says.</summary>
        public string? Connector { get; init; }

        public string? CapabilityClass { get; init; }

        public string? Description { get; init; }

        public string RawJson { get; init; } = string.Empty;
    }

    /// <summary>One stored block/allow entry, decoded so the row states what Unblock will remove.</summary>
    public sealed class EnforcementRow
    {
        /// <summary>The tool name, without any <c>@connector/</c> scoping prefix.</summary>
        public required string DisplayName { get; init; }

        public string? Kind { get; init; }

        public string? Reason { get; init; }

        public string? Scope { get; init; }

        /// <summary>The connector this entry is scoped to; null for a global (fallback-tier) entry.</summary>
        public string? Connector { get; init; }

        /// <summary>What the row applies to, shown on the row so Unblock is never a surprise.</summary>
        public string ConnectorLabel => Connector is null ? "applies to all connectors (fallback)" : $"connector: {Connector}";
    }
}
