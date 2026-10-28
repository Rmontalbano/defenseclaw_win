using System.Globalization;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Correlation;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Core.Inventory;

/// <summary>
/// One extension or plugin of an IDE installation (<c>plugins[]</c> of <c>GET /api/v1/ai-usage/ide-plugins</c>; <c>inventory.IDEPlugin</c> in 1.0.x),
/// joined with its installation's version and remote kind. Paths never appear: the gateway sends hashes only.
/// </summary>
public sealed record IdePluginRow(
    string Fingerprint,
    string User,
    string InstallId,
    string Family,
    string Product,
    string IdeVersion,
    string RemoteKind,
    string PluginId,
    string DisplayName,
    string Publisher,
    string Version,
    string Enabled,
    string EnabledSource,
    string Scope,
    bool IsAi,
    string AiSignatureId,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? LastSeen)
{
    /// <summary>The CLI's Enabled cell (<c>ide_plugin_enabled_label</c>): yes, no, client side, unknown.</summary>
    public string EnabledLabel => Enabled switch
    {
        "enabled" => "yes",
        "disabled" => "no",
        "client_side_unknown" => "client side",
        "" => "unknown",
        _ => Enabled,
    };

    /// <summary>True for a plugin whose enabled state the runtime could not read (<c>unknown</c>, <c>client_side_unknown</c>, or none).</summary>
    public bool IsUnknown => Enabled is "" or "unknown" or "client_side_unknown";

    /// <summary>The CLI's IDE cell (<c>ide_plugin_ide_label</c>): the product and its version, and where it runs for a remote install.</summary>
    public string IdeLabel
    {
        get
        {
            var product = Product.Length > 0 ? Product : Family.Length > 0 ? Family : "-";
            if (IdeVersion.Length > 0)
            {
                product += " " + IdeVersion;
            }

            if (RemoteKind.Length > 0)
            {
                return product + " (" + (RemoteKind switch { "ssh_server" => "ssh", "jetbrains_remote_dev" => "remote dev", _ => "remote" }) + ")";
            }

            return Scope == "remote" ? product + " (remote)" : product;
        }
    }

    /// <summary>The CLI's Plugin cell: the id, and the display name when it differs.</summary>
    public string PluginLabel
    {
        get
        {
            if (DisplayName.Length > 0 && !string.Equals(DisplayName, PluginId, StringComparison.OrdinalIgnoreCase))
            {
                return PluginId.Length > 0 ? $"{PluginId} ({DisplayName})" : DisplayName;
            }

            return PluginId.Length > 0 ? PluginId : "-";
        }
    }
}

/// <summary>One IDE installation (or remote server) of the answer's <c>installations[]</c>.</summary>
public sealed record IdeInstallationRow(string InstallId, string User, string Family, string Product, string Version, string RemoteKind, bool Partial);

/// <summary>The answer's <c>counts</c>: the filtered rows across every page.</summary>
public sealed record IdeInventoryCounts(long Total, long Ai, long Disabled, long Users, long Installations);

/// <summary>
/// One read of the IDE plugin inventory.
/// </summary>
/// <param name="Enabled">False when AI discovery is off on the gateway (<c>enabled</c>): there is nothing to list, and <paramref name="Reason"/> says why.</param>
/// <param name="Scope">The gateway's <c>ai_discovery.ide_inventory</c> scope: <c>all</c>, <c>ai_only</c> or <c>off</c>.</param>
/// <param name="Reason">The gateway's own sentence for an empty answer (<c>reason</c>), or empty.</param>
/// <param name="Partial">The scan hit its limits and the inventory is incomplete.</param>
/// <param name="Rows">The plugins read, at most <see cref="IdeInventoryReader.MaxPlugins"/>.</param>
/// <param name="Total">How many plugins the gateway has, whatever this read holds.</param>
public sealed record IdePluginPage(
    bool Enabled,
    string Scope,
    string Reason,
    bool Partial,
    DateTimeOffset? ScannedAt,
    IReadOnlyList<IdePluginRow> Rows,
    IReadOnlyList<IdeInstallationRow> Installations,
    IdeInventoryCounts Counts,
    long Total,
    string NextCursor,
    int Skipped)
{
    /// <summary>True when the inventory is switched off (<c>ai_discovery.ide_inventory: off</c>).</summary>
    public bool IsOff => string.Equals(Scope, "off", StringComparison.Ordinal);
}

/// <summary>The route of the IDE plugin inventory and the limits a read of it works to.</summary>
public static class IdeInventoryRoutes
{
    public const string IdePlugins = "api/v1/ai-usage/ide-plugins";

    /// <summary>The gateway's largest <c>limit</c> (<c>idePluginsMaxLimit</c>).</summary>
    public const int MaxLimit = 1000;

    /// <summary>The most bytes of one page read (1000 plugins are well under 1 MiB).</summary>
    public const int MaxPageBytes = 4 * 1024 * 1024;

    /// <summary>The relative request path of one page.</summary>
    public static string Page(int limit, string? cursor)
    {
        var text = new StringBuilder(IdePlugins).Append("?limit=").Append(Math.Clamp(limit, 1, MaxLimit).ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(cursor))
        {
            _ = text.Append("&cursor=").Append(Uri.EscapeDataString(cursor));
        }

        return text.ToString();
    }
}

/// <summary>Reads the answer of <c>GET /api/v1/ai-usage/ide-plugins</c>, shapes from <c>handleAIUsageIDEPlugins</c> and <c>inventory.IDEPlugin</c> of 1.0.x.</summary>
public static class IdeInventoryReader
{
    /// <summary>The most plugins one read keeps (the Mac's bound).</summary>
    public const int MaxPlugins = 10_000;

    private const int MaxInstallations = 2_000;

    /// <summary>Reads the page in <paramref name="root"/>.</summary>
    public static CorrelationRead<IdePluginPage> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("plugins", out var list) || list.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
        {
            return new CorrelationRead<IdePluginPage>(
                CorrelationReadStatus.Malformed,
                null,
                "The IDE plugin answer is incomplete: it is missing plugins. Nothing here is an empty inventory.");
        }

        var installs = new List<IdeInstallationRow>();
        var byInstall = new Dictionary<string, IdeInstallationRow>(StringComparer.Ordinal);
        var skipped = 0;
        if (root.TryGetProperty("installations", out var installList) && installList.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in installList.EnumerateArray())
            {
                if (installs.Count >= MaxInstallations)
                {
                    break;
                }

                var id = e.ValueKind == JsonValueKind.Object ? AiRuntimeJson.Name(e, "install_id") : string.Empty;
                if (id.Length == 0)
                {
                    skipped++;
                    continue;
                }

                var row = new IdeInstallationRow(
                    id,
                    AiRuntimeJson.Name(e, "user"),
                    AiRuntimeJson.Name(e, "ide_family"),
                    AiRuntimeJson.Name(e, "ide_product"),
                    AiRuntimeJson.Name(e, "version"),
                    AiRuntimeJson.Name(e, "remote_kind"),
                    AiRuntimeJson.Flag(e, "partial"));
                installs.Add(row);
                byInstall[id] = row;
            }
        }

        var rows = new List<IdePluginRow>();
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                if (rows.Count >= MaxPlugins)
                {
                    break;
                }

                if (e.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }

                var plugin = AiRuntimeJson.Name(e, "plugin_id");
                var name = AiRuntimeJson.Name(e, "display_name");
                if (plugin.Length == 0 && name.Length == 0)
                {
                    skipped++;
                    continue;
                }

                var installId = AiRuntimeJson.Name(e, "install_id");
                byInstall.TryGetValue(installId, out var install);
                var user = AiRuntimeJson.Name(e, "user");
                rows.Add(new IdePluginRow(
                    AiRuntimeJson.Name(e, "fingerprint"),
                    user.Length > 0 ? user : AiRuntimeJson.Name(e, "user_id"),
                    installId,
                    AiRuntimeJson.Name(e, "ide_family"),
                    AiRuntimeJson.Name(e, "ide_product"),
                    install?.Version ?? string.Empty,
                    install?.RemoteKind ?? string.Empty,
                    plugin,
                    name,
                    AiRuntimeJson.Name(e, "publisher"),
                    AiRuntimeJson.Name(e, "version"),
                    AiRuntimeJson.Name(e, "enabled"),
                    AiRuntimeJson.Name(e, "enabled_source"),
                    AiRuntimeJson.Name(e, "scope"),
                    AiRuntimeJson.Flag(e, "is_ai"),
                    AiRuntimeJson.Name(e, "ai_signature_id"),
                    CorrelationReader.ParseTime(AiRuntimeJson.Str(e, "installed_at")),
                    CorrelationReader.ParseTime(AiRuntimeJson.Str(e, "last_seen"))));
            }
        }

        var counts = new IdeInventoryCounts(0, 0, 0, 0, 0);
        if (root.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            counts = new IdeInventoryCounts(
                AiRuntimeJson.Long(c, "total"),
                AiRuntimeJson.Long(c, "ai"),
                AiRuntimeJson.Long(c, "disabled"),
                AiRuntimeJson.Long(c, "users"),
                AiRuntimeJson.Long(c, "installations"));
        }

        var enabled = !root.TryGetProperty("enabled", out var flag) || flag.ValueKind != JsonValueKind.False;
        var page = new IdePluginPage(
            enabled,
            AiRuntimeJson.Name(root, "scope"),
            AiRuntimeJson.Prose(AiRuntimeJson.Str(root, "reason"), 256),
            AiRuntimeJson.Flag(root, "partial"),
            CorrelationReader.ParseTime(AiRuntimeJson.Str(root, "scanned_at")),
            rows,
            installs,
            counts,
            Math.Max(AiRuntimeJson.Long(root, "total"), rows.Count),
            AiRuntimeJson.Name(root, "next_cursor"),
            skipped);
        return new CorrelationRead<IdePluginPage>(CorrelationReadStatus.Ok, page, string.Empty);
    }

    /// <summary>Reads a page from JSON text (fixtures and tests); text that is not JSON is <see cref="CorrelationReadStatus.Malformed"/>.</summary>
    public static CorrelationRead<IdePluginPage> ParseText(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return new CorrelationRead<IdePluginPage>(CorrelationReadStatus.Malformed, null, "The gateway's answer was not JSON.");
        }
    }
}

/// <summary>
/// The read of the IDE plugin inventory, over the same fetch seam as <see cref="CorrelationService"/>. Every call is a GET: nothing here can change
/// state, and there is no process to start (the CLI's <c>agent ide-plugins</c> and <c>aibom scan --only ide_plugins</c> both read this same route).
/// </summary>
public sealed class IdeInventoryService(Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>> fetch)
{
    /// <summary>The most pages read in one go (10 x 1000 is the bound of <see cref="IdeInventoryReader.MaxPlugins"/>).</summary>
    public const int MaxPages = 10;

    private readonly Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>> _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));

    /// <summary>
    /// The inventory, page after page until the gateway has no next cursor or the bound is reached. A failure on a later page (a new scan finished and
    /// the cursor went stale, HTTP 409, among them) keeps what was read and says it is partial; a failure on the first is the failure.
    /// </summary>
    public async Task<CorrelationRead<IdePluginPage>> ReadAsync(CancellationToken cancellationToken)
    {
        var first = await ReadPageAsync(null, cancellationToken).ConfigureAwait(false);
        if (!first.IsOk || first.Value is not { Enabled: true } head || head.IsOff)
        {
            return first;
        }

        var rows = head.Rows.ToList();
        var installs = head.Installations.ToList();
        var skipped = head.Skipped;
        var next = head.NextCursor;
        var pages = 1;
        var note = string.Empty;
        while (next.Length > 0 && pages < MaxPages && rows.Count < IdeInventoryReader.MaxPlugins)
        {
            var more = await ReadPageAsync(next, cancellationToken).ConfigureAwait(false);
            if (!more.IsOk || more.Value is null)
            {
                note = "The list is partial: " + more.Message;
                break;
            }

            rows.AddRange(more.Value.Rows);
            installs.AddRange(more.Value.Installations);
            skipped += more.Value.Skipped;
            next = more.Value.NextCursor;
            pages++;
        }

        // A later page names the installations of its own plugins; the version and remote kind of an install seen first on page 2 are joined here.
        var byInstall = installs.GroupBy(i => i.InstallId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var joined = rows.Take(IdeInventoryReader.MaxPlugins)
            .Select(r => r.IdeVersion.Length == 0 && byInstall.TryGetValue(r.InstallId, out var i) ? r with { IdeVersion = i.Version, RemoteKind = i.RemoteKind } : r)
            .ToArray();
        var merged = head with { Rows = joined, Installations = byInstall.Values.ToArray(), Skipped = skipped, NextCursor = note.Length > 0 ? next : string.Empty };
        return new CorrelationRead<IdePluginPage>(CorrelationReadStatus.Ok, merged, note);
    }

    private async Task<CorrelationRead<IdePluginPage>> ReadPageAsync(string? cursor, CancellationToken cancellationToken)
    {
        var result = await _fetch(IdeInventoryRoutes.Page(IdeInventoryRoutes.MaxLimit, cursor), IdeInventoryRoutes.MaxPageBytes, cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                try
                {
                    return CorrelationReader.FromGateway(result, Parse);
                }
                finally
                {
                    result.Value?.Dispose();
                }
            },
            CancellationToken.None).ConfigureAwait(false);

        static CorrelationRead<IdePluginPage> Parse(JsonElement root) => IdeInventoryReader.Parse(root);
    }
}
