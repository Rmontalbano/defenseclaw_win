using System.Globalization;

namespace DefenseClaw.Core.Policy.Model;

/// <summary>The effect of a tool-call level change on one scope: its levels before and after.</summary>
public sealed record LevelEffect(string Scope, ScopeLevels Before, ScopeLevels After)
{
    /// <summary>It blocks or alerts on fewer severities (the alert level can follow the block level down).</summary>
    public bool Weaker => After.BlockRank > Before.BlockRank || After.AlertRank > Before.AlertRank;

    /// <summary>How a sentence names the scope.</summary>
    public string Subject => Scope.Length == 0 || string.Equals(Scope, PolicyScopes.Global, StringComparison.Ordinal) ? "the global default" : Scope;

    /// <summary><c>blocks HIGH+ instead of MEDIUM+</c>; empty when nothing got weaker.</summary>
    public string Loosening()
    {
        var parts = new List<string>();
        if (After.BlockRank > Before.BlockRank)
        {
            parts.Add($"blocks {After.BlockAt} instead of {Before.BlockAt}");
        }

        if (After.AlertRank > Before.AlertRank)
        {
            parts.Add($"alerts on {After.AlertAt} instead of {Before.AlertAt}");
        }

        return string.Join(" and ", parts);
    }
}

/// <summary>
/// What <c>guardrail block-at|alert-at</c> would do, from the posture rows: every scope it reaches, and the connectors a global change
/// cannot reach because they set their own.
/// </summary>
/// <param name="Kind"><c>block</c> or <c>alert</c>.</param>
/// <param name="Connector">The <c>--connector</c> value; empty for the global value, which every connector without its own follows.</param>
/// <param name="Value">The stored level after it (<c>CRITICAL</c> to <c>LOW</c>); empty for inherit.</param>
/// <param name="Effects">Every scope it reaches.</param>
/// <param name="KeepOwn">The connectors that keep their own value.</param>
public sealed record LevelChange(string Kind, string Connector, string Value, IReadOnlyList<LevelEffect> Effects, IReadOnlyList<string> KeepOwn)
{
    /// <summary>The effect on one scope; null when the change does not reach it.</summary>
    public LevelEffect? EffectFor(string scope)
    {
        var want = scope.Length == 0 ? PolicyScopes.Global : scope;
        return Effects.FirstOrDefault(e => string.Equals(e.Scope, want, StringComparison.Ordinal));
    }

    /// <summary>The scopes that end up catching fewer severities.</summary>
    public IReadOnlyList<LevelEffect> Weakened() => Effects.Where(e => e.Weaker).ToArray();

    /// <summary><c>blocks CRITICAL instead of HIGH+ for the global default, codex and hermes</c>; scopes that lose the same way share one phrase.</summary>
    public string LoosenedText()
    {
        var groups = new List<(string Phrase, List<string> Subjects)>();
        foreach (var effect in Weakened())
        {
            var phrase = effect.Loosening();
            var group = groups.FirstOrDefault(g => string.Equals(g.Phrase, phrase, StringComparison.Ordinal));
            if (group.Subjects is null)
            {
                group = (phrase, new List<string>());
                groups.Add(group);
            }

            group.Subjects.Add(effect.Subject);
        }

        return string.Join("; ", groups.Select(g => $"{g.Phrase} for {PolicyLevels.AndJoin(g.Subjects)}"));
    }
}

/// <summary>A navigation entry of the panel: one of the views.</summary>
/// <param name="View">The view's id.</param>
/// <param name="Title">The view's title.</param>
/// <param name="Badge">A count (<c>26</c>, <c>0/5</c>); empty when there is nothing to count.</param>
public sealed record PolicyNavEntry(string View, string Title, string Badge);

/// <summary>
/// The Policies model over one read of the runtime's catalog: posture, opt-in packs, chains, rule families, named policies and rule packs.
/// A port of the runtime's <c>PoliciesPanelModel</c> (<c>defenseclaw.tui.services.policy_state</c>, source commit 95159fd) without its cursor
/// and key handling, which belong to the panel: here a scope is passed in where the runtime keeps a selection. Immutable and free of I/O.
/// <para>
/// <b>Six views, not seven, on Windows.</b> The runtime's model has a seventh view, Sandbox packs, and drops it where it cannot run
/// sandboxes: <c>PoliciesPanelModel(sandbox_supported=openshell_sandboxes_supported())</c> makes <c>views()</c> the first six of
/// <c>POLICY_VIEWS</c> unless the host is Linux or macOS (<c>platform_support.py</c>; the CLI's <c>sandbox</c> group exits 3 on Windows).
/// That is the one platform rule in the runtime's Policies code, and Windows is on the dropping side of it, so this model has the six and
/// no code for the seventh: it never reads <c>sandbox pack list</c>, and sandbox data in a model document is ignored.
/// </para>
/// <para>
/// <b>Tool-call levels and LLM thresholds are different things</b> and the model keeps them apart. A scope's block and alert level
/// (<c>guardrail.block_at</c> / <c>alert_at</c>, else its rule pack's profile) decide what happens to a <i>tool call</i>; a named policy's
/// block and alert threshold decide what happens to <i>LLM traffic</i> through the guardrail proxy. The Posture view shows and changes the
/// first, the Policies view the second, and the columns say which (<c>Blocks at</c> against <c>LLM block</c>).
/// </para>
/// </summary>
public sealed partial class PolicyModel
{
    /// <summary>
    /// The views, in navigation order: the runtime's <c>POLICY_VIEWS</c> without <c>sandbox_packs</c>, which its own model leaves out on a
    /// platform that cannot run sandboxes (see the type's remarks).
    /// </summary>
    public static readonly IReadOnlyList<string> ViewIds = new[] { "posture", "optin", "chains", "families", "policies", "packs" };

    private static readonly Dictionary<string, string> ViewTitles = new(StringComparer.Ordinal)
    {
        ["posture"] = "Posture",
        ["optin"] = "Opt-in packs",
        ["chains"] = "Chains",
        ["families"] = "Rule families",
        ["policies"] = "Policies",
        ["packs"] = "Rule packs",
    };

    /// <summary>Chain domains in display order (the catalog's <c>domain</c>).</summary>
    private static readonly (string Key, string Label)[] ChainDomains =
    {
        ("sql", "SQL"),
        ("kubernetes", "Kubernetes"),
        ("cloud", "Cloud"),
        ("host", "Host"),
        ("credentials", "Credentials"),
        ("data-egress", "Data egress"),
        ("network", "Network"),
        ("security-controls", "Security controls"),
    };

    /// <summary>The opt-in packs whose context a person asserts by turning them on.</summary>
    private static readonly Dictionary<string, string> ProtectedContext = new(StringComparer.Ordinal)
    {
        ["database-destruction-protection"] = "a protected database",
        ["kubernetes-production-protection"] = "a protected production Kubernetes cluster",
        ["cloud-production-protection"] = "protected production cloud accounts",
        ["infrastructure-destruction-protection"] = "protected production hosts and infrastructure",
        ["privacy-high-assurance"] = "high-assurance personal data",
    };

    public PolicyModel(PolicyCatalog catalog)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Protection = catalog.Protection.Where(p => !p.IsStaged).Concat(catalog.Protection.Where(p => p.IsStaged)).ToArray();
    }

    public PolicyCatalog Catalog { get; }

    /// <summary>The opt-in packs, selectable first and staged last.</summary>
    public IReadOnlyList<ProtectionPack> Protection { get; }

    public static string TitleOf(string view) => ViewTitles.GetValueOrDefault(view, view);

    // ---- named policies and rule packs -------------------------------------------------------------------------------------

    public NamedPolicy? ActivePolicy => Catalog.Policies.FirstOrDefault(p => p.IsActive);

    public NamedPolicy? PolicyNamed(string name) => Catalog.Policies.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>The global row first, then one per active connector.</summary>
    public IReadOnlyList<ScopePack> PackRows =>
        (Catalog.GlobalPack is { } global ? new[] { global } : Array.Empty<ScopePack>()).Concat(Catalog.ConnectorPacks).ToArray();

    /// <summary>Connectors whose own pack a global switch would clear.</summary>
    public IReadOnlyList<string> OverrideConnectors =>
        Catalog.ConnectorPacks.Where(r => string.Equals(r.Source, "override", StringComparison.Ordinal)).Select(r => r.Scope).ToArray();

    /// <summary>The pack names a switch at this scope replaces.</summary>
    public IReadOnlyList<string> PacksForScope(string connector)
    {
        if (connector.Length > 0)
        {
            var row = Catalog.ConnectorPacks.FirstOrDefault(r => string.Equals(r.Scope, connector, StringComparison.Ordinal));
            return row is null ? Array.Empty<string>() : new[] { row.Pack };
        }

        var names = new List<string>();
        if (Catalog.GlobalPack is { } global)
        {
            names.Add(global.Pack);
        }

        names.AddRange(Catalog.ConnectorPacks.Where(r => string.Equals(r.Source, "override", StringComparison.Ordinal)).Select(r => r.Pack));
        return names;
    }

    public string CurrentPack(string connector)
    {
        if (connector.Length > 0)
        {
            return Catalog.ConnectorPacks.FirstOrDefault(r => string.Equals(r.Scope, connector, StringComparison.Ordinal))?.Pack ?? string.Empty;
        }

        return Catalog.GlobalPack?.Pack ?? string.Empty;
    }

    // ---- scopes ------------------------------------------------------------------------------------------------------------

    public IReadOnlyList<ScopePosture> Postures => Catalog.Postures;

    /// <summary>The names of the scopes: <c>global</c>, then each active connector.</summary>
    public IReadOnlyList<string> Scopes =>
        Postures.Count == 0 ? new[] { PolicyScopes.Global } : Postures.Select(p => p.Scope).ToArray();

    /// <summary>The posture row of a scope (<c>""</c> or <c>global</c> is the global row); null when there is none.</summary>
    public ScopePosture? ScopeRow(string? scope)
    {
        var want = string.IsNullOrEmpty(scope) ? PolicyScopes.Global : scope;
        return Postures.FirstOrDefault(r => string.Equals(r.Scope, want, StringComparison.Ordinal));
    }

    /// <summary>The scope's row, or the global one when the name is unknown.</summary>
    public ScopePosture? ScopeRowOrGlobal(string? scope) => ScopeRow(scope) ?? ScopeRow(PolicyScopes.Global) ?? Postures.FirstOrDefault();

    /// <summary>The <c>--connector</c> value for a scope row: empty for the global one.</summary>
    public static string ConnectorOf(ScopePosture? row)
    {
        var scope = row?.Scope ?? string.Empty;
        return scope.Length == 0 || string.Equals(scope, PolicyScopes.Global, StringComparison.Ordinal) ? string.Empty : scope;
    }

    /// <summary><c>--connector</c> for mode and approval: empty on a single-connector install, whose changes go to the global value.</summary>
    public string CommandConnector(ScopePosture? row)
    {
        var connector = ConnectorOf(row);
        return connector.Length > 0 && Catalog.MultiConnector ? connector : string.Empty;
    }

    /// <summary>Connectors whose <c>mode</c> or <c>pack</c> is their own, not the global one.</summary>
    public IReadOnlyList<string> OwnSetting(string field)
    {
        var mode = string.Equals(field, "mode", StringComparison.Ordinal);
        return Postures
            .Where(r => ConnectorOf(r).Length > 0 && string.Equals(mode ? r.ModeSource : r.PackSource, "override", StringComparison.Ordinal))
            .Select(r => r.Scope)
            .ToArray();
    }

    /// <summary>What a scope sets itself ("" = it inherits); on the global row, the global values.</summary>
    public static (string Block, string Alert) OwnLevels(ScopePosture? row) =>
        row is null ? (string.Empty, string.Empty) : (PolicyLevels.LevelValue(row.OwnBlockAt), PolicyLevels.LevelValue(row.OwnAlertAt));

    /// <summary>How the gateway resolves <paramref name="row"/>'s levels, optionally with changed values.</summary>
    /// <param name="row">The scope.</param>
    /// <param name="globalOwn">Replaces the global row's values.</param>
    /// <param name="own">Replaces the row's own values (ignored for the global row, whose own values are the global ones).</param>
    public ScopeLevels RowLevels(ScopePosture row, (string Block, string Alert)? globalOwn = null, (string Block, string Alert)? own = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        var shared = globalOwn ?? OwnLevels(ScopeRow(PolicyScopes.Global));
        if (ConnectorOf(row).Length == 0)
        {
            return PolicyLevels.ResolveLevels(row.PackFolder, shared);
        }

        return PolicyLevels.ResolveLevels(row.PackFolder, shared, own ?? OwnLevels(row));
    }

    /// <summary>The (blocks at, alerts at) labels for the scope's tool calls: the catalog's, else resolved the same way.</summary>
    public (string Block, string Alert) ScopeLevelLabels(ScopePosture? row)
    {
        if (row is null)
        {
            return PolicyLevels.ProfileLevelsFor(string.Empty);
        }

        if (PolicyLevels.LevelRank(row.BlockAt) is not null && PolicyLevels.LevelRank(row.AlertAt) is not null)
        {
            return (row.BlockAt, row.AlertAt);
        }

        var levels = RowLevels(row);
        return (levels.BlockAt, levels.AlertAt);
    }

    /// <summary>The scope's tool-call levels on <paramref name="packFolder"/>, and that pack's own levels. A set level survives a pack switch.</summary>
    public (ScopeLevels Scope, ScopeLevels PackOnly) LevelsWithPack(string connector, string packFolder)
    {
        var shared = OwnLevels(ScopeRow(PolicyScopes.Global));
        var row = connector.Length > 0 ? ScopeRow(connector) : null;
        (string Block, string Alert)? own = row is null ? null : OwnLevels(row);
        return (PolicyLevels.ResolveLevels(packFolder, shared, own), PolicyLevels.ResolveLevels(packFolder));
    }

    /// <summary>What picking <paramref name="choice"/> (a picker value, or <see cref="PolicyLevels.Inherit"/>) on <paramref name="row"/> changes.</summary>
    public LevelChange LevelChangeFor(string kind, ScopePosture row, string choice)
    {
        ArgumentNullException.ThrowIfNull(row);
        var block = string.Equals(kind, "block", StringComparison.Ordinal);
        var value = string.Equals(choice, PolicyLevels.Inherit, StringComparison.Ordinal) ? string.Empty : (choice ?? string.Empty).Trim().TrimEnd('+').ToUpperInvariant();

        var target = CommandConnector(row);
        if (target.Length > 0)
        {
            var own = OwnLevels(row);
            own = block ? (value, own.Alert) : (own.Block, value);
            var effect = new LevelEffect(row.Scope, RowLevels(row), RowLevels(row, own: own));
            return new LevelChange(kind, target, value, new[] { effect }, Array.Empty<string>());
        }

        var shared = OwnLevels(ScopeRow(PolicyScopes.Global));
        shared = block ? (value, shared.Alert) : (shared.Block, value);
        var effects = new List<LevelEffect>();
        var keepOwn = new List<string>();
        foreach (var scopeRow in Postures)
        {
            var ownValue = block ? OwnLevels(scopeRow).Block : OwnLevels(scopeRow).Alert;
            if (ConnectorOf(scopeRow).Length > 0 && ownValue.Length > 0)
            {
                keepOwn.Add(scopeRow.Scope);
                continue;
            }

            effects.Add(new LevelEffect(scopeRow.Scope, RowLevels(scopeRow), RowLevels(scopeRow, globalOwn: shared)));
        }

        return new LevelChange(kind, string.Empty, value, effects, keepOwn);
    }

    /// <summary>The picker value the change's scope stores now (<see cref="PolicyLevels.Inherit"/> if none).</summary>
    public string LevelCurrent(string kind, ScopePosture row)
    {
        var holder = CommandConnector(row).Length > 0 ? row : ScopeRow(PolicyScopes.Global);
        var own = OwnLevels(holder);
        return PolicyLevels.PickerLevel(string.Equals(kind, "block", StringComparison.Ordinal) ? own.Block : own.Alert);
    }

    /// <summary><c>Use the pack's level (CRITICAL)</c> or <c>Use the global level (HIGH+)</c>.</summary>
    public string LevelInheritText(string kind, ScopePosture row)
    {
        var change = LevelChangeFor(kind, row, PolicyLevels.Inherit);
        var effect = change.EffectFor(row.Scope);
        if (effect is null)
        {
            return "Use the pack's level";
        }

        var block = string.Equals(kind, "block", StringComparison.Ordinal);
        var label = block ? effect.After.BlockAt : effect.After.AlertAt;
        var source = block ? effect.After.BlockSource : effect.After.AlertSource;
        var where = string.Equals(source, "global", StringComparison.Ordinal) ? "the global level" : "the pack's level";
        return $"Use {where} ({label})";
    }

    /// <summary>Where a scope's tool-call levels come from, for its detail.</summary>
    public string LevelsLine(ScopePosture row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var levels = RowLevels(row);
        var scope = row.Scope.Length > 0 ? row.Scope : PolicyScopes.Global;
        var pack = row.Pack;
        var profile = PolicyLevels.PackProfile(row.PackFolder);
        string line;
        if (string.Equals(levels.BlockSource, levels.AlertSource, StringComparison.Ordinal))
        {
            line = $"Levels {PolicyLevels.LevelOrigin(levels.BlockSource, scope, pack, profile)}.";
        }
        else
        {
            line = $"Block level {PolicyLevels.LevelOrigin(levels.BlockSource, scope, pack, profile)}; alert level {PolicyLevels.LevelOrigin(levels.AlertSource, scope, pack, profile)}.";
        }

        if (levels.AlertClamped)
        {
            line += $" Alerts start at {levels.AlertAt}: anything that blocks also alerts.";
        }

        return line;
    }

    /// <summary><c>strict</c>, or <c>strict+1</c> for a pack composed from strict and one opt-in pack.</summary>
    public string ScopePackLabel(ScopePosture row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var pack = PolicyLevels.Or(row.Pack, "-");
        if (Catalog.PackBases.TryGetValue(row.PackFolder, out var baseName) && baseName.Length > 0)
        {
            return $"{baseName}+{ScopeProtection(row).Count.ToString(CultureInfo.InvariantCulture)}";
        }

        return pack;
    }

    /// <summary>The opt-in packs layered into the scope's rule pack.</summary>
    public static IReadOnlyList<string> ScopeProtection(ScopePosture? row) => row?.Protection ?? Array.Empty<string>();

    // ---- opt-in packs, rule families, chains --------------------------------------------------------------------------------

    public int ProtectionTotal => Protection.Count(p => !p.IsStaged);

    /// <summary>Opt-in packs turned on for any scope, in pack order.</summary>
    public IReadOnlyList<string> ProtectionInUse
    {
        get
        {
            var used = Postures.SelectMany(ScopeProtection).ToHashSet(StringComparer.Ordinal);
            return Protection.Where(p => used.Contains(p.Name)).Select(p => p.Name).ToArray();
        }
    }

    public ProtectionPack? ProtectionPackNamed(string name) => Protection.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public IReadOnlyList<RuleFamily> ScopeFamilies(ScopePosture? row) =>
        row is not null && Catalog.Families.TryGetValue(row.PackFolder, out var families) ? families : Array.Empty<RuleFamily>();

    /// <summary>The chains in display order: grouped by domain (<see cref="ChainDomains"/>), the catalog's order inside a domain, unknown domains last as "Other".</summary>
    public IReadOnlyList<(string Label, ToolChain Chain)> ChainRows()
    {
        var order = ChainDomains.Select((d, i) => (d.Key, i)).ToDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);
        var grouped = new Dictionary<string, List<ToolChain>>(StringComparer.Ordinal);
        foreach (var chain in Catalog.Chains)
        {
            var domain = chain.Domain.Trim().ToLowerInvariant();
            var key = order.ContainsKey(domain) ? domain : string.Empty;
            if (!grouped.TryGetValue(key, out var list))
            {
                grouped[key] = list = new List<ToolChain>();
            }

            list.Add(chain);
        }

        var rows = new List<(string, ToolChain)>();
        foreach (var key in grouped.Keys.OrderBy(k => order.GetValueOrDefault(k, order.Count)))
        {
            var label = ChainDomainLabel(key);
            rows.AddRange(grouped[key].Select(chain => (label, chain)));
        }

        return rows;
    }

    public int BlockingChains => Catalog.Chains.Count(c => c.CanBlock);

    public static string ChainDomainLabel(string domain) =>
        ChainDomains.FirstOrDefault(d => string.Equals(d.Key, domain, StringComparison.Ordinal)).Label ?? "Other";

    // ---- text the panel shows ----------------------------------------------------------------------------------------------

    /// <summary>The sentence a scope's user agrees to by turning pack <paramref name="name"/> on.</summary>
    public static string ProtectionClaim(string name, string scope)
    {
        var context = ProtectedContext.GetValueOrDefault(name, "a protected environment");
        return scope.Length == 0 || string.Equals(scope, PolicyScopes.Global, StringComparison.Ordinal)
            ? $"Turning it on tells DefenseClaw that every connector using the global pack works with {context}."
            : $"Turning it on tells DefenseClaw that {scope} works with {context}.";
    }

    /// <summary><c>Database destruction protection</c> as <c>Database destruction</c>, for a table cell.</summary>
    public static string PackShortTitle(string title)
    {
        const string suffix = " protection";
        var shortTitle = title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? title[..^suffix.Length] : title;
        return shortTitle.Length > 0 ? shortTitle : title;
    }

    /// <summary><c>● default policy · default pack · 1 of 5 opt-in packs · 26 chains (4 can block)</c>.</summary>
    public string Header(int width = 0)
    {
        var policy = ActivePolicy?.Name ?? "no";
        var pack = Catalog.GlobalPack?.Pack ?? "?";
        var used = ProtectionInUse.Count;
        var total = ProtectionTotal;
        var chains = Catalog.Chains.Count;
        var blocking = BlockingChains;
        var wide = new List<string> { $"● {policy} policy", $"{pack} pack" };
        var shortened = new List<string> { $"● {policy} policy", $"{pack} pack" };
        if (total > 0)
        {
            wide.Add($"{I(used)} of {I(total)} opt-in packs");
            shortened.Add($"{I(used)}/{I(total)} opt-in");
        }

        if (chains > 0)
        {
            wide.Add($"{I(chains)} chains ({I(blocking)} can block)");
            shortened.Add($"{I(chains)} chains");
        }

        var text = string.Join(" · ", wide);
        if (width > 0 && text.Length > width)
        {
            text = string.Join(" · ", shortened);
        }

        return PolicyLevels.Fit(text, width);
    }

    /// <summary>The view's status line: what the view is about; the errors are <see cref="ViewError"/>.</summary>
    public string Headline(string view, ScopePosture? scope = null, int width = 0)
    {
        switch (view)
        {
            case "posture":
                if (Catalog.PostureError.Length > 0)
                {
                    return $"Could not read the protection settings: {Catalog.PostureError}";
                }

                if (ActivePolicy is not { } active)
                {
                    return "Tool-call levels are set per scope below · no policy is active for LLM traffic";
                }

                var text = $"LLM traffic ({active.Name} policy): blocks {PolicyLevels.Or(active.BlockAt, "?")}, alerts {PolicyLevels.Or(active.AlertAt, "?")}";
                var wide = $"{text} · tool calls: per scope below";
                return width == 0 || wide.Length <= width ? wide : text;

            case "optin":
            case "families":
                if (Catalog.PostureError.Length > 0)
                {
                    return $"Could not read the protection settings: {Catalog.PostureError}";
                }

                var name = scope?.Scope is { Length: > 0 } s ? s : "-";
                if (string.Equals(view, "optin", StringComparison.Ordinal))
                {
                    return $"Scope: {name} ▾  ·  {I(ScopeProtection(scope).Count)} of {I(ProtectionTotal)} on";
                }

                return $"Scope: {name} ▾  ·  {(scope is null ? "-" : scope.Pack)} pack";

            case "chains":
                if (Catalog.PostureError.Length > 0)
                {
                    return $"Could not read the protection settings: {Catalog.PostureError}";
                }

                var blocking = BlockingChains;
                return $"{I(Catalog.Chains.Count)} bounded chains · ✓ {I(blocking)} can block · ◐ {I(Catalog.Chains.Count - blocking)} alert only · built in, read-only";

            case "packs":
                if (Catalog.PackError.Length > 0)
                {
                    return $"Could not read rule packs: {Catalog.PackError}";
                }

                return PoliciesHeadline();

            case "policies":
                if (Catalog.PoliciesError.Length > 0)
                {
                    return $"Could not read policies: {Catalog.PoliciesError}";
                }

                return PoliciesHeadline();

            default:
                // Not a view of this model (the runtime's Sandbox packs, which Windows leaves out).
                return string.Empty;
        }
    }

    private string PoliciesHeadline()
    {
        var policy = ActivePolicy is { } active ? $"active policy {active.Name}" : "no policy activated yet";
        var pack = Catalog.GlobalPack?.Pack ?? "?";
        var overrides = OverrideConnectors.Count;
        var own = overrides > 0 ? $" · {I(overrides)} connector{(overrides != 1 ? "s" : string.Empty)} with their own" : string.Empty;
        return $"{policy} · rule pack {pack}{own}";
    }

    /// <summary>Why the view could not be read; empty when it could.</summary>
    public string ViewError(string view) => view switch
    {
        "policies" => Catalog.PoliciesError,
        "packs" => Catalog.PackError,
        "posture" or "optin" or "chains" or "families" => Catalog.PostureError,
        _ => string.Empty,
    };

    /// <summary>What an empty view says (empty while it has rows, or when an error explains the emptiness).</summary>
    public string EmptyState(string view, ScopePosture? scope = null)
    {
        if (RowCount(view, scope) > 0)
        {
            return string.Empty;
        }

        if (string.Equals(view, "packs", StringComparison.Ordinal))
        {
            return Catalog.PackError.Length > 0 ? string.Empty : "No rule packs found.";
        }

        if (ViewError(view).Length > 0)
        {
            return string.Empty;
        }

        return view switch
        {
            "posture" => "No scopes found. Set up the guardrail first: defenseclaw setup guardrail",
            "optin" => "No opt-in protection packs were found in this install.",
            "families" => "The scope's rule pack has no rule files.",
            "chains" => "The chain catalog was not found in this install.",
            "policies" => "No named policies found. Create one with: defenseclaw policy create NAME",
            _ => string.Empty,
        };
    }

    /// <summary>How many rows a view has (for the scope given to the scope-specific ones).</summary>
    public int RowCount(string view, ScopePosture? scope = null) => view switch
    {
        "posture" => Postures.Count,
        "optin" => Protection.Count,
        "chains" => Catalog.Chains.Count,
        "families" => ScopeFamilies(scope).Count,
        "packs" => PackRows.Count,
        "policies" => Catalog.Policies.Count,
        _ => 0,
    };

    /// <summary><c>(view, title, badge)</c> for the navigation list.</summary>
    public IReadOnlyList<PolicyNavEntry> Nav(ScopePosture? scope = null)
    {
        var families = ScopeFamilies(scope).Count;
        var badges = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["posture"] = Postures.Count > 0 ? I(Postures.Count) : string.Empty,
            ["optin"] = ProtectionTotal > 0 ? $"{I(ProtectionInUse.Count)}/{I(ProtectionTotal)}" : string.Empty,
            ["chains"] = Catalog.Chains.Count > 0 ? I(Catalog.Chains.Count) : string.Empty,
            ["families"] = families > 0 ? I(families) : string.Empty,
            ["policies"] = Catalog.Policies.Count > 0 ? I(Catalog.Policies.Count) : string.Empty,
            ["packs"] = PackRows.Count > 0 ? I(PackRows.Count) : string.Empty,
        };

        return ViewIds.Select(v => new PolicyNavEntry(v, TitleOf(v), badges[v])).ToArray();
    }

    internal static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
