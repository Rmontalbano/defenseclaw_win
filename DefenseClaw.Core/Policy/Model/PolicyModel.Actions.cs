namespace DefenseClaw.Core.Policy.Model;

/// <summary>What a Policies action does.</summary>
public enum PolicyActionKind
{
    /// <summary><c>guardrail mode observe|action</c>.</summary>
    Mode,

    /// <summary><c>guardrail block-at LEVEL|inherit</c>: a scope's tool-call block level.</summary>
    ToolBlockLevel,

    /// <summary><c>guardrail alert-at LEVEL|inherit</c>: a scope's tool-call alert level.</summary>
    ToolAlertLevel,

    /// <summary><c>guardrail hilt on|off</c>: human approval.</summary>
    Approval,

    /// <summary><c>guardrail use-pack PACK</c>: switch a scope's rule pack. Only after the pack validates.</summary>
    UsePack,

    /// <summary><c>guardrail validate-pack PATH --json</c>: a read, never a change.</summary>
    ValidatePack,

    /// <summary><c>guardrail protection enable|disable PACK</c>.</summary>
    Protection,

    /// <summary><c>policy activate NAME</c>.</summary>
    Activate,

    /// <summary><c>policy edit guardrail --block-threshold N</c>: a named policy's block threshold for LLM traffic.</summary>
    LlmBlockLevel,

    /// <summary><c>policy edit guardrail --alert-threshold N</c>: a named policy's alert threshold for LLM traffic.</summary>
    LlmAlertLevel,
}

/// <summary>
/// What a change says before it runs: the question, what it does, the consequences, and (only when it protects less) the warning. Built from
/// the runtime's own model - the same facts its consequence dialogs state - so the panel does not word a consequence of its own.
/// </summary>
/// <param name="Heading">The question the review asks (<c>Set codex's block level to HIGH+?</c>).</param>
/// <param name="Summary">One line: <c>block level: MEDIUM+ → HIGH+</c>.</param>
/// <param name="Details">What it does and what it leaves alone, one sentence each.</param>
/// <param name="Warning">Why it protects less (<c>This weakens protection: ...</c>); empty when it does not.</param>
/// <param name="ConfirmLabel">The confirm button's text.</param>
public sealed record PolicyConsequence(string Heading, string Summary, IReadOnlyList<string> Details, string Warning, string ConfirmLabel)
{
    /// <summary>The consequence with one more detail line (the validation of a rule pack).</summary>
    public PolicyConsequence With(string detail) =>
        this with { Details = Details.Concat(new[] { detail }).ToArray() };
}

/// <summary>One thing the panel can do to the selected row.</summary>
/// <param name="Id">A stable key within the row: the group and the argv.</param>
/// <param name="Kind">What it does.</param>
/// <param name="Group">The group of related choices it belongs to (<c>Tool-call block level</c>).</param>
/// <param name="Title">The choice (<c>HIGH+</c>, <c>Switch to action</c>).</param>
/// <param name="Argv">The arguments after <c>defenseclaw</c>.</param>
/// <param name="IsCurrent">True when this choice is already in force (the button shows as the current one).</param>
/// <param name="Weakens">True when it protects less: the review then asks for an acknowledgement.</param>
/// <param name="Consequence">What it says before it runs.</param>
/// <param name="PackFolder">For <see cref="PolicyActionKind.UsePack"/> and <see cref="PolicyActionKind.ValidatePack"/>, the pack's folder, which is validated before a switch.</param>
public sealed record PolicyAction(
    string Id,
    PolicyActionKind Kind,
    string Group,
    string Title,
    IReadOnlyList<string> Argv,
    bool IsCurrent,
    bool Weakens,
    PolicyConsequence Consequence,
    string? PackFolder = null)
{
    /// <summary>True for a read (the validation of a pack): it changes nothing.</summary>
    public bool IsRead => Kind == PolicyActionKind.ValidatePack;
}

public sealed partial class PolicyModel
{
    /// <summary>The actions the row offers, grouped; empty for a read-only view and for a row nothing can be done to.</summary>
    /// <param name="view">One of <see cref="ViewIds"/>.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="scope">The scope the opt-in packs are turned on for.</param>
    public IReadOnlyList<PolicyAction> Actions(string view, string key, ScopePosture? scope = null) => view switch
    {
        "posture" => ScopeRow(key) is { } row ? PostureActions(row) : Array.Empty<PolicyAction>(),
        "optin" => ProtectionPackNamed(key) is { } pack && scope is not null ? ProtectionActions(scope, pack) : Array.Empty<PolicyAction>(),
        "policies" => PolicyNamed(key) is { } policy ? PolicyActionsOf(policy) : Array.Empty<PolicyAction>(),
        "packs" => PackRows.FirstOrDefault(r => string.Equals(r.Scope, key, StringComparison.Ordinal)) is { } packRow
            ? RulePackActions(packRow.IsGlobal ? string.Empty : packRow.Scope)
            : Array.Empty<PolicyAction>(),
        _ => Array.Empty<PolicyAction>(),
    };

    private static PolicyAction Make(PolicyActionKind kind, string group, string title, PolicyIntent intent, bool current, bool weakens, PolicyConsequence consequence, string? folder = null) =>
        new($"{group}|{string.Join(' ', intent.Args)}", kind, group, title, intent.Args, current, weakens, consequence, folder);

    // ---- posture: mode, tool-call levels, approval, rule pack --------------------------------------------------------------

    private IReadOnlyList<PolicyAction> PostureActions(ScopePosture row)
    {
        var actions = new List<PolicyAction>();
        var command = CommandConnector(row);

        foreach (var mode in new[] { "observe", "action" })
        {
            var weakens = PolicyLevels.ModeWeakens(row.Mode, mode);
            actions.Add(Make(
                PolicyActionKind.Mode,
                "Mode",
                mode == "action" ? "Enforce (action)" : "Log only (observe)",
                PolicyIntents.Mode(mode, command),
                string.Equals(row.Mode, mode, StringComparison.Ordinal),
                weakens,
                ModeConsequence(row, mode, weakens)));
        }

        foreach (var (kind, levels, group, actionKind) in new[]
                 {
                     ("block", PolicyLevels.ToolBlockLevels, "Tool-call block level", PolicyActionKind.ToolBlockLevel),
                     ("alert", PolicyLevels.ToolAlertLevels, "Tool-call alert level", PolicyActionKind.ToolAlertLevel),
                 })
        {
            var current = LevelCurrent(kind, row);
            foreach (var level in levels.Concat(new[] { PolicyLevels.Inherit }))
            {
                var change = LevelChangeFor(kind, row, level);
                var weakens = change.Weakened().Count > 0;
                actions.Add(Make(
                    actionKind,
                    group,
                    level == PolicyLevels.Inherit ? LevelInheritText(kind, row) : level,
                    PolicyIntents.Level(kind, level, change.Connector),
                    string.Equals(current, level, StringComparison.Ordinal),
                    weakens,
                    LevelConsequence(row, kind, level, change)));
            }
        }

        foreach (var level in PolicyLevels.HiltLevels)
        {
            var weakens = PolicyLevels.HiltWeakens(row.Hilt, level);
            actions.Add(Make(
                PolicyActionKind.Approval,
                "Human approval",
                level == "off" ? "Off" : level,
                PolicyIntents.Hilt(level, command),
                string.Equals(row.Hilt, level, StringComparison.Ordinal),
                weakens,
                HiltConsequence(row, level, weakens)));
        }

        actions.AddRange(RulePackActions(ConnectorOf(row)));
        return actions;
    }

    // ---- rule packs --------------------------------------------------------------------------------------------------------

    private IReadOnlyList<PolicyAction> RulePackActions(string connector)
    {
        var actions = new List<PolicyAction>();
        var current = CurrentPack(connector);
        foreach (var pack in Catalog.Packs)
        {
            var value = pack.IsPreset ? pack.Name : pack.Folder;
            if (!PolicyIntents.IsSafePackTarget(value))
            {
                continue;
            }

            var weakens = PolicyLevels.PackWeakens(PacksForScope(connector), pack.Name);
            actions.Add(Make(
                PolicyActionKind.UsePack,
                "Rule pack",
                pack.Name,
                PolicyIntents.UsePack(value, connector),
                string.Equals(current, pack.Name, StringComparison.Ordinal),
                weakens,
                PackConsequence(connector, pack, weakens),
                pack.Folder));
        }

        foreach (var pack in Catalog.Packs)
        {
            if (!Path.IsPathFullyQualified(pack.Folder) || !PolicyIntents.IsSafePackTarget(pack.Folder))
            {
                continue;
            }

            actions.Add(Make(
                PolicyActionKind.ValidatePack,
                "Validate a rule pack",
                pack.Name,
                new PolicyIntent($"guardrail validate-pack {pack.Folder}", PolicyIntents.ValidatePack(pack.Folder), $"Validate {pack.Name} without changing anything."),
                false,
                false,
                new PolicyConsequence($"Validate the {pack.Name} rule pack?", "Checks the pack with the gateway's own validator. Nothing changes.", Array.Empty<string>(), string.Empty, "Validate"),
                pack.Folder));
        }

        return actions;
    }

    private PolicyConsequence PackConsequence(string connector, RulePackEntry pack, bool weakens)
    {
        var replaced = PacksForScope(connector);
        var details = new List<string>();
        if (connector.Length > 0)
        {
            details.Add($"{connector}: {PolicyLevels.Or(CurrentPack(connector), "-")} → {pack.Name}");
            details.Add("Other connectors keep their pack.");
        }
        else
        {
            details.Add($"Global: {PolicyLevels.Or(CurrentPack(string.Empty), "-")} → {pack.Name}");
            var cleared = OverrideConnectors;
            details.Add(cleared.Count > 0 ? "Clears the own pack of: " + string.Join(", ", cleared) : "Every connector uses the global pack.");
        }

        // guardrail.block_at / alert_at win over the pack's own levels.
        var (levels, packLevels) = LevelsWithPack(connector, pack.Folder);
        var held = new List<string>();
        if (!string.Equals(levels.BlockSource, "pack", StringComparison.Ordinal) && !string.Equals(levels.BlockAt, packLevels.BlockAt, StringComparison.Ordinal))
        {
            held.Add($"block at {levels.BlockAt} (not the pack's {packLevels.BlockAt})");
        }

        if (!string.Equals(levels.AlertSource, "pack", StringComparison.Ordinal) && !string.Equals(levels.AlertAt, packLevels.AlertAt, StringComparison.Ordinal))
        {
            held.Add($"alert at {levels.AlertAt} (not the pack's {packLevels.AlertAt})");
        }

        if (held.Count > 0)
        {
            details.Add("Tool calls still " + string.Join(" and ", held) + ", as set with block-at / alert-at.");
        }

        var warning = weakens ? $"{pack.Name} is a looser preset than {string.Join(", ", replaced.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}." : string.Empty;
        return new PolicyConsequence(
            $"Use the {pack.Name} rule pack for {(connector.Length > 0 ? connector : "every connector")}?",
            "A running gateway restarts to load the new pack.",
            details,
            warning,
            $"Use {pack.Name}");
    }

    // ---- posture: mode -----------------------------------------------------------------------------------------------------

    private PolicyConsequence ModeConsequence(ScopePosture row, string mode, bool weakens)
    {
        var old = PolicyLevels.Or(row.Mode, "observe");
        var scope = row.Scope.Length > 0 ? row.Scope : PolicyScopes.Global;
        var (block, alert) = ScopeLevelLabels(row);
        var hilt = PolicyLevels.Or(row.Hilt, "off");
        var connector = ConnectorOf(row);
        var details = new List<string> { PolicyLevels.PostureSummary(scope, mode, block, alert, hilt) };
        if (connector.Length > 0 && !Catalog.MultiConnector)
        {
            details.Add("This install has one connector, so this sets the global mode.");
        }
        else if (connector.Length > 0)
        {
            details.Add("Only this connector changes; the others keep their mode. A running gateway restarts.");
        }
        else
        {
            var own = OwnSetting("mode");
            if (own.Count > 0)
            {
                details.Add("Connectors with their own mode keep it: " + string.Join(", ", own) + ".");
            }

            details.Add("A running gateway restarts to apply it.");
        }

        var warning = weakens
            ? $"This weakens protection: {scope} stops blocking; findings are only logged, and its hooks fail open while the gateway is down unless their fail mode is set to closed."
            : string.Empty;
        return new PolicyConsequence(
            connector.Length > 0 ? $"Switch {connector} to {mode} mode?" : $"Set the global guardrail mode to {mode}?",
            $"{old} → {mode}",
            details,
            warning,
            $"Switch to {mode}");
    }

    // ---- posture: tool-call levels -----------------------------------------------------------------------------------------

    private static (string Label, string Source) LevelsSetting(ScopeLevels levels, string kind) =>
        string.Equals(kind, "block", StringComparison.Ordinal) ? (levels.BlockAt, levels.BlockSource) : (levels.AlertAt, levels.AlertSource);

    private PolicyConsequence LevelConsequence(ScopePosture row, string kind, string choice, LevelChange change)
    {
        var scope = row.Scope.Length > 0 ? row.Scope : PolicyScopes.Global;
        var effect = change.EffectFor(scope);
        var before = effect?.Before ?? RowLevels(row);
        var after = effect?.After ?? before;
        var (old, _) = LevelsSetting(before, kind);
        var (current, newSource) = LevelsSetting(after, kind);
        var mode = PolicyLevels.Or(row.Mode, "observe");
        var hilt = PolicyLevels.Or(row.Hilt, "off");
        var connector = ConnectorOf(row);
        var details = new List<string> { PolicyLevels.PostureSummary(scope, mode, after.BlockAt, after.AlertAt, hilt) };
        if (string.Equals(choice, PolicyLevels.Inherit, StringComparison.Ordinal))
        {
            var follows = string.Equals(newSource, "global", StringComparison.Ordinal) ? "the global level" : "its rule pack's level";
            details.Add($"Follows {follows} again.");
        }
        else if (string.Equals(old, current, StringComparison.Ordinal))
        {
            var others = change.Connector.Length > 0 ? "the global or rule pack level" : "the rule pack's level";
            details.Add($"Stays at {current}, but no longer follows {others} if that changes.");
        }

        if (connector.Length > 0 && !Catalog.MultiConnector)
        {
            details.Add("This install has one connector, so this sets the global level.");
        }
        else if (connector.Length > 0)
        {
            details.Add("Only this connector changes; a running gateway restarts to apply it.");
        }
        else if (Catalog.MultiConnector)
        {
            details.Add("Every connector without its own level follows it; a running gateway restarts to apply it.");
            if (change.KeepOwn.Count > 0)
            {
                details.Add("Keep their own level: " + string.Join(", ", change.KeepOwn) + ".");
            }
        }
        else
        {
            details.Add("A running gateway restarts to apply it.");
        }

        if (after.AlertClamped)
        {
            details.Add($"Alerts start at {after.AlertAt}: anything that blocks also alerts.");
        }

        details.Add("These are the levels for tool calls. The policy's levels for LLM traffic through the guardrail proxy are separate (Policies view).");

        var loosened = change.LoosenedText();
        var what = string.Equals(kind, "block", StringComparison.Ordinal) ? "block" : "alert";
        var target = change.Connector;
        string heading;
        string label;
        if (string.Equals(choice, PolicyLevels.Inherit, StringComparison.Ordinal))
        {
            heading = target.Length > 0 ? $"Clear {target}'s own {what} level?" : $"Clear the global {what} level?";
            label = "Use the inherited level";
        }
        else
        {
            heading = target.Length > 0 ? $"Set {target}'s {what} level to {choice}?" : $"Set the global {what} level to {choice}?";
            label = $"{char.ToUpperInvariant(what[0])}{what[1..]} at {choice}";
        }

        return new PolicyConsequence(heading, $"{what} level: {old} → {current}", details, loosened.Length > 0 ? $"This weakens protection: {loosened}." : string.Empty, label);
    }

    // ---- posture: human approval -------------------------------------------------------------------------------------------

    private PolicyConsequence HiltConsequence(ScopePosture row, string level, bool weakens)
    {
        var old = PolicyLevels.Or(row.Hilt, "off");
        var scope = row.Scope.Length > 0 ? row.Scope : PolicyScopes.Global;
        var mode = PolicyLevels.Or(row.Mode, "observe");
        var (block, alert) = ScopeLevelLabels(row);
        var connector = ConnectorOf(row);
        var details = new List<string> { PolicyLevels.PostureSummary(scope, "action", block, alert, level) };
        if (!string.Equals(mode, "action", StringComparison.Ordinal))
        {
            details.Add($"{scope} is in observe mode, so nothing waits for approval until it runs in action mode.");
        }

        if (connector.Length > 0 && !Catalog.MultiConnector)
        {
            details.Add("This install has one connector, so this sets approval for every connector.");
        }
        else if (connector.Length == 0 && Catalog.MultiConnector)
        {
            var others = Postures.Where(r => ConnectorOf(r).Length > 0).Select(r => r.Scope).ToArray();
            if (others.Length > 0)
            {
                details.Add("Sets approval on every active connector: " + string.Join(", ", others) + ".");
            }
        }

        details.Add("The gateway restarts to apply it.");

        var warning = string.Empty;
        if (weakens)
        {
            var lost = PolicyLevels.ActionsWeaken(PolicyLevels.SeverityActions(block, alert, old), PolicyLevels.SeverityActions(block, alert, level));
            warning = lost.Count > 0
                ? $"This weakens protection: {string.Join(", ", lost)} findings on {scope} are no longer held for a person."
                : $"This weakens protection: {scope} asks a person for fewer findings.";
        }

        var title = level == "off" ? "Turn off human approval" : $"Ask a human for {level}";
        var words = connector.Length > 0 ? connector : "the global default";
        return new PolicyConsequence($"{title} on {words}?", $"{old} → {level}", details, warning, level == "off" ? "Turn approval off" : $"Ask for {level}");
    }

    // ---- opt-in packs ------------------------------------------------------------------------------------------------------

    private IReadOnlyList<PolicyAction> ProtectionActions(ScopePosture row, ProtectionPack pack)
    {
        if (pack.IsStaged)
        {
            return Array.Empty<PolicyAction>();
        }

        var enable = !ScopeProtection(row).Contains(pack.Name, StringComparer.Ordinal);
        var command = CommandConnector(row);
        var consequence = ProtectionConsequence(row, pack, enable, out var weakens);
        return new[]
        {
            Make(
                PolicyActionKind.Protection,
                "Opt-in pack",
                enable ? "Turn on" : "Turn off",
                PolicyIntents.Protection(pack.Name, enable, command),
                false,
                weakens,
                consequence),
        };
    }

    private PolicyConsequence ProtectionConsequence(ScopePosture row, ProtectionPack pack, bool enable, out bool weakens)
    {
        var title = PolicyLevels.Or(pack.Title, pack.Name);
        var covers = PolicyLevels.Or(pack.Covers, pack.Summary);
        var scope = row.Scope.Length > 0 ? row.Scope : PolicyScopes.Global;
        var connector = CommandConnector(row);
        var where = connector.Length > 0 ? connector : "the global pack";
        var details = new List<string>();
        if (ConnectorOf(row).Length > 0 && connector.Length == 0)
        {
            details.Add("This install has one connector, so this changes the global pack.");
        }

        var warning = string.Empty;
        weakens = !enable;
        string summary;
        if (enable)
        {
            summary = ProtectionClaim(pack.Name, scope);
            var rules = pack.RuleCount > 0 ? $" ({I(pack.RuleCount)} rule{(pack.RuleCount != 1 ? "s" : string.Empty)}; the details list them)" : string.Empty;
            details.Add(covers.Length > 0 ? $"It blocks: {covers}{rules}." : "It adds the pack's blocking rules.");
            var target = ComposedPackFolder(row);
            var profileFolder = Path.GetFileName(target);
            var scopeFolder = Path.GetFileName(Path.GetDirectoryName(target) ?? string.Empty);
            var current = PolicyLevels.Or(row.Pack, "-");
            details.Add(
                $"Builds {Path.Combine("guardrail", scopeFolder, profileFolder)} in your policy folder from {current} and the opt-in packs, checks it, and switches {where} to it; a running gateway restarts.");
            if (connector.Length == 0)
            {
                var own = OwnSetting("pack");
                if (own.Count > 0)
                {
                    details.Add("Not covered, they have their own pack; turn it on there too: " + string.Join(", ", own) + ".");
                }
            }

            var before = PolicyLevels.PackProfile(row.PackFolder);
            var after = PolicyLevels.PackProfile(target);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                weakens = true;
                var (oldBlock, oldAlert) = PolicyLevels.ProfileLevelsFor(row.PackFolder);
                var (newBlock, newAlert) = PolicyLevels.ProfileLevelsFor(target);
                warning = $"This weakens protection: the gateway takes tool-call levels from the pack folder's name, so {profileFolder} is treated as {after}: blocks {newBlock} and alerts on {newAlert} instead of {oldBlock} and {oldAlert} ({before}).";
            }
        }
        else
        {
            summary = $"{scope} stops blocking what the pack covers.";
            var remaining = ScopeProtection(row).Where(p => !string.Equals(p, pack.Name, StringComparison.Ordinal)).ToArray();
            if (remaining.Length == 0)
            {
                details.Add($"That is the last opt-in pack, so {where} goes back to its base pack.");
            }

            details.Add("A running gateway restarts to load the change.");
            details.Add(covers.Length > 0 ? $"No longer blocked: {covers}." : "Its rules stop applying.");
            warning = $"This weakens protection: {title} is turned off for {scope}.";
        }

        var verb = enable ? "Turn on" : "Turn off";
        return new PolicyConsequence($"{verb} {title} for {where}?", summary, details, warning, verb);
    }

    /// <summary>Where <c>guardrail protection enable</c> composes a scope's pack: <c>protected-SCOPE/PROFILE</c>, the profile of its base pack being the last folder (the gateway reads tool-call levels from it).</summary>
    private string ComposedPackFolder(ScopePosture row)
    {
        var scope = row.Scope.Length > 0 ? row.Scope : PolicyScopes.Global;
        var root = Catalog.PolicyFolder.Length > 0 ? Catalog.PolicyFolder : "<policy dir>";
        return Path.Combine(root, "guardrail", $"protected-{scope}", PolicyLevels.PackProfile(row.PackFolder));
    }

    // ---- named policies ----------------------------------------------------------------------------------------------------

    private IReadOnlyList<PolicyAction> PolicyActionsOf(NamedPolicy policy)
    {
        var actions = new List<PolicyAction>();
        if (!PolicyNames.IsSafe(policy.Name))
        {
            return actions;
        }

        var active = ActivePolicy;
        var weaker = PolicyLevels.PolicyWeakenings(active, policy);
        actions.Add(Make(
            PolicyActionKind.Activate,
            "Activate",
            policy.IsActive ? "Active" : "Activate",
            PolicyIntents.Activate(policy.Name),
            policy.IsActive,
            weaker.Count > 0,
            ActivateConsequence(active, policy, weaker)));

        foreach (var (kind, levels, group, actionKind, before) in new[]
                 {
                     ("block", PolicyLevels.BlockLevels, "LLM block level", PolicyActionKind.LlmBlockLevel, policy.BlockAt),
                     ("alert", PolicyLevels.AlertLevels, "LLM alert level", PolicyActionKind.LlmAlertLevel, policy.AlertAt),
                 })
        {
            foreach (var level in levels)
            {
                // An edit weakens only what is enforced: the active policy's thresholds (another policy's are a draft until it is activated).
                var weakens = policy.IsActive && PolicyLevels.ThresholdWeakens(before, level);
                actions.Add(Make(
                    actionKind,
                    group,
                    level,
                    PolicyIntents.Threshold(kind, level, policy.Name),
                    string.Equals(before, level, StringComparison.OrdinalIgnoreCase),
                    weakens,
                    ThresholdConsequence(kind, level, policy, weakens)));
            }
        }

        return actions;
    }

    private static PolicyConsequence ActivateConsequence(NamedPolicy? active, NamedPolicy chosen, IReadOnlyList<string> weaker)
    {
        var before = active?.Name ?? "no policy";
        var details = new List<string>
        {
            $"block {PolicyLevels.Or(active?.BlockAt, "-")} → {chosen.BlockAt} · alert {PolicyLevels.Or(active?.AlertAt, "-")} → {chosen.AlertAt} · installs {PolicyLevels.Or(active?.InstallBlockAt, "-")} → {chosen.InstallBlockAt}",
        };
        var effects = PolicyLevels.PolicySideEffects(chosen);
        if (effects.Count > 0)
        {
            details.Add("Also: " + string.Join(" · ", effects));
        }

        details.Add("The gateway reloads the policy.");
        details.Add("Its guardrail thresholds govern LLM traffic through the proxy; tool-call blocking is unchanged (see Posture).");
        return new PolicyConsequence(
            $"Activate the {chosen.Name} policy?",
            $"{before} → {chosen.Name}",
            details,
            weaker.Count > 0 ? "This weakens protection: " + string.Join("; ", weaker) + "." : string.Empty,
            $"Activate {chosen.Name}");
    }

    private static PolicyConsequence ThresholdConsequence(string kind, string level, NamedPolicy policy, bool weakens)
    {
        var block = string.Equals(kind, "block", StringComparison.Ordinal);
        var old = PolicyLevels.Or(block ? policy.BlockAt : policy.AlertAt, "-");
        var details = new List<string>
        {
            "LLM traffic through the guardrail proxy only; tool calls keep each scope's levels (Posture view).",
        };
        if (!policy.IsActive)
        {
            details.Add($"The {policy.Name} policy isn't active, so nothing changes until you activate it.");
        }

        if (policy.IsBuiltIn && !policy.IsEdited)
        {
            details.Add($"The built-in {policy.Name} policy is copied to your policy folder first.");
        }

        if (policy.IsActive)
        {
            details.Add("The gateway reloads the policy.");
        }

        var what = block ? "Block" : "Alert";
        var verb = block ? "blocks" : "alerts on";
        return new PolicyConsequence(
            $"{what} LLM traffic at {level} in the {policy.Name} policy?",
            $"{old} → {level}",
            details,
            weakens ? $"This weakens protection: the policy {verb} {level} instead of {old}." : string.Empty,
            $"Set {what.ToLowerInvariant()} at {level}");
    }

    /// <summary>The severity-to-action line a level picker previews: <c>CRITICAL block · HIGH block · MEDIUM alert · LOW allow</c>.</summary>
    public static string ActionLine(string blockAt, string alertAt, string hilt) =>
        string.Join(" · ", PolicyLevels.SeverityActions(blockAt, alertAt, hilt).Select(p => $"{p.Severity} {p.Action}"));
}
