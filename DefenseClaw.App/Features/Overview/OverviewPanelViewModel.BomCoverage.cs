using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.ViewModels;

/// <summary>One connector's line on the Overview's AI BOM coverage card: what the last AI BOM found in it, and what it could not say.</summary>
public sealed record OverviewBomCoverageRow
{
    public required string Connector { get; init; }

    /// <summary><c>12 skills · 3 plugins · 4 MCP servers</c>: the kinds the scan counted, in tab order; a kind it did not cover is left out.</summary>
    public required string Counts { get; init; }

    /// <summary>Failed inventory commands and capabilities the connector cannot inventory; empty when there are none.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Warn when something failed or could not be inventoried, Neutral otherwise.</summary>
    public string NoteKey { get; init; } = "Neutral";

    public override string ToString() => string.Join(". ", new[] { Connector, Counts, Note }.Where(p => p.Length > 0));
}

/// <summary>
/// The AI BOM coverage card (CUST-329, over the CUST-275 snapshot): one row per connector the Inventory page's last AI BOM covered, so the
/// Overview says how much of each connector has been inventoried. It only reads <see cref="Services.InventoryBomStore"/>: <c>aibom scan</c> is
/// state-changing (it records an audit event and posts to the gateway), so the Overview never runs one and never starts one on its own. With no
/// run yet the card says so and where to make one.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    public ObservableCollection<OverviewBomCoverageRow> BomCoverageRows { get; } = new();

    /// <summary><c>Generated 4m ago · every category</c>, or empty before a run.</summary>
    [ObservableProperty]
    private string _bomCoverageSummary = string.Empty;

    /// <summary>Why there are no rows (no run yet), or a caveat about the ones shown (a scan that skipped connectors); empty otherwise.</summary>
    [ObservableProperty]
    private string _bomCoverageNote = NoBomYet;

    [ObservableProperty]
    private bool _hasBomCoverage;

    internal const string NoBomYet =
        "No AI BOM has been generated in this session. The Overview does not run one: open Inventory and use Generate AI BOM (it shows the command first).";

    /// <summary>Reads what the Inventory page last published (no I/O, no scan) and rebuilds the rows.</summary>
    internal void RefreshBomCoverage()
    {
        var store = Services.InventoryBom;
        var snapshot = store.Snapshot;
        if (snapshot is null)
        {
            SyncByEquality(BomCoverageRows, Array.Empty<OverviewBomCoverageRow>(), static row => row.Connector);
            HasBomCoverage = false;
            BomCoverageSummary = string.Empty;
            BomCoverageNote = NoBomYet;
            return;
        }

        var scanned = store.Scanned;
        var rows = snapshot.Connectors.Select(connector => RowFor(connector, scanned)).ToList();
        SyncByEquality(BomCoverageRows, rows, static row => row.Connector);
        HasBomCoverage = rows.Count > 0;

        var parts = new List<string>();
        if (store.CapturedAt is { } at)
        {
            parts.Add($"Generated {OverviewAgentReader.Age(at, DateTimeOffset.Now)}");
        }

        parts.Add(scanned.Count == 0 ? "every category" : "only " + string.Join(", ", scanned.Select(static kind => kind.OnlyName())));
        BomCoverageSummary = string.Join(" · ", parts);

        BomCoverageNote = snapshot.Skipped.Count > 0
            ? $"{Plural(snapshot.Skipped.Count, "connector")} could not be read and {(snapshot.Skipped.Count == 1 ? "is" : "are")} not listed: " +
              string.Join(", ", snapshot.Skipped.Select(static s => s.Name))
            : rows.Count == 0 ? "The last AI BOM listed no connector." : string.Empty;
    }

    private static string Plural(int count, string noun) =>
        $"{count.ToString(CultureInfo.CurrentCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static OverviewBomCoverageRow RowFor(InventoryBomConnector connector, IReadOnlyList<InventoryBomKind> scanned)
    {
        var counts = new List<string>();
        foreach (var kind in InventoryBomKinds.All)
        {
            // A kind the scan was narrowed away from, or one the connector reported no count for, is not "0": it is left out.
            if (scanned.Count > 0 && !scanned.Contains(kind))
            {
                continue;
            }

            if (connector.Count(kind) is { Collected: true } count)
            {
                counts.Add($"{count.Count.ToString(CultureInfo.CurrentCulture)} {(count.Count == 1 ? Singular(kind) : kind.Plural())}");
            }
        }

        var notes = new List<string>();
        if (connector.ErrorCount > 0)
        {
            notes.Add($"{Plural((int)connector.ErrorCount, "inventory command")} failed");
        }

        if (connector.LimitationCount > 0)
        {
            notes.Add($"{connector.LimitationCount.ToString(CultureInfo.CurrentCulture)} not inventoried");
        }

        return new OverviewBomCoverageRow
        {
            Connector = connector.Name,
            Counts = counts.Count == 0 ? "nothing counted" : string.Join(" · ", counts),
            Note = string.Join(" · ", notes),
            NoteKey = notes.Count == 0 ? "Neutral" : "Warn",
        };
    }

    private static string Singular(InventoryBomKind kind) => kind switch
    {
        InventoryBomKind.Skills => "skill",
        InventoryBomKind.Plugins => "plugin",
        InventoryBomKind.Mcp => "MCP server",
        InventoryBomKind.Agents => "agent",
        InventoryBomKind.Tools => "tool",
        InventoryBomKind.Models => "model provider",
        InventoryBomKind.Memory => "memory store",
        _ => kind.Plural(),
    };
}
