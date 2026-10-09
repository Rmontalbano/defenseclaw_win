namespace DefenseClaw.Core.Policy.Model;

/// <summary>How a table column draws its cells.</summary>
public enum PolicyCellKind
{
    /// <summary>Plain text, trimmed with an ellipsis.</summary>
    Text,

    /// <summary>A path or digest, in the monospace face.</summary>
    Mono,

    /// <summary>A state with a mark (<c>on</c>, <c>Active</c>), coloured by its tone.</summary>
    Status,

    /// <summary>A short label as a pill (<c>Built-in</c>, <c>Custom</c>).</summary>
    Pill,

    /// <summary>A severity as a badge.</summary>
    Severity,
}

/// <summary>One cell of a view's table.</summary>
/// <param name="Text">The text exactly as the runtime's own table prints it, glyphs included (<c>● on</c>); what a search and a test see.</param>
/// <param name="Tone">The tone key (<c>Ok</c>, <c>Warn</c>, <c>Bad</c>, <c>Neutral</c>, a severity); empty for none.</param>
/// <param name="Display">What a panel draws when it draws its own mark (<c>on</c>): the text without the glyph. Null means <paramref name="Text"/>.</param>
public sealed record PolicyCell(string Text, string Tone = "", string? Display = null)
{
    /// <summary>The text a panel draws.</summary>
    public string Shown => Display ?? Text;
}

/// <summary>One column of a view's table.</summary>
/// <param name="Header">The column's title.</param>
/// <param name="Kind">How its cells are drawn.</param>
/// <param name="Weight">Its share of the table's width.</param>
public sealed record PolicyColumn(string Header, PolicyCellKind Kind, double Weight);

/// <summary>One row of a view's table.</summary>
/// <param name="Key">A stable key within the view (the scope, the pack, the chain id ...).</param>
/// <param name="Cells">The cells, one per column.</param>
/// <param name="SearchText">The text a filter matches: the cells and the detail.</param>
public sealed record PolicyTableRow(string Key, IReadOnlyList<PolicyCell> Cells, string SearchText);

/// <summary>One view's table.</summary>
public sealed record PolicyTable(string View, IReadOnlyList<PolicyColumn> Columns, IReadOnlyList<PolicyTableRow> Rows);

/// <summary>The detail of one row: a title and plain lines (a blank line is spacing).</summary>
public sealed record PolicyRowDetail(string Title, IReadOnlyList<string> Lines)
{
    public static PolicyRowDetail None { get; } = new(string.Empty, Array.Empty<string>());
}

public sealed partial class PolicyModel
{
    private const string Dot = "●";
    private const string Ring = "○";

    /// <summary>
    /// A view's table. <paramref name="runtimeWidth"/> 0 gives the cells whole; a width of 100 or more gives them cut exactly as the runtime's
    /// own table cuts them at that width (used to check this port against the runtime's rows: the descriptions are cut with an ellipsis).
    /// </summary>
    /// <param name="view">One of <see cref="ViewIds"/>.</param>
    /// <param name="scope">The scope the opt-in packs and the rule families are shown for; ignored by the other views.</param>
    /// <param name="runtimeWidth">0, or a table width of 100 or more characters.</param>
    public PolicyTable Table(string view, ScopePosture? scope = null, int runtimeWidth = 0) => view switch
    {
        "posture" => PostureTable(runtimeWidth),
        "optin" => OptInTable(scope, runtimeWidth),
        "chains" => ChainTable(scope, runtimeWidth),
        "families" => FamilyTable(scope, runtimeWidth),
        "packs" => PackTable(scope, runtimeWidth),
        "policies" => PolicyTableOf(scope, runtimeWidth),
        _ => new PolicyTable(view, Array.Empty<PolicyColumn>(), Array.Empty<PolicyTableRow>()),
    };

    private static string Cut(string text, int runtimeWidth, int width) => runtimeWidth > 0 ? PolicyLevels.Fit(text, width) : text;

    private PolicyTable Finish(string view, IReadOnlyList<PolicyColumn> columns, IEnumerable<(string Key, PolicyCell[] Cells)> rows, ScopePosture? scope)
    {
        var built = new List<PolicyTableRow>();
        foreach (var (key, cells) in rows)
        {
            var detail = Detail(view, key, scope);
            var search = string.Join(' ', cells.Select(c => c.Text).Concat(new[] { detail.Title }).Concat(detail.Lines)).Trim();
            built.Add(new PolicyTableRow(key, cells, search));
        }

        return new PolicyTable(view, columns, built);
    }

    // ---- 1. posture --------------------------------------------------------------------------------------------------------

    private PolicyTable PostureTable(int runtimeWidth)
    {
        var total = ProtectionTotal;
        var columns = new[]
        {
            new PolicyColumn("Scope", PolicyCellKind.Text, 1.1),
            new PolicyColumn("Mode", PolicyCellKind.Status, 1.0),
            new PolicyColumn("Blocks at", PolicyCellKind.Text, 0.9),
            new PolicyColumn("Alerts at", PolicyCellKind.Text, 0.9),
            new PolicyColumn("Approval", PolicyCellKind.Text, 0.9),
            new PolicyColumn("Rule pack", PolicyCellKind.Text, 1.4),
            new PolicyColumn("Opt-in", PolicyCellKind.Text, 0.8),
        };

        var rows = Postures.Select(row =>
        {
            var (block, alert) = ScopeLevelLabels(row);
            var own = ConnectorOf(row).Length > 0;
            var mode = PolicyLevels.Or(row.Mode, "observe");
            var pack = ScopePackLabel(row);
            var on = ScopeProtection(row).Count;
            var modeText = own && string.Equals(row.ModeSource, "override", StringComparison.Ordinal) ? mode + " (own)" : mode;
            var packText = own && string.Equals(row.PackSource, "override", StringComparison.Ordinal) ? pack + " (own)" : pack;
            var optIn = total > 0 ? $"{I(on)} of {I(total)}" : I(on);
            var hilt = PolicyLevels.Or(row.Hilt, "off");
            return (row.Scope, new[]
            {
                new PolicyCell(Cut(row.Scope, runtimeWidth, 16)),
                new PolicyCell(modeText, string.Equals(mode, "action", StringComparison.Ordinal) ? "Ok" : "Neutral", modeText),
                new PolicyCell(block),
                new PolicyCell(alert),
                new PolicyCell(hilt, string.Equals(hilt, "off", StringComparison.Ordinal) ? "Neutral" : "Ok"),
                new PolicyCell(Cut(packText, runtimeWidth, 22)),
                new PolicyCell(optIn),
            });
        });

        return Finish("posture", columns, rows, null);
    }

    // ---- 2. opt-in packs ---------------------------------------------------------------------------------------------------

    private PolicyTable OptInTable(ScopePosture? scope, int runtimeWidth)
    {
        var columns = new[]
        {
            new PolicyColumn("Pack", PolicyCellKind.Text, 1.6),
            new PolicyColumn("Covers", PolicyCellKind.Text, 3.0),
            new PolicyColumn("Rules", PolicyCellKind.Text, 0.6),
            new PolicyColumn("State", PolicyCellKind.Status, 0.9),
        };

        var enabled = ScopeProtection(scope);
        var rows = Protection.Select(pack =>
        {
            var title = PackShortTitle(PolicyLevels.Or(pack.Title, pack.Name));
            string state, shown, tone, rules, covers;
            if (pack.IsStaged)
            {
                (state, shown, tone, rules) = ("─ staged", "staged", "Neutral", "-");
                covers = "staged, not available yet";
            }
            else
            {
                var on = enabled.Contains(pack.Name, StringComparer.Ordinal);
                (state, shown, tone, rules) = on ? (Dot + " on", "on", "Ok", I(pack.RuleCount)) : (Ring + " off", "off", "Neutral", I(pack.RuleCount));
                covers = PolicyLevels.Or(pack.Covers, "-");
            }

            return (pack.Name, new[]
            {
                new PolicyCell(Cut(title, runtimeWidth, 26)),
                new PolicyCell(Cut(covers, runtimeWidth, runtimeWidth - 26 - 5 - 8 - 8)),
                new PolicyCell(rules),
                new PolicyCell(state, tone, shown),
            });
        });

        return Finish("optin", columns, rows, scope);
    }

    // ---- 3. chains ---------------------------------------------------------------------------------------------------------

    private PolicyTable ChainTable(ScopePosture? scope, int runtimeWidth)
    {
        var columns = new[]
        {
            new PolicyColumn("Effect", PolicyCellKind.Status, 0.9),
            new PolicyColumn("Chain", PolicyCellKind.Text, 4.0),
            new PolicyColumn("Severity", PolicyCellKind.Severity, 0.8),
            new PolicyColumn("Domain", PolicyCellKind.Text, 1.2),
        };

        var titleWidth = Math.Max(12, runtimeWidth - 1 - 8 - 6);
        var rows = ChainRows().Select(item =>
        {
            var chain = item.Chain;
            var severity = PolicyLevels.Or(chain.Severity, "-");
            var marker = chain.CanBlock ? "✓" : "◐";
            return (chain.Id, new[]
            {
                new PolicyCell(marker, chain.CanBlock ? "Ok" : "Neutral", chain.CanBlock ? "Can block" : "Alert only"),
                new PolicyCell(Cut(chain.Title, runtimeWidth, titleWidth)),
                new PolicyCell(runtimeWidth > 0 && severity.Length > 8 ? severity[..8] : severity, ChainSeverityTone(chain.Severity)),
                new PolicyCell(item.Label),
            });
        });

        return Finish("chains", columns, rows, scope);
    }

    private static string ChainSeverityTone(string severity) => severity.Trim().ToUpperInvariant() switch
    {
        "CRITICAL" => "Critical",
        "HIGH" => "High",
        "MEDIUM" => "Medium",
        "LOW" => "Low",
        _ => "Info",
    };

    // ---- 4. rule families --------------------------------------------------------------------------------------------------

    private PolicyTable FamilyTable(ScopePosture? scope, int runtimeWidth)
    {
        var columns = new[]
        {
            new PolicyColumn("Family", PolicyCellKind.Text, 1.2),
            new PolicyColumn("Rules", PolicyCellKind.Text, 0.6),
            new PolicyColumn("Enabled", PolicyCellKind.Text, 0.7),
            new PolicyColumn("What it catches", PolicyCellKind.Text, 4.0),
        };

        var rows = ScopeFamilies(scope).Select(family => (family.Name, new[]
        {
            new PolicyCell(Cut(family.Name, runtimeWidth, 15)),
            new PolicyCell(I(family.Rules)),
            new PolicyCell(I(family.Enabled)),
            new PolicyCell(Cut(PolicyLevels.Or(family.Description, "-"), runtimeWidth, runtimeWidth - 15 - 5 - 7 - 8)),
        }));

        return Finish("families", columns, rows, scope);
    }

    // ---- 5. policies -------------------------------------------------------------------------------------------------------

    private PolicyTable PolicyTableOf(ScopePosture? scope, int runtimeWidth)
    {
        // Block / alert here are the policy's levels for LLM traffic through the guardrail proxy; the Posture view has the tool-call levels.
        var columns = new[]
        {
            new PolicyColumn("Active", PolicyCellKind.Status, 0.7),
            new PolicyColumn("Policy", PolicyCellKind.Text, 1.4),
            new PolicyColumn("Kind", PolicyCellKind.Pill, 0.8),
            new PolicyColumn("LLM block", PolicyCellKind.Text, 0.9),
            new PolicyColumn("LLM alert", PolicyCellKind.Text, 0.9),
            new PolicyColumn("Install block", PolicyCellKind.Text, 0.9),
            new PolicyColumn("Firewall", PolicyCellKind.Text, 0.8),
            new PolicyColumn("Description", PolicyCellKind.Text, 3.0),
        };

        var rows = Catalog.Policies.Select(policy => (policy.Name, new[]
        {
            policy.IsActive ? new PolicyCell(Dot, "Ok", "Active") : new PolicyCell(string.Empty),
            new PolicyCell(Cut(policy.Name, runtimeWidth, 24)),
            new PolicyCell(policy.IsBuiltIn ? "built-in" : "custom", policy.IsBuiltIn ? "Neutral" : "Medium", policy.IsBuiltIn ? "Built-in" : "Custom"),
            new PolicyCell(PolicyLevels.Or(policy.BlockAt, "-")),
            new PolicyCell(PolicyLevels.Or(policy.AlertAt, "-")),
            new PolicyCell(PolicyLevels.Or(policy.InstallBlockAt, "-")),
            new PolicyCell(PolicyLevels.Or(policy.FirewallDefault, "-")),
            new PolicyCell(Cut(PolicyLevels.Or(policy.Description, "-"), runtimeWidth, 40)),
        }));

        return Finish("policies", columns, rows, scope);
    }

    // ---- 6. rule packs -----------------------------------------------------------------------------------------------------

    private PolicyTable PackTable(ScopePosture? scope, int runtimeWidth)
    {
        var columns = new[]
        {
            new PolicyColumn("Scope", PolicyCellKind.Text, 1.0),
            new PolicyColumn("Pack", PolicyCellKind.Text, 1.2),
            new PolicyColumn("Source", PolicyCellKind.Text, 1.2),
            new PolicyColumn("Folder", PolicyCellKind.Mono, 3.0),
        };

        var rows = PackRows.Select(row =>
        {
            string source;
            if (row.IsGlobal)
            {
                source = string.Equals(row.Source, "global", StringComparison.Ordinal) ? "configured" : "built-in default";
            }
            else
            {
                source = row.Source switch
                {
                    "global" => "uses global",
                    "override" => "own pack",
                    "default" => "built-in default",
                    _ => row.Source,
                };
            }

            return (row.Scope, new[]
            {
                new PolicyCell(row.Scope),
                new PolicyCell(PolicyLevels.Or(row.Pack, "-")),
                new PolicyCell(source),
                new PolicyCell(Cut(PolicyLevels.Or(row.Folder, "-"), runtimeWidth, 48)),
            });
        });

        return Finish("packs", columns, rows, scope);
    }

    // ---- the detail of a row (the runtime's "aside") -----------------------------------------------------------------------

    /// <summary>The detail of one row: the runtime's own text for it.</summary>
    /// <param name="view">One of <see cref="ViewIds"/>.</param>
    /// <param name="key">The row's <see cref="PolicyTableRow.Key"/>.</param>
    /// <param name="scope">The scope the opt-in packs and the rule families are shown for.</param>
    public PolicyRowDetail Detail(string view, string key, ScopePosture? scope = null) => view switch
    {
        "posture" => PostureDetail(ScopeRow(key)),
        "optin" => OptInDetail(ProtectionPackNamed(key), scope),
        "chains" => ChainDetail(Catalog.Chains.FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.Ordinal))),
        "families" => FamilyDetail(ScopeFamilies(scope).FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.Ordinal)), scope),
        "packs" => PackDetail(PackRows.FirstOrDefault(r => string.Equals(r.Scope, key, StringComparison.Ordinal))),
        "policies" => PolicyDetailOf(PolicyNamed(key)),
        _ => PolicyRowDetail.None,
    };

    private PolicyRowDetail PostureDetail(ScopePosture? row)
    {
        if (row is null)
        {
            return PolicyRowDetail.None;
        }

        var scope = row.Scope;
        var mode = PolicyLevels.Or(row.Mode, "observe");
        var hilt = PolicyLevels.Or(row.Hilt, "off");
        var (block, alert) = ScopeLevelLabels(row);
        var source = ConnectorOf(row).Length > 0 && string.Equals(row.ModeSource, "override", StringComparison.Ordinal) ? "its own mode" : "global mode";
        var lines = new List<string>
        {
            PolicyLevels.PostureSummary(scope, mode, block, alert, hilt),
            string.Empty,
            $"Tool calls ({mode}, {source}):",
        };
        lines.AddRange(PolicyLevels.MatrixLines(mode, block, alert, hilt));
        lines.Add(LevelsLine(row));
        lines.Add(string.Empty);
        lines.Add(PackLine(row));

        var protection = ScopeProtection(row);
        if (protection.Count > 0)
        {
            var titles = protection.Select(name => ProtectionPackNamed(name) is { Title.Length: > 0 } pack ? pack.Title : name);
            lines.Add("Opt-in: " + string.Join(", ", titles));
        }

        if (ActivePolicy is { } active)
        {
            lines.Add($"LLM traffic through the guardrail proxy: the {active.Name} policy blocks {PolicyLevels.Or(active.BlockAt, "?")}, alerts at {PolicyLevels.Or(active.AlertAt, "?")}.");
        }

        return new PolicyRowDetail($"Posture · {scope}", lines);
    }

    private string PackLine(ScopePosture row)
    {
        var pack = PolicyLevels.Or(row.Pack, "-");
        var profile = PolicyLevels.PackProfile(row.PackFolder);
        if (Catalog.PackBases.TryGetValue(row.PackFolder, out var baseName) && baseName.Length > 0)
        {
            var on = ScopeProtection(row).Count;
            return $"Rule pack: {pack} = {baseName} + {I(on)} opt-in pack{(on != 1 ? "s" : string.Empty)}; its folder name gives it {profile} levels (the gateway reads them from there).";
        }

        return $"Rule pack: {pack} ({profile} levels)";
    }

    private PolicyRowDetail OptInDetail(ProtectionPack? pack, ScopePosture? scopeRow)
    {
        if (pack is null)
        {
            return PolicyRowDetail.None;
        }

        var lines = new List<string> { PolicyLevels.Or(pack.Summary, "-") };
        if (pack.IsStaged)
        {
            lines.Add("Staged: this pack is a contract only and can't be turned on yet.");
        }
        else
        {
            var scope = scopeRow is { Scope.Length: > 0 } ? scopeRow.Scope : PolicyScopes.Global;
            var state = ScopeProtection(scopeRow).Contains(pack.Name, StringComparer.Ordinal) ? "on" : "off";
            lines.Add($"{scope}: {state}. {ProtectionClaim(pack.Name, scope)}");
        }

        var rules = pack.Rules.Count > 0 ? pack.Rules : pack.RuleIds.Select(id => new ProtectionRule(id, string.Empty, string.Empty)).ToArray();
        if (rules.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Rules ({I(rules.Count)}):");
            lines.AddRange(rules.Select(rule => "  " + string.Join(" · ", new[] { rule.Id, rule.Severity, rule.Title }.Where(part => part.Length > 0))));
        }

        return new PolicyRowDetail(PolicyLevels.Or(pack.Title, pack.Name), lines);
    }

    private static PolicyRowDetail ChainDetail(ToolChain? chain)
    {
        if (chain is null)
        {
            return PolicyRowDetail.None;
        }

        var lines = new List<string>
        {
            $"{(chain.CanBlock ? "✓ Can block" : "◐ Alert only")} · {PolicyLevels.Or(chain.Severity, "no severity")} · {ChainDomainLabel(chain.Domain.ToLowerInvariant())}",
            $"Looks at {WindowText(chain)}.",
        };
        if (chain.Requires.Count > 0)
        {
            lines.Add("Requires: " + string.Join(", ", chain.Requires) + ".");
        }

        if (chain.Note.Length > 0)
        {
            lines.Add(chain.Note);
        }

        lines.Add($"id: {chain.Id}");
        return new PolicyRowDetail(chain.Title, lines);
    }

    private static string WindowText(ToolChain chain)
    {
        var parts = new List<string>();
        if (chain.EventWindow > 0)
        {
            parts.Add($"the last {I(chain.EventWindow)} tool calls");
        }

        if (chain.TimeWindowSeconds > 0)
        {
            parts.Add(chain.TimeWindowSeconds >= 60 ? $"{I(chain.TimeWindowSeconds / 60)} minutes" : $"{I(chain.TimeWindowSeconds)} seconds");
        }

        return parts.Count > 0 ? string.Join(" within ", parts) : "-";
    }

    private static PolicyRowDetail FamilyDetail(RuleFamily? family, ScopePosture? row)
    {
        if (family is null)
        {
            return PolicyRowDetail.None;
        }

        var lines = new List<string>
        {
            PolicyLevels.Or(family.Description, "-"),
            $"{I(family.Enabled)} of {I(family.Rules)} rules enabled",
            $"Pack: {PolicyLevels.Or(row?.Pack, "-")} ({PolicyLevels.Or(row?.Scope, "-")})",
        };
        return new PolicyRowDetail($"Rule family · {family.Name}", lines);
    }

    private PolicyRowDetail PackDetail(ScopePack? row)
    {
        if (row is null)
        {
            return PolicyRowDetail.None;
        }

        var lines = new List<string> { $"pack: {row.Pack}", $"source: {row.Source}", $"folder: {row.Folder}" };
        var pack = Catalog.Packs.FirstOrDefault(p => string.Equals(p.Folder, row.Folder, StringComparison.Ordinal));
        if (pack is not null)
        {
            lines.Add($"kind: {pack.Kind}");
            if (pack.UsedBy.Count > 0)
            {
                lines.Add($"used by: {string.Join(", ", pack.UsedBy)}");
            }
        }

        return new PolicyRowDetail($"Rule pack · {row.Scope}", lines);
    }

    private static PolicyRowDetail PolicyDetailOf(NamedPolicy? policy)
    {
        if (policy is null)
        {
            return PolicyRowDetail.None;
        }

        var effects = string.Join(" · ", PolicyLevels.PolicySideEffects(policy));
        var lines = new List<string>
        {
            $"LLM traffic (guardrail proxy): block {policy.BlockAt} · alert {policy.AlertAt}",
            $"installs blocked at {policy.InstallBlockAt} · firewall {PolicyLevels.Or(policy.FirewallDefault, "unchanged")} · approval {PolicyLevels.HiltWord(policy.Hilt)}",
            PolicyLevels.Or(policy.Description, "-"),
            policy.SourcePath + (effects.Length > 0 ? $"  (activating it: {effects})" : string.Empty),
        };
        return new PolicyRowDetail($"Policy · {policy.Name} ({(policy.IsBuiltIn ? "built-in" : "custom")}{(policy.IsActive ? ", active" : string.Empty)})", lines);
    }
}
