using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>One connector's line in the generated AI BOM summary.</summary>
public sealed record InventoryBomRow(string Connector, string Summary)
{
    public override string ToString() => $"{Connector}: {Summary}";
}

/// <summary>
/// Inventory: empty-state logic and the optional "Generate AI BOM" action.
/// <para>
/// <b>AI BOM.</b> <c>defenseclaw aibom scan --json [--connector C]</c> (flags from its help) inventories the
/// active connectors' own skills, plugins, MCP servers, agents, tools, models and memory with policy
/// verdicts. The inventory is only read, but every run records a scan event in audit.db and posts to the
/// gateway (and fails closed if the gateway is down), so it is a state-changing command: it goes through the
/// review dialog and never runs on its own. Its JSON shape was not run here (a live run writes the audit
/// record), so the summary reads it generically — the connector's <c>summary</c> object if it has one,
/// otherwise the length of each top-level array — and falls back to "see Activity".
/// </para>
/// </summary>
public sealed partial class InventoryPanelViewModel
{
    public const string AllConnectorsLabel = "All active connectors";

    private static readonly Regex ConnectorNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private string? _lastBomJson;
    private bool _lastBomTruncated;

    /// <summary>inventory.db does not exist yet: the normal state until AI discovery has run once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatabase))]
    private bool _databaseMissing;

    /// <summary>False until the first read finishes, so the grid does not flash "no components" while it is still looking.</summary>
    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    private bool _showEmptyState;

    [ObservableProperty]
    private string _emptyTitle = string.Empty;

    [ObservableProperty]
    private string _emptyDetail = string.Empty;

    [ObservableProperty]
    private string? _selectedBomConnector;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBom))]
    private string? _bomStatus;

    [ObservableProperty]
    private bool _canSaveBom;

    public bool HasDatabase => !DatabaseMissing;

    public bool HasBom => !string.IsNullOrWhiteSpace(BomStatus);

    public ObservableCollection<string> BomConnectors { get; } = new();

    public ObservableCollection<InventoryBomRow> BomRows { get; } = new();

    /// <summary>Esc closes the review dialog. True when it consumed the key.</summary>
    public bool HandleEscape() => Review.IsOpen && Review.HandleEscape();

    /// <summary>
    /// Says why the components grid is empty, when it is: no database yet, no rollup for this database's
    /// layout, a scan that found nothing, or a search that hides everything. Never a scary state for a
    /// normal install.
    /// </summary>
    private void UpdateEmptyState()
    {
        if (!HasLoaded)
        {
            ShowEmptyState = false;
            return;
        }

        if (DatabaseMissing)
        {
            EmptyTitle = "No inventory yet";
            EmptyDetail = "inventory.db is created the first time AI discovery runs. Turn on AI Discovery and run a scan, then refresh this page.";
            ShowEmptyState = true;
            return;
        }

        if (UsingFallbackView)
        {
            EmptyTitle = "No component rollup available";
            EmptyDetail = "Use the table browser below to inspect the raw tables this install's inventory.db does have.";
            ShowEmptyState = true;
            return;
        }

        if (_allComponents.Count == 0)
        {
            EmptyTitle = "No components in the latest scan";
            EmptyDetail = (string.IsNullOrWhiteSpace(ScanNote) ? string.Empty : ScanNote + " ") +
                "Nothing found is not the same as nothing looked at: AI Discovery lists exactly what its scans examined.";
            ShowEmptyState = true;
            return;
        }

        var shown = ComponentsView.Cast<object>().Count();
        if (shown == 0)
        {
            EmptyTitle = $"Nothing matches “{SearchText}”";
            EmptyDetail = "Clear or change the search text.";
            ShowEmptyState = true;
            return;
        }

        ShowEmptyState = false;
    }

    /// <summary>The connectors <c>aibom scan --connector</c> can be narrowed to: the ones named in config.yaml.</summary>
    private void BuildBomConnectors()
    {
        BomConnectors.Clear();
        BomConnectors.Add(AllConnectorsLabel);

        var guardrail = Services.Config.Config.Guardrail;
        AddConnector(guardrail.Connector);
        foreach (var key in guardrail.Connectors.Keys)
        {
            AddConnector(key);
        }

        SelectedBomConnector = AllConnectorsLabel;

        void AddConnector(string? name)
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && ConnectorNamePattern.IsMatch(trimmed) &&
                !BomConnectors.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                BomConnectors.Add(trimmed);
            }
        }
    }

    [RelayCommand]
    private void GenerateAiBom()
    {
        var connector = SelectedBomConnector;
        var scoped = !string.IsNullOrWhiteSpace(connector) &&
            !string.Equals(connector, AllConnectorsLabel, StringComparison.Ordinal) &&
            ConnectorNamePattern.IsMatch(connector);

        var argv = new List<string> { "aibom", "scan", "--json" };
        if (scoped)
        {
            argv.Add("--connector");
            argv.Add(connector!);
        }

        Review.Open(
            "Generate an AI BOM?",
            "Inventories the skills, plugins, MCP servers, agents, tools, models and memory of " +
            (scoped ? $"the {connector} connector" : "every active connector") +
            ", with their policy verdicts. The inventory itself is only read, but every run records a scan event " +
            "in audit.db (it raises Overview's Scans count) and posts an observability event to the gateway, and it " +
            "fails if the gateway is down. This reads the connectors' own configuration; it is not the machine-wide " +
            "scan on the AI Discovery page.",
            new[]
            {
                new DiscoverStep(
                    argv,
                    "Build the AI bill of materials as JSON.",
                    CommandTier.StateChanging,
                    CliRunner.ExtendedTimeout),
            },
            result => AfterBomAsync(result, scoped ? connector : null),
            primaryText: "Generate");
    }

    private Task AfterBomAsync(DiscoverReviewResult result, string? connector)
    {
        BomRows.Clear();
        _lastBomJson = null;
        _lastBomTruncated = false;
        CanSaveBom = false;

        var stamp = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (!result.Succeeded || result.Invocations.Count == 0)
        {
            BomStatus = $"The AI BOM run did not finish ({stamp}). The dialog and the Activity panel have its output.";
            return Task.CompletedTask;
        }

        var invocation = result.Invocations[^1];
        var stdout = DiscoverCli.Stdout(invocation);
        var scope = connector ?? "every active connector";

        try
        {
            foreach (var row in ParseBom(stdout, connector))
            {
                BomRows.Add(row);
            }

            _lastBomJson = stdout;
            _lastBomTruncated = invocation.IsOutputTruncated;
            CanSaveBom = !_lastBomTruncated && !string.IsNullOrWhiteSpace(stdout);
            BomStatus = _lastBomTruncated
                ? $"AI BOM generated {stamp} for {scope}. The output was too long to keep in full, so it cannot be saved from here; " +
                  "run 'defenseclaw aibom scan --json' in a terminal to capture all of it."
                : $"AI BOM generated {stamp} for {scope}. The full JSON is in the Activity panel, and you can save it.";
        }
        catch (JsonException ex)
        {
            BomStatus = $"AI BOM ran ({stamp}) but its output was not the JSON this page expected ({ex.Message}). See the Activity panel.";
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A connector's category counts from <c>aibom scan --json</c>: the bare object for one connector, a list
    /// for several. Reads a <c>summary</c> object of numbers if there is one, otherwise the length of each
    /// top-level array.
    /// </summary>
    internal static List<InventoryBomRow> ParseBom(string json, string? requestedConnector)
    {
        var rows = new List<InventoryBomRow>();
        using var document = JsonDocument.Parse(DiscoverCli.TrimToJson(json));
        var root = document.RootElement;

        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : new List<JsonElement> { root };

        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var connector = FirstString(item, "connector", "connector_name", "name") ?? requestedConnector ?? "active connector";
            var counts = new List<(string Name, long Count)>();

            if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in summary.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var number))
                    {
                        counts.Add((property.Name, number));
                    }
                }
            }

            if (counts.Count == 0)
            {
                foreach (var property in item.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        counts.Add((property.Name, property.Value.GetArrayLength()));
                    }
                }
            }

            rows.Add(new InventoryBomRow(
                connector,
                counts.Count == 0
                    ? "inventory generated; the category counts could not be read from this output (see Activity)"
                    : string.Join(
                        " · ",
                        counts.Select(c => $"{c.Name.Replace('_', ' ')} {c.Count.ToString(CultureInfo.InvariantCulture)}"))));
        }

        return rows;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>Writes the generated BOM JSON to a file the operator chooses.</summary>
    [RelayCommand]
    private void SaveBom()
    {
        if (_lastBomJson is null || _lastBomTruncated)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save AI BOM",
            FileName = $"aibom-{DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.json",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _lastBomJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            BomStatus = $"Saved the AI BOM to {dialog.FileName}. It lists your connectors' skills, plugins and MCP servers, so share it with care.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BomStatus = $"Could not save the AI BOM: {ex.Message}";
        }
    }
}
