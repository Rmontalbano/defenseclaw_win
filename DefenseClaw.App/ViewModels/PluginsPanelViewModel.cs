using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Plugins panel.
/// <para>
/// <b>No gateway endpoint exists for plugins</b> - <see cref="Core.Gateway.IGatewayClient"/>
/// has no <c>GetPlugins</c> method because the sidecar REST surface never exposed one (only
/// <c>/skills</c>, <c>/mcps</c>, <c>/tools/catalog</c> and the enforcement lists do). Local
/// plugin state is read the same way the CLI itself would show it to an operator: by
/// shelling out to the read-only <c>defenseclaw plugin list --json</c>, discovered and
/// verified against the live 0.8.7 install via <c>defenseclaw plugin --help</c> /
/// <c>defenseclaw plugin list --help</c>. This keeps the read path honest with the "every
/// mutation goes through the CLI" principle, just extended to the one read this panel needs
/// that the gateway cannot answer.
/// </para>
/// <para>
/// The real <c>--json</c> shape on this install is <c>[]</c> (no plugins installed), which
/// does not disambiguate a flat list of plugin objects from <c>tool list --json</c>'s
/// per-connector wrapper shape (<c>[{"connector": "...", "tools": [...]}]</c>). Row parsing
/// below tolerates both: a wrapper object with a nested array is flattened, and a bare
/// object is treated as a plugin row directly. Every row keeps its exact JSON text behind
/// an expander so an unexpected shape still renders something.
/// </para>
/// </summary>
public sealed partial class PluginsPanelViewModel : PanelViewModelBase
{
    private const string AllConnectorsLabel = "All configured connectors";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadingState))]
    [NotifyPropertyChangedFor(nameof(IsUnavailableState))]
    [NotifyPropertyChangedFor(nameof(IsEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsErrorState))]
    [NotifyPropertyChangedFor(nameof(IsLoadedState))]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private GovernListState _state = GovernListState.Loading;

    [ObservableProperty] private string? _bannerMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty] private string? _selectedConnector;

    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmHeading = string.Empty;
    [ObservableProperty] private string _confirmCommandText = string.Empty;

    [ObservableProperty] private bool _isResultOpen;
    [ObservableProperty] private string _resultTitle = string.Empty;
    [ObservableProperty] private string _resultMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _resultSeverity = InfoBarSeverity.Informational;

    // "Install plugin" mini form.
    [ObservableProperty] private bool _isInstallFormOpen;
    [ObservableProperty] private string _installNameOrPath = string.Empty;
    [ObservableProperty] private bool _installForce;
    [ObservableProperty] private bool _installApplyActionPolicy;

    private Func<Task>? _pendingAction;

    public PluginsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Plugins";

    public override string Description => "Discovered plugins and their governance state.";

    public ObservableCollection<PluginRow> Plugins { get; } = new();

    public ObservableCollection<string> Connectors { get; } = new();

    public bool IsLoadingState => State == GovernListState.Loading;

    public bool IsUnavailableState => State == GovernListState.Unavailable;

    public bool IsEmptyState => State == GovernListState.Empty;

    public bool IsErrorState => State == GovernListState.Error;

    public bool IsLoadedState => State == GovernListState.Loaded;

    public bool IsIdle => !IsBusy;

    public bool ShowBanner => State is GovernListState.Unavailable or GovernListState.Empty or GovernListState.Error;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BuildConnectorList();
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand]
    private void ToggleInstallForm() => IsInstallFormOpen = !IsInstallFormOpen;

    [RelayCommand]
    private void SubmitInstallForm()
    {
        if (string.IsNullOrWhiteSpace(InstallNameOrPath))
        {
            ShowResult("Path or name required", "Enter a local path, npm package, clawhub:// URI or URL before saving.", InfoBarSeverity.Warning);
            return;
        }

        var target = InstallNameOrPath.Trim();
        var argv = new List<string> { "plugin", "install", target };

        if (InstallForce)
        {
            argv.Add("--force");
        }

        if (InstallApplyActionPolicy)
        {
            argv.Add("--action");
        }

        AppendConnectorScope(argv);

        BeginConfirm($"Install plugin “{target}”?", argv, () => RunMutationAsync(argv, $"Installed “{target}”.", closeInstallForm: true));
    }

    [RelayCommand]
    private void RemovePlugin(PluginRow? row)
    {
        if (row is null)
        {
            return;
        }

        var name = NameOf(row);
        var argv = new List<string> { "plugin", "remove", name };
        AppendConnectorScope(argv);
        BeginConfirm($"Remove plugin “{name}”?", argv, () => RunMutationAsync(argv, $"Removed “{name}”.", closeInstallForm: false));
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

    private static string NameOf(PluginRow row) => string.IsNullOrWhiteSpace(row.Name) ? row.DisplayName : row.Name;

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

    private async Task RunMutationAsync(IReadOnlyList<string> argv, string successMessage, bool closeInstallForm)
    {
        IsBusy = true;
        try
        {
            var invocation = await Services.Cli.RunAsync(argv).ConfigureAwait(true);
            if (invocation.ExitCode == 0)
            {
                ShowResult("Done", successMessage, InfoBarSeverity.Success);
                if (closeInstallForm)
                {
                    IsInstallFormOpen = false;
                }
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
        Plugins.Clear();

        try
        {
            CliInvocation invocation;
            try
            {
                invocation = await Services.Cli.RunAsync(new[] { "plugin", "list", "--json" }, cancellationToken: cancellationToken).ConfigureAwait(true);
            }
            catch (CliNotFoundException ex)
            {
                State = GovernListState.Unavailable;
                BannerMessage = $"The defenseclaw CLI could not be found ({ex.Message}). Plugin state cannot be read without it.";
                return;
            }

            if (invocation.FailureReason is not null)
            {
                State = GovernListState.Error;
                BannerMessage = $"`defenseclaw plugin list --json` could not start: {invocation.FailureReason}";
                return;
            }

            if (invocation.ExitCode != 0)
            {
                var errorLine = invocation.OutputLines.LastOrDefault(l => l.Stream == CliStream.StandardError);
                State = GovernListState.Error;
                BannerMessage = errorLine?.Text ?? $"`defenseclaw plugin list --json` exited with code {invocation.ExitCode}.";
                return;
            }

            var stdout = string.Concat(invocation.OutputLines
                .Where(l => l.Stream == CliStream.StandardOutput)
                .Select(l => l.Text + "\n"));

            if (string.IsNullOrWhiteSpace(stdout))
            {
                State = GovernListState.Empty;
                BannerMessage = "No plugins are installed for the connector(s) in scope.";
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(stdout);
            }
            catch (JsonException ex)
            {
                State = GovernListState.Error;
                BannerMessage = $"Could not parse `defenseclaw plugin list --json` output: {ex.Message}";
                return;
            }

            using (document)
            {
                foreach (var row in ParseRows(document.RootElement))
                {
                    Plugins.Add(row);
                }
            }

            State = Plugins.Count == 0 ? GovernListState.Empty : GovernListState.Loaded;
            BannerMessage = Plugins.Count == 0 ? "No plugins are installed for the connector(s) in scope." : null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Tolerates both a flat array of plugin objects and a per-connector wrapper array
    /// (matching the shape <c>tool list --json</c> uses: <c>[{"connector": "...", "plugins": [...]}]</c>),
    /// since the real payload on this install is <c>[]</c> and cannot confirm either shape.
    /// </summary>
    private static IEnumerable<PluginRow> ParseRows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var (element, connector) in FlattenPluginRows(root))
        {
            yield return new PluginRow
            {
                DisplayName = BestEffortName(element),
                Name = StringOrNull(element, "name") ?? StringOrNull(element, "id"),
                Path = StringOrNull(element, "path"),
                Status = StringOrNull(element, "status"),
                Severity = StringOrNull(element, "severity") ?? StringOrNull(element, "scan_severity"),
                Connector = connector ?? StringOrNull(element, "connector"),
                RawJson = FormatJson(element),
            };
        }
    }

    private static IEnumerable<(JsonElement Element, string? Connector)> FlattenPluginRows(JsonElement root)
    {
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var connector = StringOrNull(item, "connector");
                JsonElement? nested = null;

                foreach (var key in new[] { "plugins", "items", "entries", "list" })
                {
                    if (item.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
                    {
                        nested = candidate;
                        break;
                    }
                }

                if (nested is { } nestedArray)
                {
                    foreach (var sub in nestedArray.EnumerateArray())
                    {
                        yield return (sub, connector);
                    }

                    continue;
                }
            }

            yield return (item, null);
        }
    }

    private static string? StringOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static string BestEffortName(JsonElement element) =>
        StringOrNull(element, "name") ?? StringOrNull(element, "id") ?? StringOrNull(element, "path") ?? "(unnamed)";

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

        /// <summary>The <c>defenseclaw</c> executable itself could not be found.</summary>
        Unavailable,

        Empty,
        Error,
        Loaded,
    }

    public sealed class PluginRow
    {
        public required string DisplayName { get; init; }

        public string? Name { get; init; }

        public string? Path { get; init; }

        public string? Status { get; init; }

        public string? Severity { get; init; }

        public string? Connector { get; init; }

        public string RawJson { get; init; } = string.Empty;
    }
}
