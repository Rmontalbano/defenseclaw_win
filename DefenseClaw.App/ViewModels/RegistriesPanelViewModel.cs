using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>One key/value pair of a source's raw JSON, for the generic detail expander.</summary>
public sealed record RegistryFieldRow(string Key, string Value);

/// <summary>
/// One configured registry source, as returned by <c>defenseclaw registry list --json</c>.
/// The CLI does not publish a schema for this array, so every field beyond an identifying
/// name is read generically and shown verbatim rather than guessed at.
/// </summary>
public sealed class RegistrySourceRow
{
    public required string Id { get; init; }

    public string? Kind { get; init; }

    public string? Location { get; init; }

    public string? EntriesSummary { get; init; }

    public required IReadOnlyList<RegistryFieldRow> Fields { get; init; }
}

/// <summary>
/// View-model for the Registries panel: external skill / MCP catalog sources DefenseClaw
/// can sync from.
/// <para>
/// Primary source is the read-only <c>defenseclaw registry list --json</c> verb (confirmed
/// against the live CLI — it only reads <c>index.json</c> plus config, no writes). When the
/// CLI is unavailable this falls back to whatever raw <c>registry:</c> section exists in
/// <c>config.yaml</c>, and otherwise shows a friendly empty state — this box currently has
/// no sources configured, which is itself a legitimate, common state to render well rather
/// than treat as an error.
/// </para>
/// </summary>
public sealed partial class RegistriesPanelViewModel : PanelViewModelBase
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCliError))]
    private string? _cliErrorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _configSectionYaml;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSources))]
    private bool _hasSources;

    [ObservableProperty]
    private RegistrySourceRow? _selectedSource;

    public RegistriesPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Registries";

    public override string Description =>
        "External skill / MCP catalog sources DefenseClaw can sync from, read via 'defenseclaw registry list'.";

    public bool HasCliError => !string.IsNullOrEmpty(CliErrorMessage);

    public bool HasConfigSection => !string.IsNullOrEmpty(ConfigSectionYaml);

    public bool HasNoSources => !HasSources;

    public ObservableCollection<RegistrySourceRow> Sources { get; } = new();

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(CancellationToken.None).ConfigureAwait(true);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        CliErrorMessage = null;
        Sources.Clear();

        try
        {
            var invocation = await Services.Cli.RunAsync(
                new[] { "registry", "list", "--json" }, cancellationToken: cancellationToken).ConfigureAwait(true);

            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                CliErrorMessage = $"Could not run 'defenseclaw registry list': {reason}";
            }
            else if (invocation.ExitCode != 0)
            {
                var stderr = string.Join(
                    Environment.NewLine,
                    invocation.OutputLines.Where(l => l.Stream == CliStream.StandardError).Select(l => l.Text));
                CliErrorMessage = string.IsNullOrWhiteSpace(stderr)
                    ? $"'defenseclaw registry list' exited {invocation.ExitCode}."
                    : $"'defenseclaw registry list' exited {invocation.ExitCode}: {stderr}";
            }
            else
            {
                var stdout = string.Join(
                    Environment.NewLine,
                    invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));
                ParseSources(stdout);
            }
        }
        catch (CliNotFoundException ex)
        {
            CliErrorMessage = $"'defenseclaw' was not found on PATH: {ex.Message}";
        }

        HasSources = Sources.Count > 0;
        StatusMessage = Sources.Count == 0
            ? "No registry sources configured."
            : $"{Sources.Count} registry source{(Sources.Count == 1 ? string.Empty : "s")}.";

        LoadConfigFallback();

        IsLoading = false;
    }

    /// <summary>
    /// Falls back to whatever raw <c>registry:</c> section survives in config.yaml — this
    /// model has no typed section for it (see <c>DefenseClawConfig.KnownSections</c>), so
    /// it round-trips as verbatim YAML text rather than being parsed here.
    /// </summary>
    private void LoadConfigFallback()
    {
        ConfigSectionYaml = Services.Config.SectionText("registry");
    }

    private void ParseSources(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                index++;
                Sources.Add(MapSource(element, index));
            }
        }
        catch (JsonException ex)
        {
            CliErrorMessage = $"'defenseclaw registry list --json' did not return valid JSON: {ex.Message}";
        }
    }

    private static RegistrySourceRow MapSource(JsonElement element, int fallbackIndex)
    {
        var fields = new List<RegistryFieldRow>();
        string? id = null;
        string? kind = null;
        string? location = null;
        string? entries = null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var display = FormatValue(property.Value);
                fields.Add(new RegistryFieldRow(property.Name, display));

                switch (property.Name.ToLowerInvariant())
                {
                    case "id":
                    case "name":
                        id ??= display;
                        break;
                    case "type":
                    case "kind":
                        kind ??= display;
                        break;
                    case "url":
                    case "source":
                    case "location":
                    case "repo":
                        location ??= display;
                        break;
                    case "entries":
                    case "entry_count":
                    case "count":
                        entries ??= display;
                        break;
                }
            }
        }
        else
        {
            fields.Add(new RegistryFieldRow("value", FormatValue(element)));
        }

        return new RegistrySourceRow
        {
            Id = id ?? $"source-{fallbackIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            Kind = kind,
            Location = location,
            EntriesSummary = entries,
            Fields = fields,
        };
    }

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "—",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.GetRawText(),
    };
}
