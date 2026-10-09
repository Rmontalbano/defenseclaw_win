using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.Core.Policy.Model;

/// <summary>What <c>guardrail list-packs --json</c> printed.</summary>
/// <param name="Global">The global scope's pack.</param>
/// <param name="Connectors">One row per active connector.</param>
/// <param name="Packs">Every rule pack the runtime knows.</param>
public sealed record ListPacksDocument(ScopePack? Global, IReadOnlyList<ScopePack> Connectors, IReadOnlyList<RulePackEntry> Packs);

/// <summary>One scope's row of <c>guardrail protection list --json</c>: its pack and the opt-in packs layered into it.</summary>
public sealed record ProtectionScope(string Scope, string Pack, string Folder, IReadOnlyList<string> Enabled);

/// <summary>What <c>guardrail protection list --json</c> printed.</summary>
public sealed record ProtectionDocument(IReadOnlyList<ProtectionPack> Packs, IReadOnlyList<ProtectionScope> Scopes);

/// <summary>A connector's own guardrail settings (<c>guardrail.connectors.NAME</c>); an unset value is empty (or null for the approval block).</summary>
/// <param name="Mode">The connector's own mode; empty when it follows the global one.</param>
/// <param name="HiltEnabled">The connector's own approval switch; null together with <paramref name="HiltMinSeverity"/> when the block is absent (it inherits the global one).</param>
/// <param name="HiltMinSeverity">The connector's own approval threshold.</param>
/// <param name="BlockAt">The connector's own tool-call block level.</param>
/// <param name="AlertAt">The connector's own tool-call alert level.</param>
public sealed record ConnectorGuardrailSettings(string Mode, bool? HiltEnabled, string? HiltMinSeverity, string BlockAt, string AlertAt)
{
    /// <summary>True when the connector carries its own approval block.</summary>
    public bool HasOwnHilt => HiltEnabled is not null;
}

/// <summary>The guardrail settings that decide a scope's posture, from <c>config show --section guardrail --format json</c>.</summary>
/// <param name="Mode">The global mode.</param>
/// <param name="HiltEnabled">The global approval switch.</param>
/// <param name="HiltMinSeverity">The global approval threshold.</param>
/// <param name="BlockAt">The global tool-call block level.</param>
/// <param name="AlertAt">The global tool-call alert level.</param>
/// <param name="Connectors">The per-connector blocks, by the name config.yaml gives them.</param>
public sealed record GuardrailSettings(
    string Mode,
    bool HiltEnabled,
    string HiltMinSeverity,
    string BlockAt,
    string AlertAt,
    IReadOnlyDictionary<string, ConnectorGuardrailSettings> Connectors)
{
    /// <summary>The block the settings hold for <paramref name="connector"/>: an exact key hit, else the key that names the same connector (<c>claude-code</c> for <c>claudecode</c>); null when there is none.</summary>
    public ConnectorGuardrailSettings? ConnectorBlock(string connector)
    {
        if (string.IsNullOrEmpty(connector) || Connectors.Count == 0)
        {
            return null;
        }

        if (Connectors.TryGetValue(connector, out var exact))
        {
            return exact;
        }

        var want = NormalizeConnector(connector);
        foreach (var (name, block) in Connectors)
        {
            if (string.Equals(NormalizeConnector(name), want, StringComparison.Ordinal))
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>The canonical lower-case connector name (<c>connector_paths.normalize</c>): <c>Claude-Code</c> is <c>claudecode</c>.</summary>
    public static string NormalizeConnector(string? connector)
    {
        if (string.IsNullOrEmpty(connector))
        {
            return "openclaw";
        }

        var name = connector.Trim().ToLowerInvariant();
        if (name is "open-hands" or "open_hands")
        {
            return "openhands";
        }

        if (name is "claude-code" or "claude_code")
        {
            return "claudecode";
        }

        return name.Length == 0 ? "openclaw" : name;
    }
}

/// <summary>The outcome of validating a rule pack: <c>valid</c>, <c>invalid</c> or <c>unavailable</c> (the validator could not run).</summary>
/// <param name="State"><c>valid</c>, <c>invalid</c> or <c>unavailable</c>.</param>
/// <param name="Message">Why it is not valid.</param>
/// <param name="RuleCount">How many rules the pack declares.</param>
/// <param name="EnabledRuleCount">How many of them are enabled.</param>
/// <param name="RuleFileCount">How many rule files the pack has.</param>
/// <param name="Digest">The pack's digest.</param>
public sealed record PackValidation(string State, string Message = "", int RuleCount = 0, int EnabledRuleCount = 0, int RuleFileCount = 0, string Digest = "")
{
    /// <summary>True only for a pack the validator accepted.</summary>
    public bool IsValid => string.Equals(State, "valid", StringComparison.Ordinal);

    /// <summary>One line on the outcome.</summary>
    public string Summary
    {
        get
        {
            if (IsValid)
            {
                var digest = Digest.Length > 0 ? $" · digest {(Digest.Length > 12 ? Digest[..12] : Digest)}" : string.Empty;
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"valid: {EnabledRuleCount}/{RuleCount} rules enabled across {RuleFileCount} files{digest}");
            }

            if (string.Equals(State, "invalid", StringComparison.Ordinal))
            {
                return $"invalid: {(Message.Length > 0 ? Message : "the pack did not load")}";
            }

            return $"validator unavailable: {(Message.Length > 0 ? Message : "defenseclaw-gateway could not check it")}";
        }
    }
}

/// <summary>
/// Reads the JSON the pinned runtime's read-only commands print for the Policies catalog. Defensive: a missing member reads as empty, a
/// member of the wrong type is ignored, and a document that is not what the command prints is an error with a sentence, never an empty
/// catalog (an empty read must not look like "no policies").
/// </summary>
public static class PolicyCatalogJson
{
    /// <summary>The longest document read (the catalog is a few kilobytes; this stops a runaway).</summary>
    public const int MaxDocumentChars = 4 * 1024 * 1024;

    // ---- policy list --json ------------------------------------------------------------------------------------------------

    /// <summary>Parses <c>policy list --json</c>: <c>{"version":1,"active":"default","policies":[...]}</c>.</summary>
    public static bool TryParsePolicies(string? text, out IReadOnlyList<NamedPolicy> policies, out string error)
    {
        policies = Array.Empty<NamedPolicy>();
        if (!TryRoot(text, "policy list --json", out var document, out error))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("policies", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                error = "policy list --json printed no policies list.";
                return false;
            }

            var found = new List<NamedPolicy>();
            foreach (var item in list.EnumerateArray())
            {
                if (Policy(item) is { } policy)
                {
                    found.Add(policy);
                }
            }

            policies = found;
            return true;
        }
    }

    /// <summary>One policy object (a <c>policy list</c> row or the <c>policy</c> member of <c>policy show --json</c>); null when it has no name.</summary>
    public static NamedPolicy? Policy(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        return new NamedPolicy(
            name,
            Text(item, "description"),
            Flag(item, "builtin"),
            Flag(item, "active"),
            Text(item, "path"),
            Text(item, "block_at"),
            Text(item, "alert_at"),
            Text(item, "install_block_at"),
            Text(item, "firewall_default"),
            NullableFlag(item, "hilt"),
            Whole(item, "scanner_overrides"),
            Flag(item, "adds_webhooks"),
            Flag(item, "sets_cisco"),
            Flag(item, "edited"));
    }

    // ---- guardrail list-packs --json ---------------------------------------------------------------------------------------

    /// <summary>Parses <c>guardrail list-packs --json</c>.</summary>
    public static bool TryParseListPacks(string? text, out ListPacksDocument document, out string error)
    {
        document = new ListPacksDocument(null, Array.Empty<ScopePack>(), Array.Empty<RulePackEntry>());
        if (!TryRoot(text, "guardrail list-packs --json", out var json, out error))
        {
            return false;
        }

        using (json)
        {
            var root = json.RootElement;
            if (!root.TryGetProperty("global", out var global) || global.ValueKind != JsonValueKind.Object)
            {
                error = "guardrail list-packs --json printed no global pack.";
                return false;
            }

            var connectors = new List<ScopePack>();
            if (root.TryGetProperty("connectors", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (ConnectorPack(row) is { } pack)
                    {
                        connectors.Add(pack);
                    }
                }
            }

            var packs = new List<RulePackEntry>();
            if (root.TryGetProperty("packs", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object && Text(entry, "name") is { Length: > 0 } name)
                    {
                        packs.Add(new RulePackEntry(name, Text(entry, "path"), Text(entry, "kind"), Strings(entry, "used_by")));
                    }
                }
            }

            document = new ListPacksDocument(ConnectorPack(global) is { } g ? g with { Scope = PolicyScopes.Global } : null, connectors, packs);
            if (document.Global is null)
            {
                error = "guardrail list-packs --json printed a global pack this app cannot read.";
                return false;
            }

            return true;
        }
    }

    private static ScopePack? ConnectorPack(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scope = Text(row, "connector");
        return scope.Length == 0 ? null : new ScopePack(scope, Text(row, "pack"), Text(row, "path"), Text(row, "source"));
    }

    // ---- guardrail protection list --json ----------------------------------------------------------------------------------

    /// <summary>Parses <c>guardrail protection list --json</c>: the opt-in packs and the scopes that have them on.</summary>
    public static bool TryParseProtection(string? text, out ProtectionDocument document, out string error)
    {
        document = new ProtectionDocument(Array.Empty<ProtectionPack>(), Array.Empty<ProtectionScope>());
        if (!TryRoot(text, "guardrail protection list --json", out var json, out error))
        {
            return false;
        }

        using (json)
        {
            var root = json.RootElement;
            if (!root.TryGetProperty("packs", out var packList) || packList.ValueKind != JsonValueKind.Array)
            {
                error = "guardrail protection list --json printed no packs list.";
                return false;
            }

            var packs = new List<ProtectionPack>();
            foreach (var item in packList.EnumerateArray())
            {
                if (ProtectionPackOf(item) is { } pack)
                {
                    packs.Add(pack);
                }
            }

            var scopes = new List<ProtectionScope>();
            if (root.TryGetProperty("scopes", out var scopeList) && scopeList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in scopeList.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && Text(item, "scope") is { Length: > 0 } scope)
                    {
                        scopes.Add(new ProtectionScope(scope, Text(item, "pack"), Text(item, "path"), Strings(item, "enabled")));
                    }
                }
            }

            document = new ProtectionDocument(packs, scopes);
            return true;
        }
    }

    /// <summary>One opt-in pack object (a <c>packs[]</c> row); null when it has no name.</summary>
    public static ProtectionPack? ProtectionPackOf(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        var rules = new List<ProtectionRule>();
        if (item.TryGetProperty("rules", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in list.EnumerateArray())
            {
                if (rule.ValueKind == JsonValueKind.Object && Text(rule, "id") is { Length: > 0 } id)
                {
                    rules.Add(new ProtectionRule(id, Text(rule, "severity"), Text(rule, "title")));
                }
            }
        }

        return new ProtectionPack(
            name,
            Text(item, "title"),
            Text(item, "summary"),
            Text(item, "covers"),
            Whole(item, "rule_count"),
            Strings(item, "rule_ids"),
            Text(item, "status"),
            rules);
    }

    // ---- config show --section guardrail --format json ---------------------------------------------------------------------

    /// <summary>
    /// Parses <c>config show --section guardrail --format json</c>: <c>{"guardrail": {...}}</c>, the section as the runtime runs it (the
    /// values config.yaml sets plus the defaults for the keys it leaves out). Reads only what decides a posture.
    /// </summary>
    public static bool TryParseGuardrailSettings(string? text, out GuardrailSettings settings, out string error)
    {
        settings = new GuardrailSettings("observe", false, "HIGH", string.Empty, string.Empty, new Dictionary<string, ConnectorGuardrailSettings>(StringComparer.Ordinal));
        if (!TryRoot(text, "config show --section guardrail", out var json, out error))
        {
            return false;
        }

        using (json)
        {
            var root = json.RootElement;
            if (!root.TryGetProperty("guardrail", out var section) || section.ValueKind != JsonValueKind.Object)
            {
                error = "config show --section guardrail printed no guardrail section.";
                return false;
            }

            var hilt = section.TryGetProperty("hilt", out var global) && global.ValueKind == JsonValueKind.Object ? global : default;
            var connectors = new Dictionary<string, ConnectorGuardrailSettings>(StringComparer.Ordinal);
            if (section.TryGetProperty("connectors", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in map.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    // A present approval block overrides the global one entirely (effective_hilt); null or absent inherits.
                    var own = entry.Value.TryGetProperty("hilt", out var block) && block.ValueKind == JsonValueKind.Object ? block : default;
                    var hasOwn = own.ValueKind == JsonValueKind.Object;
                    connectors[entry.Name] = new ConnectorGuardrailSettings(
                        Text(entry.Value, "mode").Trim(),
                        hasOwn ? Flag(own, "enabled") : null,
                        hasOwn ? Text(own, "min_severity") : null,
                        Text(entry.Value, "block_at"),
                        Text(entry.Value, "alert_at"));
                }
            }

            settings = new GuardrailSettings(
                Text(section, "mode").Trim(),
                hilt.ValueKind == JsonValueKind.Object && Flag(hilt, "enabled"),
                hilt.ValueKind == JsonValueKind.Object ? Text(hilt, "min_severity") : "HIGH",
                Text(section, "block_at"),
                Text(section, "alert_at"),
                connectors);
            return true;
        }
    }

    // ---- guardrail validate-pack PATH --json -------------------------------------------------------------------------------

    /// <summary>Decodes <c>validate-pack --json</c> (exit 0 valid, 1 invalid, 2 the validator is unavailable).</summary>
    public static PackValidation ParseValidation(int exitCode, string? stdout)
    {
        string reason = string.Empty;
        JsonDocument? json = null;
        try
        {
            var text = Trim(stdout);
            json = text.Length == 0 ? null : JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            json = null;
        }

        using (json)
        {
            var root = json?.RootElement ?? default;
            var isObject = root.ValueKind == JsonValueKind.Object;
            if (isObject && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                reason = Text(error, "reason").Trim();
                var where = Text(error, "path").Trim();
                if (where.Length > 0 && !string.Equals(where, "$", StringComparison.Ordinal))
                {
                    reason = reason.Length > 0 ? $"{reason} (at {where})" : where;
                }
            }

            if (exitCode == 2)
            {
                return new PackValidation("unavailable", reason);
            }

            if (exitCode == 0 && isObject && root.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.True)
            {
                var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
                return new PackValidation(
                    "valid",
                    string.Empty,
                    summary.ValueKind == JsonValueKind.Object ? Whole(summary, "rule_count") : 0,
                    summary.ValueKind == JsonValueKind.Object ? Whole(summary, "enabled_rule_count") : 0,
                    summary.ValueKind == JsonValueKind.Object ? Whole(summary, "rule_file_count") : 0,
                    summary.ValueKind == JsonValueKind.Object ? Text(summary, "digest") : string.Empty);
            }

            return new PackValidation("invalid", reason.Length > 0 ? reason : $"exit {exitCode.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    // ---- the runtime's data files ------------------------------------------------------------------------------------------

    /// <summary>Parses the chain catalog (<c>tool-chains.json</c>: <c>{"version":1,"chains":[...]}</c>); empty when it is not that.</summary>
    public static IReadOnlyList<ToolChain> ParseToolChains(string? text)
    {
        if (!TryRoot(text, "tool-chains.json", out var json, out _))
        {
            return Array.Empty<ToolChain>();
        }

        using (json)
        {
            var root = json.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("chains", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ToolChain>();
            }

            var chains = new List<ToolChain>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || Text(entry, "id").Trim() is not { Length: > 0 } id)
                {
                    continue;
                }

                var title = Text(entry, "title").Trim();
                chains.Add(new ToolChain(
                    id,
                    title.Length > 0 ? title : id,
                    Text(entry, "severity").Trim().ToUpperInvariant(),
                    Text(entry, "domain").Trim(),
                    entry.TryGetProperty("can_block", out var block) && block.ValueKind == JsonValueKind.True,
                    Whole(entry, "event_window"),
                    Whole(entry, "time_window_seconds"),
                    Strings(entry, "requires").Select(r => r.Trim()).Where(r => r.Length > 0).ToArray(),
                    Text(entry, "note").Trim()));
            }

            return chains;
        }
    }

    /// <summary>The pack a composed folder was built on, from its <c>defenseclaw-pack.json</c>; null when there is no usable manifest.</summary>
    public static string? ParsePackBase(string? manifest)
    {
        if (!TryRoot(manifest, "defenseclaw-pack.json", out var json, out _))
        {
            return null;
        }

        using (json)
        {
            var root = json.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || Text(root, "base").Trim().Length == 0 || !root.TryGetProperty("protection", out var names) || names.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var baseName = Text(root, "base_name");
            if (baseName.Length > 0)
            {
                return baseName;
            }

            var folder = Text(root, "base").Trim().TrimEnd('/', '\\');
            var cut = folder.LastIndexOfAny(new[] { '/', '\\' });
            return cut >= 0 ? folder[(cut + 1)..] : folder;
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // A banner line before the document (the CLI prints some on stdout) is skipped.
        var start = text.IndexOfAny(new[] { '{', '[' });
        return start < 0 ? string.Empty : text[start..];
    }

    private static bool TryRoot(string? text, string what, out JsonDocument document, out string error)
    {
        document = null!;
        error = string.Empty;
        var json = Trim(text);
        if (json.Length == 0)
        {
            error = $"{what} printed nothing this app can read.";
            return false;
        }

        if (json.Length > MaxDocumentChars)
        {
            error = $"{what} printed more than this app reads.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = $"{what} printed something that is not JSON.";
            return false;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            error = $"{what} printed JSON that is not an object.";
            return false;
        }

        return true;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool? NullableFlag(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int Whole(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0
            ? number
            : 0;

    private static string[] Strings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray();
    }
}
