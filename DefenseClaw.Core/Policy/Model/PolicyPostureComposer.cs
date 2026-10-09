namespace DefenseClaw.Core.Policy.Model;

/// <summary>
/// Builds the scope postures (the global default, then each active connector) from the three documents the pinned runtime prints for them:
/// <c>guardrail list-packs --json</c> (each scope's rule pack), <c>guardrail protection list --json</c> (the opt-in packs layered into it) and
/// <c>config show --section guardrail --format json</c> (the mode, approval and tool-call levels, global and per connector).
/// <para>
/// The runtime's own <c>policy_catalog.scope_postures</c> does this from its in-process configuration; no command prints its result, so the
/// resolution is repeated here with the same rules, in the same order: the connector's own mode, else the global one; the connector's approval
/// block (which replaces the global one when present), else the global one; each tool-call level from the connector, else the global value,
/// else the scope's rule-pack profile, the alert level clamped to the block level (<see cref="PolicyLevels.ResolveLevels"/>). The scopes
/// and their packs are the runtime's own answer, not derived.
/// </para>
/// </summary>
public static class PolicyPostureComposer
{
    /// <summary>
    /// The postures, global first. A scope the protection list does not mention has no opt-in packs; the difference is reported through
    /// <paramref name="problems"/> so the read is called incomplete rather than silently trusted.
    /// </summary>
    /// <param name="packs">What <c>guardrail list-packs --json</c> printed.</param>
    /// <param name="protection">What <c>guardrail protection list --json</c> printed.</param>
    /// <param name="settings">The guardrail section.</param>
    /// <param name="problems">What did not line up (empty when the documents agree on the scopes).</param>
    public static IReadOnlyList<ScopePosture> Compose(ListPacksDocument packs, ProtectionDocument protection, GuardrailSettings settings, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(settings);

        var issues = new List<string>();
        var rows = new List<ScopePosture>();
        var global = packs.Global ?? new ScopePack(PolicyScopes.Global, "default", string.Empty, "default");
        var globalOwn = (PolicyLevels.LevelValue(settings.BlockAt), PolicyLevels.LevelValue(settings.AlertAt));

        var levels = PolicyLevels.ResolveLevels(global.Folder, globalOwn);
        rows.Add(new ScopePosture(
            PolicyScopes.Global,
            PolicyLevels.ModeLabel(settings.Mode),
            "global",
            PolicyLevels.HiltLabel(settings.HiltEnabled, settings.HiltMinSeverity),
            global.Pack,
            global.Folder,
            global.Source,
            EnabledFor(protection, PolicyScopes.Global, issues),
            levels.BlockAt,
            levels.AlertAt,
            levels.Source,
            globalOwn.Item1,
            globalOwn.Item2));

        foreach (var row in packs.Connectors)
        {
            var block = settings.ConnectorBlock(row.Scope);
            var overrideMode = block?.Mode.Trim() ?? string.Empty;
            var mode = overrideMode.Length > 0 ? overrideMode : settings.Mode.Trim();

            // A connector's approval block, when it has one, replaces the global block as a whole.
            var hilt = block is { HasOwnHilt: true }
                ? PolicyLevels.HiltLabel(block.HiltEnabled == true, block.HiltMinSeverity)
                : PolicyLevels.HiltLabel(settings.HiltEnabled, settings.HiltMinSeverity);

            var own = (PolicyLevels.LevelValue(block?.BlockAt), PolicyLevels.LevelValue(block?.AlertAt));
            var resolved = PolicyLevels.ResolveLevels(row.Folder, globalOwn, own);
            rows.Add(new ScopePosture(
                row.Scope,
                PolicyLevels.ModeLabel(mode),
                overrideMode.Length > 0 ? "override" : "global",
                hilt,
                row.Pack,
                row.Folder,
                row.Source,
                EnabledFor(protection, row.Scope, issues),
                resolved.BlockAt,
                resolved.AlertAt,
                resolved.Source,
                own.Item1,
                own.Item2));
        }

        problems = issues;
        return rows;
    }

    private static IReadOnlyList<string> EnabledFor(ProtectionDocument protection, string scope, List<string> issues)
    {
        var want = GuardrailSettings.NormalizeConnector(scope);
        foreach (var row in protection.Scopes)
        {
            if (string.Equals(row.Scope, scope, StringComparison.Ordinal) || string.Equals(GuardrailSettings.NormalizeConnector(row.Scope), want, StringComparison.Ordinal))
            {
                return row.Enabled;
            }
        }

        issues.Add($"The opt-in pack list has no row for {scope}.");
        return Array.Empty<string>();
    }
}
