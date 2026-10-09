using System.Globalization;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Inventory;

/// <summary>
/// The kinds of entity <c>defenseclaw aibom scan --json</c> lists, in the order the TUI's Inventory sub-tabs run (skills, plugins,
/// MCPs, agents, models, memory) with Tools where the newer runtime's TUI puts it, between agents and models.
/// </summary>
public enum InventoryBomKind
{
    Skills,
    Plugins,
    Mcp,
    Agents,
    Tools,
    Models,
    Memory,
}

/// <summary>What the TUI calls each <see cref="InventoryBomKind"/>: its JSON key, its <c>--only</c> word, its tab label and its table columns.</summary>
public static class InventoryBomKinds
{
    /// <summary>Every kind, in tab order.</summary>
    public static IReadOnlyList<InventoryBomKind> All { get; } = Enum.GetValues<InventoryBomKind>();

    /// <summary>The TUI's <c>FAST_SCAN_CATEGORIES</c>: the three kinds a quick scan covers (its "Fast" scope).</summary>
    public static IReadOnlyList<InventoryBomKind> FastScan { get; } = [InventoryBomKind.Skills, InventoryBomKind.Plugins, InventoryBomKind.Mcp];

    /// <summary>The key of the kind's array (and of its summary entry) in the JSON: <c>model_providers</c> for models, the kind's own word otherwise.</summary>
    public static string JsonKey(this InventoryBomKind kind) => kind switch
    {
        InventoryBomKind.Skills => "skills",
        InventoryBomKind.Plugins => "plugins",
        InventoryBomKind.Mcp => "mcp",
        InventoryBomKind.Agents => "agents",
        InventoryBomKind.Tools => "tools",
        InventoryBomKind.Models => "model_providers",
        InventoryBomKind.Memory => "memory",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The word <c>aibom scan --only</c> takes for the kind (<c>models</c>, where the JSON says <c>model_providers</c>).</summary>
    public static string OnlyName(this InventoryBomKind kind) => kind == InventoryBomKind.Models ? "models" : kind.JsonKey();

    /// <summary>The kind's tab label, as the TUI spells it ("MCPs").</summary>
    public static string Label(this InventoryBomKind kind) => kind switch
    {
        InventoryBomKind.Skills => "Skills",
        InventoryBomKind.Plugins => "Plugins",
        InventoryBomKind.Mcp => "MCPs",
        InventoryBomKind.Agents => "Agents",
        InventoryBomKind.Tools => "Tools",
        InventoryBomKind.Models => "Models",
        InventoryBomKind.Memory => "Memory",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind in a sentence ("No agents found").</summary>
    public static string Plural(this InventoryBomKind kind) => kind switch
    {
        InventoryBomKind.Mcp => "MCP servers",
        InventoryBomKind.Models => "model providers",
        InventoryBomKind.Memory => "memory stores",
        _ => kind.Label().ToLowerInvariant(),
    };

    /// <summary>
    /// The columns of the kind's table, without the Connector column a merged inventory puts in front: the TUI's
    /// <c>data_table_columns</c>, label for label. Tools has the newer TUI's columns (it is not a tab of 0.8.10's).
    /// </summary>
    public static IReadOnlyList<string> Columns(this InventoryBomKind kind) => kind switch
    {
        InventoryBomKind.Skills => ["ID", "Verdict", "Enabled", "Severity", "Findings", "Source"],
        InventoryBomKind.Plugins => ["Name", "Version", "Origin", "Status", "Verdict", "Findings", "Severity"],
        InventoryBomKind.Mcp => ["ID", "Source", "Transport", "Command/URL"],
        InventoryBomKind.Agents => ["ID", "Source", "Model", "Workspace", "Default"],
        InventoryBomKind.Tools => ["ID", "Name", "Kind", "Source"],
        InventoryBomKind.Models => ["ID", "Source", "Default Model", "Status"],
        InventoryBomKind.Memory => ["ID", "Backend", "Provider", "Files", "Chunks", "Workspace"],
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Reads a <c>--only</c> word (or a JSON key): <c>models</c> and <c>model_providers</c> both mean <see cref="InventoryBomKind.Models"/>.</summary>
    public static bool TryParse(string? word, out InventoryBomKind kind)
    {
        var text = (word ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var candidate in All)
        {
            if (text == candidate.OnlyName() || text == candidate.JsonKey())
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>One labelled value of an entity's detail pane (the TUI's <c>InventoryDetailInfo.fields</c>).</summary>
public sealed record InventoryBomField(string Label, string Value);

/// <summary>
/// One row of an AI bill of materials: a skill, plugin, MCP server, agent, tool, model provider or memory store of one connector.
/// <para>
/// <see cref="Cells"/> are the table cells in <see cref="InventoryBomKinds.Columns"/> order (no Connector cell: a merged
/// inventory puts <see cref="Connector"/> in front when it needs one), and <see cref="Fields"/> are the TUI's detail fields, label for label, in
/// its order, with the TUI's text for each value (<c>true</c> / <c>false</c>, counts, lists joined by <c>, </c>); a value the row did not carry is
/// empty. <see cref="More"/> are the other members the row carried, shown by presence: nothing is added for a member the row does not have.
/// Every text has had its control and bidirectional characters spelled out (<see cref="DisplayNames.Visible"/>): these are names and
/// descriptions read from files an agent installed.
/// </para>
/// </summary>
/// <param name="Kind">Which table the row belongs to.</param>
/// <param name="Connector">The connector whose inventory listed it.</param>
/// <param name="Id">The row's id (a plugin's, its manifest id, even though the TUI names it by <paramref name="Title"/>).</param>
/// <param name="Title">The detail pane's heading, the TUI's: <c>AGENT: reviewer</c>.</param>
/// <param name="Cells">The table cells.</param>
/// <param name="Fields">The TUI's detail fields.</param>
/// <param name="More">The row's other members, by presence.</param>
/// <param name="Verdict">The policy verdict (<c>policy_verdict</c>) of a skill or plugin; empty for the other kinds and for a row nothing evaluated.</param>
/// <param name="Status">A plugin's status (<c>loaded</c>, <c>disabled</c>), which the TUI's Plugins filter keys on; empty for the other kinds.</param>
/// <param name="Eligible">A skill's <c>eligible</c>, which the TUI's Skills filter keys on.</param>
public sealed record InventoryBomEntity(
    InventoryBomKind Kind,
    string Connector,
    string Id,
    string Title,
    IReadOnlyList<string> Cells,
    IReadOnlyList<InventoryBomField> Fields,
    IReadOnlyList<InventoryBomField> More,
    string Verdict,
    string Status,
    bool Eligible);

/// <summary>An inventory command that failed, as the connector's <c>errors</c> list names it (<c>codex:mcp</c> and what went wrong).</summary>
public sealed record InventoryBomError(string Command, string Message);

/// <summary>A category a connector cannot inventory, which is information and not a failure (the TUI's <c>InventoryLimitation</c>).</summary>
public sealed record InventoryBomLimitation(string Connector, string Category, string Status, string Reason);

/// <summary>
/// How many entities of a kind a connector reported, from its <c>summary</c>.
/// <paramref name="Eligible" />, <paramref name="Loaded" /> and <paramref name="Disabled" /> are the extra counts of the skills and plugins
/// entries (null where the summary has none); <paramref name="Collected" /> is false where the newer runtime's summary says the scan did not
/// collect the kind (an <c>--only</c> run), so an empty list there means "not asked for" and not "none".
/// </summary>
public sealed record InventoryBomCount(long Count, long? Eligible = null, long? Loaded = null, long? Disabled = null, bool Collected = true);

/// <summary>A connector the output named but this app could not use, and why (the TUI skips such a connector without a word).</summary>
public sealed record InventoryBomSkipped(string Name, string Reason);

/// <summary>
/// One connector's <c>aibom scan --json</c> inventory (the TUI's <c>InventorySnapshot</c>), kept bounded: at most
/// <see cref="InventoryBomSnapshot.MaxRowsPerCategory"/> rows of a kind (<see cref="Dropped"/> says how many more there were), and only the
/// text a screen shows.
/// </summary>
public sealed class InventoryBomConnector
{
    private static readonly IReadOnlyList<InventoryBomEntity> NoEntities = [];
    private static readonly IReadOnlyDictionary<string, long> NoCounts = new Dictionary<string, long>();

    private readonly Dictionary<InventoryBomKind, IReadOnlyList<InventoryBomEntity>> _entities;
    private readonly Dictionary<InventoryBomKind, int> _dropped;

    internal InventoryBomConnector(
        Dictionary<InventoryBomKind, IReadOnlyList<InventoryBomEntity>> entities,
        Dictionary<InventoryBomKind, int> dropped)
    {
        _entities = entities;
        _dropped = dropped;
    }

    /// <summary>The connector's name as the inventory spells it (<c>claudecode</c>); every one of its rows carries it.</summary>
    public required string Name { get; init; }

    /// <summary>The inventory's schema number (<c>version</c>: 3 for 0.8.10).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>When the inventory was built (<c>generated_at</c>, as printed).</summary>
    public string GeneratedAt { get; init; } = string.Empty;

    /// <summary>The connector's home (<c>connector_home</c>, else <c>claw_home</c>).</summary>
    public string Home { get; init; } = string.Empty;

    /// <summary>The connector's first config file (<c>connector_config_files[0]</c>, else <c>openclaw_config</c>), the TUI's "Config".</summary>
    public string Config { get; init; } = string.Empty;

    /// <summary>The summary's <c>total_items</c>, or the sum of the counts when the summary has none.</summary>
    public long TotalItems { get; init; }

    /// <summary>Each kind's count: the summary's, else the length of its array.</summary>
    public IReadOnlyDictionary<InventoryBomKind, InventoryBomCount> Counts { get; init; } = new Dictionary<InventoryBomKind, InventoryBomCount>();

    /// <summary>The newer runtime's count of rule files, from <c>summary.rules</c>; null when the summary has none (0.8.10's has none).</summary>
    public long? Rules { get; init; }

    /// <summary>How many inventory commands failed (<c>summary.errors</c>, else the length of <c>errors</c>).</summary>
    public long ErrorCount { get; init; }

    /// <summary>The failed commands, with the reason each gave (at most <see cref="InventoryBomSnapshot.MaxErrorsPerConnector"/>).</summary>
    public IReadOnlyList<InventoryBomError> Errors { get; init; } = [];

    /// <summary>How many capabilities the connector cannot inventory (<c>summary.limitations</c>, else the length of <c>limitations</c>).</summary>
    public long LimitationCount { get; init; }

    /// <summary>What the connector cannot inventory, and why.</summary>
    public IReadOnlyList<InventoryBomLimitation> Limitations { get; init; } = [];

    /// <summary>The summary's policy verdict tallies by kind (<c>policy_skills</c>, <c>policy_plugins</c>, <c>policy_mcp</c>): blocked, allowed, clean, ...</summary>
    public IReadOnlyDictionary<InventoryBomKind, IReadOnlyDictionary<string, long>> PolicyVerdicts { get; init; } =
        new Dictionary<InventoryBomKind, IReadOnlyDictionary<string, long>>();

    /// <summary>The summary's scan coverage by kind (<c>scan_skills</c>, <c>scan_plugins</c>, <c>scan_mcp</c>): scanned, unscanned, total_findings.</summary>
    public IReadOnlyDictionary<InventoryBomKind, IReadOnlyDictionary<string, long>> ScanCoverage { get; init; } =
        new Dictionary<InventoryBomKind, IReadOnlyDictionary<string, long>>();

    /// <summary>The kind's rows, in the order the CLI printed them.</summary>
    public IReadOnlyList<InventoryBomEntity> Entities(InventoryBomKind kind) =>
        _entities.TryGetValue(kind, out var rows) ? rows : NoEntities;

    /// <summary>How many rows of the kind were left out because there were more than <see cref="InventoryBomSnapshot.MaxRowsPerCategory"/>.</summary>
    public int Dropped(InventoryBomKind kind) => _dropped.GetValueOrDefault(kind);

    /// <summary>The kind's count, or null when the connector's output said nothing about it.</summary>
    public InventoryBomCount? Count(InventoryBomKind kind) => Counts.TryGetValue(kind, out var count) ? count : null;

    /// <summary>The verdict tally of a kind, empty when the summary had none.</summary>
    public IReadOnlyDictionary<string, long> Verdicts(InventoryBomKind kind) =>
        PolicyVerdicts.TryGetValue(kind, out var map) ? map : NoCounts;

    /// <summary>The scan coverage of a kind, empty when the summary had none.</summary>
    public IReadOnlyDictionary<string, long> Coverage(InventoryBomKind kind) =>
        ScanCoverage.TryGetValue(kind, out var map) ? map : NoCounts;
}

/// <summary>What became of an attempt to read the output of <c>aibom scan --json</c>.</summary>
public enum InventoryBomParseStatus
{
    /// <summary>It was read; <see cref="InventoryBomParseResult.Snapshot"/> holds it (possibly with no connector, or with some skipped).</summary>
    Parsed,

    /// <summary>It was over <see cref="InventoryBomSnapshot.MaxOutputBytes"/>: nothing was parsed or kept.</summary>
    TooLarge,

    /// <summary>It was not the JSON the command prints.</summary>
    NotJson,
}

/// <summary>The outcome of <see cref="InventoryBomSnapshot.Parse"/>: a snapshot, or the reason there is none. Never an exception.</summary>
/// <param name="Status">What became of the output.</param>
/// <param name="Snapshot">The inventory; non-null exactly when <paramref name="Status"/> is <see cref="InventoryBomParseStatus.Parsed"/>.</param>
/// <param name="Message">
/// For <see cref="InventoryBomParseStatus.TooLarge"/> the size sentence ("aibom scan output is 5.3 MB, over the 4 MB limit", the wording
/// <see cref="OversizedValue.Reason"/> uses for every other oversized value); for <see cref="InventoryBomParseStatus.NotJson"/> the parser's complaint.
/// </param>
public sealed record InventoryBomParseResult(InventoryBomParseStatus Status, InventoryBomSnapshot? Snapshot, string Message)
{
    public bool Succeeded => Status == InventoryBomParseStatus.Parsed;
}

/// <summary>
/// The parsed output of <c>defenseclaw aibom scan --json</c>: one <see cref="InventoryBomConnector"/> per connector it printed, kept so the
/// Inventory page can browse them (the TUI's <c>connector_snapshots</c> and merged view in one).
/// <para>
/// <b>Shape.</b> A bare object when exactly one connector was inventoried and a list when several were (<c>cmd_aibom.py</c> 111-118), <c>[]</c>
/// when none is active. Each connector's object is the inventory dict of <c>claw_inventory.py</c> (<c>build_claw_aibom</c>, 104-174, and
/// <c>_build_aibom_from_filesystem</c>, 2091-2177): <c>version</c>, <c>generated_at</c>, <c>connector</c>, <c>claw_home</c>, <c>claw_mode</c>, the seven
/// category arrays, <c>errors</c>, <c>limitations</c>, the <c>connector_*</c> paths and a <c>summary</c> (<c>_build_summary</c>, 724-747, plus the
/// <c>policy_*</c> and <c>scan_*</c> tallies policy enrichment adds, 221-327). The rows are read the way the TUI's
/// <c>tui/services/inventory_state.py</c> reads them, which is what <see cref="InventoryBomEntity.Cells"/> and <see cref="InventoryBomEntity.Fields"/> follow.
/// </para>
/// <para>
/// <b>Bounded.</b> Output over <see cref="MaxOutputBytes"/> (the Mac's 4 MiB parse limit) is not parsed at all
/// (<see cref="InventoryBomParseStatus.TooLarge"/>); what is parsed keeps at most <see cref="MaxRowsPerCategory"/> rows of a kind per connector, at most
/// <see cref="MaxMoreFields"/> extra members of a row, and no text longer than <see cref="MaxValueCharacters"/>. The raw JSON is not kept.
/// </para>
/// <para>
/// <b>A connector that cannot be used is skipped and named</b> (<see cref="Skipped"/>), where the TUI drops it silently: an entry that is not an
/// object, one that is not an inventory at all, one whose rows could not be read. The others are kept. A connector whose own <c>errors</c> list
/// names failed commands is not skipped: it is kept with what it did list, and the failures are on <see cref="InventoryBomConnector.Errors"/>.
/// </para>
/// <para>
/// <b>Newer fields are read by presence</b>, never by asking which runtime printed them: <c>summary.rules</c>, a category's <c>collected</c> flag, a
/// plugin's <c>source_kind</c>, a tool's <c>kind</c> and <c>description</c>, and every other member of a row are used when the row has them and are
/// simply absent when it does not (0.8.10's output has none of the first four). Nothing is invented for a member that is missing.
/// </para>
/// </summary>
public sealed class InventoryBomSnapshot
{
    /// <summary>The most output text (UTF-8 bytes) that is parsed: 4 MiB, the Mac's <c>maximumInputBytes</c>. Over it, nothing is kept.</summary>
    public const int MaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>The most rows of one kind kept per connector; the summary's count still says how many there were.</summary>
    public const int MaxRowsPerCategory = 5_000;

    /// <summary>The most extra members (<see cref="InventoryBomEntity.More"/>) kept for a row.</summary>
    public const int MaxMoreFields = 24;

    /// <summary>The most characters kept of any one value; a longer one is cut and ends in an ellipsis.</summary>
    public const int MaxValueCharacters = 512;

    /// <summary>The most failed commands kept per connector.</summary>
    public const int MaxErrorsPerConnector = 50;

    /// <summary>The most skipped connectors named; a runaway list must not turn a note into a page.</summary>
    public const int MaxSkipped = 20;

    /// <summary>Text shorter than this is not put through <see cref="DisplayRedaction"/>: no credential assignment fits in it, and a table of short flags stays cheap.</summary>
    private const int RedactionMinimumLength = 8;

    private InventoryBomSnapshot(IReadOnlyList<InventoryBomConnector> connectors, IReadOnlyList<InventoryBomSkipped> skipped)
    {
        Connectors = connectors;
        Skipped = skipped;
    }

    /// <summary>The connectors that could be read, in the order the output listed them.</summary>
    public IReadOnlyList<InventoryBomConnector> Connectors { get; }

    /// <summary>The connectors that could not, each named with the reason.</summary>
    public IReadOnlyList<InventoryBomSkipped> Skipped { get; }

    /// <summary>Several connectors were inventoried: the TUI's cue for a Connector column.</summary>
    public bool HasSeveralConnectors => Connectors.Count > 1;

    /// <summary>The connector with this name (case does not matter), or null.</summary>
    public InventoryBomConnector? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Connectors.FirstOrDefault(c => string.Equals(c.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the output of <c>aibom scan --json</c>. Never throws for what the output holds: output over the cap is
    /// <see cref="InventoryBomParseStatus.TooLarge"/>, output without the JSON is <see cref="InventoryBomParseStatus.NotJson"/>, and a connector
    /// that cannot be read is skipped and named.
    /// </summary>
    /// <param name="output">The command's standard output. Anything before the first <c>{</c> or <c>[</c> (a banner) is ignored.</param>
    /// <param name="requestedConnector">The <c>--connector</c> the command was given, which names a bare object that carries no connector of its own.</param>
    public static InventoryBomParseResult Parse(string? output, string? requestedConnector = null)
    {
        var text = output ?? string.Empty;

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxOutputBytes)
        {
            return new InventoryBomParseResult(
                InventoryBomParseStatus.TooLarge,
                null,
                new OversizedValue("aibom scan output", bytes, MaxOutputBytes).Reason);
        }

        var start = text.AsSpan().IndexOfAny('{', '[');
        if (start < 0)
        {
            return NotJson("there is no JSON in the output");
        }

        try
        {
            using var document = JsonDocument.Parse(text.AsMemory(start));
            return new InventoryBomParseResult(InventoryBomParseStatus.Parsed, Read(document.RootElement, requestedConnector), string.Empty);
        }
        catch (JsonException ex)
        {
            return NotJson(ex.Message);
        }
    }

    private static InventoryBomParseResult NotJson(string message) =>
        new(InventoryBomParseStatus.NotJson, null, message);

    private static InventoryBomSnapshot Read(JsonElement root, string? requestedConnector)
    {
        var connectors = new List<InventoryBomConnector>();
        var skipped = new List<InventoryBomSkipped>();

        // A bare object for one connector, a list for several, [] for none.
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        var number = 0;
        foreach (var item in items)
        {
            number++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                Skip($"entry {number}", $"it is {Describe(item.ValueKind)}, not an inventory object");
                continue;
            }

            var name = Or(Text(item, "connector"), Text(item, "claw_mode"), requestedConnector?.Trim() ?? string.Empty);
            var label = name.Length > 0 ? Shown(name) : $"entry {number}";
            if (!LooksLikeInventory(item))
            {
                Skip(label, "it is not an inventory (no connector, summary or category list)");
                continue;
            }

            try
            {
                connectors.Add(ReadConnector(item, name.Length > 0 ? Shown(name) : $"connector {number}"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException or KeyNotFoundException)
            {
                Skip(label, "its rows could not be read: " + Shown(ex.Message));
            }
        }

        return new InventoryBomSnapshot(connectors, skipped);

        void Skip(string name, string reason)
        {
            if (skipped.Count < MaxSkipped)
            {
                skipped.Add(new InventoryBomSkipped(name, reason));
            }
        }
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "not an object",
    };

    /// <summary>The Mac's <c>isInventoryDocument</c>, widened to the category lists: a connector name, a summary or any array of rows.</summary>
    private static bool LooksLikeInventory(JsonElement item)
    {
        if (item.TryGetProperty("connector", out _) || item.TryGetProperty("claw_mode", out _) || item.TryGetProperty("summary", out _))
        {
            return true;
        }

        return InventoryBomKinds.All.Any(kind => item.TryGetProperty(kind.JsonKey(), out var rows) && rows.ValueKind == JsonValueKind.Array);
    }

    // ------------------------------------------------------------------ one connector

    private static InventoryBomConnector ReadConnector(JsonElement item, string name)
    {
        var entities = new Dictionary<InventoryBomKind, IReadOnlyList<InventoryBomEntity>>();
        var dropped = new Dictionary<InventoryBomKind, int>();
        foreach (var kind in InventoryBomKinds.All)
        {
            var rows = new List<InventoryBomEntity>();
            var total = 0;
            if (item.TryGetProperty(kind.JsonKey(), out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in array.EnumerateArray())
                {
                    // The TUI keeps only the mappings of a list.
                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    total++;
                    if (rows.Count < MaxRowsPerCategory)
                    {
                        rows.Add(InventoryBomRows.Read(kind, name, row));
                    }
                }
            }

            entities[kind] = rows;
            if (total > rows.Count)
            {
                dropped[kind] = total - rows.Count;
            }
        }

        var summary = item.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;

        var counts = new Dictionary<InventoryBomKind, InventoryBomCount>();
        foreach (var kind in InventoryBomKinds.All)
        {
            var arrayLength = item.TryGetProperty(kind.JsonKey(), out var rows) && rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() : (int?)null;
            if (ReadCount(summary, kind, arrayLength) is { } count)
            {
                counts[kind] = count;
            }
        }

        var errors = ReadErrors(item);
        var limitations = ReadLimitations(item, name);
        var configFiles = StringList(item, "connector_config_files");

        return new InventoryBomConnector(entities, dropped)
        {
            Name = name,
            Version = Shown(Text(item, "version")),
            GeneratedAt = Shown(Text(item, "generated_at")),
            Home = Shown(Or(Text(item, "connector_home"), Text(item, "claw_home"))),
            Config = Shown(Or(configFiles.FirstOrDefault() ?? string.Empty, Text(item, "connector_config"), Text(item, "openclaw_config"))),
            Counts = counts,
            TotalItems = summary is { } sm && Number(sm, "total_items") is { } total2 ? total2 : counts.Values.Sum(c => c.Count),
            Rules = summary is { } sr ? ReadCount(sr, "rules")?.Count : null,
            ErrorCount = summary is { } se && Number(se, "errors") is { } ec ? ec : errors.Total,
            Errors = errors.Rows,
            LimitationCount = summary is { } sl && Number(sl, "limitations") is { } lc ? lc : limitations.Count,
            Limitations = limitations,
            PolicyVerdicts = ReadTallies(summary, "policy_"),
            ScanCoverage = ReadTallies(summary, "scan_"),
        };
    }

    /// <summary>
    /// A kind's count the way the TUI reads it (<c>_map_val(summary.X, "count")</c>) when the summary has one, whether a <c>{"count": n, ...}</c>
    /// object or a bare number; else the length of the kind's own list when the output had one; else nothing.
    /// </summary>
    private static InventoryBomCount? ReadCount(JsonElement? summary, InventoryBomKind kind, int? arrayLength)
    {
        if (summary is { } s && ReadCount(s, kind.JsonKey()) is { } fromSummary)
        {
            return fromSummary;
        }

        return arrayLength is { } length ? new InventoryBomCount(length) : null;
    }

    private static InventoryBomCount? ReadCount(JsonElement summary, string key)
    {
        if (!summary.TryGetProperty(key, out var entry))
        {
            return null;
        }

        if (entry.ValueKind == JsonValueKind.Number)
        {
            return Number(entry) is { } bare ? new InventoryBomCount(bare) : null;
        }

        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("count", out var count) || Number(count) is not { } n)
        {
            return null;
        }

        return new InventoryBomCount(
            n,
            Eligible: Number(entry, "eligible"),
            Loaded: Number(entry, "loaded"),
            Disabled: Number(entry, "disabled"),
            // The newer runtime marks a kind an --only run did not collect; 0.8.10 never says.
            Collected: !(entry.TryGetProperty("collected", out var collected) && collected.ValueKind == JsonValueKind.False));
    }

    private static Dictionary<InventoryBomKind, IReadOnlyDictionary<string, long>> ReadTallies(JsonElement? summary, string prefix)
    {
        var result = new Dictionary<InventoryBomKind, IReadOnlyDictionary<string, long>>();
        if (summary is not { } s)
        {
            return result;
        }

        // The kinds policy enrichment tallies (claw_inventory.py _POLICY_CATEGORIES): skills, plugins and MCP servers.
        foreach (var kind in new[] { InventoryBomKind.Skills, InventoryBomKind.Plugins, InventoryBomKind.Mcp })
        {
            if (!s.TryGetProperty(prefix + kind.JsonKey(), out var map) || map.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tally = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var member in map.EnumerateObject())
            {
                if (Number(member.Value) is { } value)
                {
                    tally[Shown(member.Name)] = value;
                }
            }

            result[kind] = tally;
        }

        return result;
    }

    private static (IReadOnlyList<InventoryBomError> Rows, long Total) ReadErrors(JsonElement item)
    {
        var rows = new List<InventoryBomError>();
        long total = 0;
        if (item.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                total++;
                if (rows.Count >= MaxErrorsPerConnector)
                {
                    continue;
                }

                rows.Add(error.ValueKind == JsonValueKind.Object
                    ? new InventoryBomError(Shown(Text(error, "command")), Shown(Or(Text(error, "error"), Text(error, "message"))))
                    : new InventoryBomError(string.Empty, Shown(TextOf(error))));
            }
        }

        return (rows, total);
    }

    private static List<InventoryBomLimitation> ReadLimitations(JsonElement item, string connector)
    {
        var rows = new List<InventoryBomLimitation>();
        if (item.TryGetProperty("limitations", out var limitations) && limitations.ValueKind == JsonValueKind.Array)
        {
            foreach (var limitation in limitations.EnumerateArray())
            {
                if (limitation.ValueKind != JsonValueKind.Object || rows.Count >= MaxErrorsPerConnector)
                {
                    continue;
                }

                rows.Add(new InventoryBomLimitation(
                    Shown(Or(Text(limitation, "connector"), connector)),
                    Shown(Text(limitation, "category")),
                    Shown(Or(Text(limitation, "status"), "unsupported")),
                    Shown(Text(limitation, "reason"))));
            }
        }

        return rows;
    }

    // ------------------------------------------------------------------ reading JSON the way the TUI's Python does

    /// <summary>Python's <c>str(raw.get(name) or "")</c>: a missing, null, false, zero or empty value is empty text.</summary>
    internal static string Text(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) ? TextOf(value) : string.Empty;

    internal static string TextOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText() is "0" or "0.0" ? string.Empty : value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.Array => value.GetArrayLength() == 0 ? string.Empty : Compact(value),
        JsonValueKind.Object => value.EnumerateObject().Any() ? Compact(value) : string.Empty,
        _ => string.Empty,
    };

    /// <summary>Python's <c>bool(raw.get(name))</c>.</summary>
    internal static bool Truthy(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => Number(value) != 0,
            JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => false,
        };

    /// <summary>Python's <c>int(raw.get(name) or 0)</c>, except that text which is not a number is 0 here and an error there.</summary>
    internal static long IntOf(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) ? Number(value) ?? 0 : 0;

    internal static long? Number(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) ? Number(value) : null;

    internal static long? Number(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var whole))
                {
                    return whole;
                }

                return value.TryGetDouble(out var real) && double.IsFinite(real) && Math.Abs(real) < long.MaxValue ? (long)real : null;
            case JsonValueKind.String:
                return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    /// <summary>The strings of a list member; empty when the member is missing or is not a list (the TUI would walk the characters of a string).</summary>
    internal static List<string> StringList(JsonElement owner, string name)
    {
        var result = new List<string>();
        if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                result.Add(entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? string.Empty : TextOf(entry));
            }
        }

        return result;
    }

    /// <summary>The first of the texts that is not empty (Python's <c>a or b</c>).</summary>
    internal static string Or(params string[] texts) => texts.FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? string.Empty;

    // Shown to a person and never embedded in HTML: the default encoder would print "résumé" as backslash-u escapes.
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A JSON value on one line, with no more whitespace than it needs.</summary>
    internal static string Compact(JsonElement value) => JsonSerializer.Serialize(value, CompactOptions);

    /// <summary>
    /// Text for the screen: cut to <see cref="MaxValueCharacters"/> and with control, bidirectional and invisible characters spelled out
    /// (<see cref="DisplayNames.Visible"/>), so a name an agent chose cannot reverse, hide or break the line it is read in.
    /// </summary>
    internal static string Shown(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var bounded = text.Length > MaxValueCharacters ? text[..(MaxValueCharacters - 1)] + "…" : text;
        return DisplayNames.Visible(bounded);
    }

    /// <summary>
    /// <see cref="Shown"/> for free text that can hold a credential - an MCP server's command line and URL, a description, any member the TUI
    /// does not show - with the credentials masked first (<see cref="DisplayRedaction"/>, the masking every panel applies to what the runtime
    /// printed) and only then cut, so a credential that straddles the limit does not leave its first characters readable.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="commandLine">
    /// True for a command line or URL, where <c>--password hunter22</c> is an assignment; false for prose, where "reset your password to continue" is
    /// a sentence (<see cref="DisplayRedaction.Prose"/>).
    /// </param>
    internal static string Masked(string? text, bool commandLine = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Anything past the window is cut by Shown anyway, so masking stops there instead of working through a megabyte.
        var window = text.Length > 4 * MaxValueCharacters ? text[..(4 * MaxValueCharacters)] : text;
        if (window.Length >= RedactionMinimumLength)
        {
            window = commandLine ? DisplayRedaction.Text(window, int.MaxValue) : DisplayRedaction.Prose(window, int.MaxValue);
        }

        return Shown(window);
    }
}

/// <summary>Maps one JSON row of each kind to an <see cref="InventoryBomEntity"/>: the TUI's <c>from_mapping</c>, <c>data_table_rows</c> and <c>detail_info</c>.</summary>
internal static class InventoryBomRows
{
    private static readonly HashSet<string> SkillKeys = new(StringComparer.Ordinal)
    {
        "id", "source", "eligible", "enabled", "bundled", "description", "policy_verdict", "verdict", "policy_detail", "verdict_detail",
        "scan_findings", "scan_severity", "scan_target",
    };

    private static readonly HashSet<string> PluginKeys = new(StringComparer.Ordinal)
    {
        "id", "name", "version", "origin", "enabled", "status", "policy_verdict", "verdict", "policy_detail", "verdict_detail",
        "scan_findings", "scan_severity", "scan_target",
    };

    private static readonly HashSet<string> McpKeys = new(StringComparer.Ordinal) { "id", "source", "transport", "command", "url" };

    private static readonly HashSet<string> AgentKeys = new(StringComparer.Ordinal)
    {
        "id", "model", "workspace", "is_default", "default", "source", "subagents_max_concurrent", "max_concurrent",
    };

    private static readonly HashSet<string> ToolKeys = new(StringComparer.Ordinal) { "id", "name", "kind", "source", "description" };

    private static readonly HashSet<string> ModelKeys = new(StringComparer.Ordinal)
    {
        "id", "source", "default_model", "fallbacks", "allowed", "config_path", "status",
    };

    private static readonly HashSet<string> MemoryKeys = new(StringComparer.Ordinal)
    {
        "id", "backend", "files", "chunks", "db_path", "provider", "sources", "workspace", "fts_available", "vector_enabled",
    };

    public static InventoryBomEntity Read(InventoryBomKind kind, string connector, JsonElement row) => kind switch
    {
        InventoryBomKind.Skills => Skill(connector, row),
        InventoryBomKind.Plugins => Plugin(connector, row),
        InventoryBomKind.Mcp => Mcp(connector, row),
        InventoryBomKind.Agents => Agent(connector, row),
        InventoryBomKind.Tools => Tool(connector, row),
        InventoryBomKind.Models => Model(connector, row),
        InventoryBomKind.Memory => Memory(connector, row),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Flag(bool value) => value ? "true" : "false";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Shown(string? text) => InventoryBomSnapshot.Shown(text);

    private static string Text(JsonElement row, string name) => Shown(InventoryBomSnapshot.Text(row, name));

    /// <summary>A sentence from the row (a description), with credentials masked.</summary>
    private static string Prose(JsonElement row, string name) => InventoryBomSnapshot.Masked(InventoryBomSnapshot.Text(row, name));

    /// <summary>A command line or URL from the row, with credentials masked.</summary>
    private static string CommandLine(JsonElement row, string name) =>
        InventoryBomSnapshot.Masked(InventoryBomSnapshot.Text(row, name), commandLine: true);

    private static string List(JsonElement row, string name) =>
        string.Join(", ", InventoryBomSnapshot.StringList(row, name).Select(Shown));

    private static InventoryBomField F(string label, string value) => new(label, value);

    /// <summary>The members whose values the mapping reads as a number (<see cref="More"/> shows one that is not a number after all).</summary>
    private static readonly string[] NumericKeys = ["scan_findings", "subagents_max_concurrent", "max_concurrent", "files", "chunks"];

    /// <summary>The members whose values the mapping reads as a list.</summary>
    private static readonly string[] ListKeys = ["fallbacks", "allowed", "sources"];

    /// <summary>Members that hold a command line or a URL, where a credential is written as a flag or in the query.</summary>
    private static readonly HashSet<string> CommandLineKeys = new(StringComparer.Ordinal) { "args", "command", "url", "base_url", "endpoint" };

    private static InventoryBomEntity Skill(string connector, JsonElement row)
    {
        var id = Text(row, "id");
        var verdict = Shown(InventoryBomSnapshot.Or(InventoryBomSnapshot.Text(row, "policy_verdict"), InventoryBomSnapshot.Text(row, "verdict")));
        var detail = Shown(InventoryBomSnapshot.Or(InventoryBomSnapshot.Text(row, "policy_detail"), InventoryBomSnapshot.Text(row, "verdict_detail")));
        var eligible = InventoryBomSnapshot.Truthy(row, "eligible");
        var enabled = InventoryBomSnapshot.Truthy(row, "enabled");
        var findings = Count(InventoryBomSnapshot.IntOf(row, "scan_findings"));
        var severity = Text(row, "scan_severity");
        var source = Text(row, "source");
        var description = Prose(row, "description");

        var fields = new List<InventoryBomField>
        {
            F("Source", source),
            F("Eligible", Flag(eligible)),
            F("Enabled", Flag(enabled)),
            F("Bundled", Flag(InventoryBomSnapshot.Truthy(row, "bundled"))),
            F("Verdict", verdict),
            F("Detail", detail),
            F("Scan Findings", findings),
            F("Scan Severity", severity),
            F("Scan Target", Text(row, "scan_target")),
        };
        if (description.Length > 0)
        {
            fields.Insert(0, F("Description", description));
        }

        return new InventoryBomEntity(
            InventoryBomKind.Skills,
            connector,
            id,
            "SKILL: " + id,
            [id, verdict, YesNo(enabled), severity, findings, source],
            fields,
            More(row, SkillKeys),
            verdict,
            string.Empty,
            eligible);
    }

    private static InventoryBomEntity Plugin(string connector, JsonElement row)
    {
        var id = Text(row, "id");
        var name = Text(row, "name");
        var display = name.Length > 0 ? name : id;
        var verdict = Shown(InventoryBomSnapshot.Or(InventoryBomSnapshot.Text(row, "policy_verdict"), InventoryBomSnapshot.Text(row, "verdict")));
        var detail = Shown(InventoryBomSnapshot.Or(InventoryBomSnapshot.Text(row, "policy_detail"), InventoryBomSnapshot.Text(row, "verdict_detail")));
        var enabled = InventoryBomSnapshot.Truthy(row, "enabled");
        var findings = Count(InventoryBomSnapshot.IntOf(row, "scan_findings"));
        var severity = Text(row, "scan_severity");

        // A plugin row of the newer runtime that has no origin or status says where it came from (source_kind) and whether it is enabled; 0.8.10's
        // rows always have both, so for them these fallbacks are never used.
        var origin = Text(row, "origin");
        var keys = PluginKeys;
        if (origin.Length == 0 && InventoryBomSnapshot.Text(row, "source_kind").Length > 0)
        {
            origin = Text(row, "source_kind");
            keys = [.. PluginKeys, "source_kind"];
        }

        var status = Text(row, "status");
        if (status.Length == 0 && row.TryGetProperty("enabled", out _))
        {
            status = enabled ? "enabled" : "disabled";
        }

        var version = Text(row, "version");
        return new InventoryBomEntity(
            InventoryBomKind.Plugins,
            connector,
            id,
            "PLUGIN: " + display,
            [display, version, origin, status, verdict, findings, severity],
            [
                F("ID", id),
                F("Version", version),
                F("Origin", origin),
                F("Status", status),
                F("Enabled", Flag(enabled)),
                F("Verdict", verdict),
                F("Detail", detail),
                F("Scan Findings", findings),
                F("Scan Severity", severity),
                F("Scan Target", Text(row, "scan_target")),
            ],
            More(row, keys),
            verdict,
            status,
            false);
    }

    private static InventoryBomEntity Mcp(string connector, JsonElement row)
    {
        var id = Text(row, "id");
        var source = Text(row, "source");
        var transport = Text(row, "transport");
        var command = CommandLine(row, "command");
        var url = CommandLine(row, "url");
        return new InventoryBomEntity(
            InventoryBomKind.Mcp,
            connector,
            id,
            "MCP: " + id,
            [id, source, transport, command.Length > 0 ? command : url],
            [F("Source", source), F("Transport", transport), F("Command", command), F("URL", url)],
            More(row, McpKeys),
            string.Empty,
            string.Empty,
            false);
    }

    private static InventoryBomEntity Agent(string connector, JsonElement row)
    {
        var id = Text(row, "id");
        var model = Text(row, "model");
        var workspace = Text(row, "workspace");
        var source = Text(row, "source");
        var isDefault = InventoryBomSnapshot.Truthy(row, "is_default") || InventoryBomSnapshot.Truthy(row, "default");
        var maxConcurrent = InventoryBomSnapshot.IntOf(row, "subagents_max_concurrent");
        if (maxConcurrent == 0)
        {
            maxConcurrent = InventoryBomSnapshot.IntOf(row, "max_concurrent");
        }

        return new InventoryBomEntity(
            InventoryBomKind.Agents,
            connector,
            id,
            "AGENT: " + id,
            [id, source, model, workspace, YesNo(isDefault)],
            [
                F("Model", model),
                F("Workspace", workspace),
                F("Default", Flag(isDefault)),
                F("Source", source),
                F("Max Concurrent", Count(maxConcurrent)),
            ],
            More(row, AgentKeys),
            string.Empty,
            string.Empty,
            false);
    }

    private static InventoryBomEntity Tool(string connector, JsonElement row)
    {
        var name = Text(row, "name");
        var id = Text(row, "id");
        if (id.Length == 0)
        {
            id = name;
        }

        var display = name.Length > 0 ? name : id;
        var kind = Text(row, "kind");
        var source = Text(row, "source");
        return new InventoryBomEntity(
            InventoryBomKind.Tools,
            connector,
            id,
            "TOOL: " + display,
            [id, display, kind, source],
            [F("ID", id), F("Kind", kind), F("Source", source), F("Description", Prose(row, "description"))],
            More(row, ToolKeys),
            string.Empty,
            string.Empty,
            false);
    }

    private static InventoryBomEntity Model(string connector, JsonElement row)
    {
        var id = Text(row, "id");
        var source = Text(row, "source");
        var defaultModel = Text(row, "default_model");
        var status = Text(row, "status");
        var fallbacks = List(row, "fallbacks");
        var allowed = List(row, "allowed");

        var fields = new List<InventoryBomField>
        {
            F("Source", source),
            F("Default Model", defaultModel),
            F("Status", status),
            F("Config", Text(row, "config_path")),
        };
        if (fallbacks.Length > 0)
        {
            fields.Add(F("Fallbacks", fallbacks));
        }

        if (allowed.Length > 0)
        {
            fields.Add(F("Allowed", allowed));
        }

        return new InventoryBomEntity(
            InventoryBomKind.Models,
            connector,
            id,
            "MODEL: " + id,
            [id, source, defaultModel, status],
            fields,
            More(row, ModelKeys),
            string.Empty,
            string.Empty,
            false);
    }

    private static InventoryBomEntity Memory(string connector, JsonElement row)
    {
        var id = Text(row, "id");
        var backend = Text(row, "backend");
        var provider = Text(row, "provider");
        var workspace = Text(row, "workspace");
        var files = Count(InventoryBomSnapshot.IntOf(row, "files"));
        var chunks = Count(InventoryBomSnapshot.IntOf(row, "chunks"));
        var sources = List(row, "sources");

        var fields = new List<InventoryBomField>
        {
            F("Backend", backend),
            F("Provider", provider),
            F("Workspace", workspace),
            F("DB Path", Text(row, "db_path")),
            F("Files", files),
            F("Chunks", chunks),
            F("FTS Available", Flag(InventoryBomSnapshot.Truthy(row, "fts_available"))),
            F("Vector Enabled", Flag(InventoryBomSnapshot.Truthy(row, "vector_enabled"))),
        };
        if (sources.Length > 0)
        {
            fields.Add(F("Sources", sources));
        }

        return new InventoryBomEntity(
            InventoryBomKind.Memory,
            connector,
            id,
            "MEMORY: " + id,
            [id, backend, provider, files, chunks, workspace],
            fields,
            More(row, MemoryKeys),
            string.Empty,
            string.Empty,
            false);
    }

    /// <summary>
    /// The members of <paramref name="row"/> the TUI's detail does not show, in the order the CLI printed them: whatever the row carries beyond the
    /// TUI's fields, so a newer runtime's additions (a tool's <c>kind</c>, a skill's <c>scan_eligible</c>) and a filesystem connector's own
    /// (<c>path</c>, <c>entry_count</c>, <c>base_url</c>) appear exactly when they are there. Empty, null and <c>provenance</c> members are left out.
    /// </summary>
    private static List<InventoryBomField> More(JsonElement row, HashSet<string> shown)
    {
        var more = new List<InventoryBomField>();
        foreach (var member in row.EnumerateObject())
        {
            if (more.Count >= InventoryBomSnapshot.MaxMoreFields)
            {
                break;
            }

            if (member.Name == "provenance" || (shown.Contains(member.Name) && WasRead(member)))
            {
                continue;
            }

            var value = MemberText(member.Name, member.Value);
            if (value.Length == 0)
            {
                continue;
            }

            more.Add(F(Humanize(member.Name), InventoryBomSnapshot.Masked(value, CommandLineKeys.Contains(member.Name))));
        }

        return more;
    }

    /// <summary>
    /// Whether the mapping took the member's value into a field. One it reads as a number or a list that is something else (a memory row's
    /// <c>files</c> is a list of paths on the newer runtime, where the TUI counts files) was not shown, so it is not left out of the extras either.
    /// </summary>
    private static bool WasRead(JsonProperty member)
    {
        var value = member.Value;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (Array.IndexOf(NumericKeys, member.Name) >= 0)
        {
            return InventoryBomSnapshot.Number(value) is not null;
        }

        return Array.IndexOf(ListKeys, member.Name) < 0 || value.ValueKind == JsonValueKind.Array;
    }

    /// <summary>
    /// A member's value as text: a list of plain values joined by <c>, </c> (by a space for <c>args</c>, which is a command line), a nested object as
    /// compact JSON, a boolean as <c>true</c> / <c>false</c>.
    /// </summary>
    private static string MemberText(string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0)
                {
                    return string.Empty;
                }

                return value.EnumerateArray().All(IsPlain)
                    ? string.Join(name == "args" ? " " : ", ", value.EnumerateArray().Select(PlainText))
                    : InventoryBomSnapshot.Compact(value);
            case JsonValueKind.Object:
                return InventoryBomSnapshot.TextOf(value);
            case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return PlainText(value);
            default:
                return string.Empty;
        }
    }

    private static bool IsPlain(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    /// <summary>A string, number or boolean as the text a person reads. Unlike the TUI's <c>or ""</c>, a <c>0</c> and a <c>false</c> are values here.</summary>
    private static string PlainText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        _ => "false",
    };

    /// <summary><c>env_keys</c> as "Env Keys", <c>toolNames</c> as "Tool Names": a member name as a label.</summary>
    internal static string Humanize(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-')
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            if (char.IsUpper(c) && i > 0 && char.IsLower(name[i - 1]) && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(builder.Length == 0 || builder[^1] == ' ' ? char.ToUpperInvariant(c) : c);
        }

        return builder.ToString().Trim();
    }
}
