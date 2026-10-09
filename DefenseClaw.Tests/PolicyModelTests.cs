using System.Text.Json;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The seven-view Policies model (<see cref="PolicyModel"/>): the rows, headers, headlines and navigation of every view against what the
/// pinned runtime's own model rendered from the same catalog (the Phase 2 capture, <c>policy-model/phase2-model.json</c>), and the
/// scope, level and inheritance rules against the pinned runtime's test scenarios (<c>tui/test_protection_levels.py</c>, source commit 95159fd).
/// </summary>
public sealed class PolicyModelTests
{
    // ---- the Phase 2 capture ------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_view_renders_the_rows_the_runtime_rendered_from_the_same_catalog()
    {
        using var document = PolicyModelFixtures.Phase2();
        var views = document.RootElement.GetProperty("views");
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());
        var scope = model.ScopeRow(PolicyScopes.Global);
        Assert.NotNull(scope);

        // The capture is the runtime's seven views; the first six are this model's. The seventh, Sandbox packs, is left out where the
        // runtime cannot run sandboxes (Windows), and whatever the capture holds for it is ignored.
        var captured = views.EnumerateObject().Select(v => v.Name).ToArray();
        Assert.Equal(7, captured.Length);
        Assert.Equal("sandbox_packs", captured[^1]);
        Assert.Equal(captured[..^1], PolicyModel.ViewIds);
        Assert.Equal(6, PolicyModel.ViewIds.Count);

        foreach (var view in PolicyModel.ViewIds)
        {
            var expected = views.GetProperty(view);
            var table = model.Table(view, scope, runtimeWidth: 160);

            Assert.Equal(expected.GetProperty("title").GetString(), PolicyModel.TitleOf(view));

            // A column the runtime leaves untitled (the mark) has a name here; every titled one matches.
            var columns = expected.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
            for (var i = 0; i < columns.Length; i++)
            {
                if (columns[i].Length > 0)
                {
                    Assert.Equal(columns[i], table.Columns[i].Header);
                }
            }

            var rows = expected.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()!).ToArray()).ToList();
            if (view == "chains")
            {
                // The runtime interleaves a heading row per domain; the table here has a Domain column instead.
                rows = rows.Where(r => !(r[0].Length == 0 && r[1].StartsWith("\u2500\u2500 ", StringComparison.Ordinal))).ToList();
            }

            Assert.Equal(expected.GetProperty("row_count").GetInt32() - (view == "chains" ? 8 : 0), table.Rows.Count);
            Assert.Equal(rows.Count, table.Rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                Assert.Equal(rows[i], table.Rows[i].Cells.Take(rows[i].Length).Select(c => c.Text).ToArray());
            }

            Assert.Equal(expected.GetProperty("header").GetString(), model.Header(160));
            Assert.Equal(expected.GetProperty("headline").GetString(), model.Headline(view, scope, 160));
            Assert.Equal(expected.GetProperty("empty_state").GetString(), model.EmptyState(view, scope));
            Assert.Equal(string.Empty, model.ViewError(view));
        }
    }

    [Fact]
    public void The_navigation_counts_are_the_runtimes_for_the_six_views_windows_shows()
    {
        using var document = PolicyModelFixtures.Phase2();
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        var captured = document.RootElement.GetProperty("nav_entries").EnumerateArray()
            .Select(e => (e[0].GetString()!, e[1].GetString()!, e[2].GetString()!))
            .ToArray();
        var actual = model.Nav(model.ScopeRow("global")).Select(e => (e.View, e.Title, e.Badge)).ToArray();

        // the capture lists seven entries, the last of them Sandbox packs; Windows has the other six
        Assert.Equal(7, captured.Length);
        Assert.Equal(captured[..^1], actual);
        Assert.Equal(new[] { "1", "0/5", "26", "7", "3", "1" }, actual.Select(a => a.Item3));
        Assert.Equal(new[] { "Posture", "Opt-in packs", "Chains", "Rule families", "Policies", "Rule packs" }, actual.Select(a => a.Item2));
    }

    [Fact]
    public void Windows_has_no_sandbox_packs_view_and_nothing_in_the_model_answers_for_one()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        Assert.DoesNotContain("sandbox_packs", PolicyModel.ViewIds);
        Assert.DoesNotContain(model.Nav(), e => e.View == "sandbox_packs" || e.Title.Contains("Sandbox", StringComparison.Ordinal));

        // asking for the view the runtime drops here gets nothing, never another view's rows
        Assert.Empty(model.Table("sandbox_packs").Rows);
        Assert.Empty(model.Table("sandbox_packs").Columns);
        Assert.Equal(PolicyRowDetail.None, model.Detail("sandbox_packs", "open"));
        Assert.Empty(model.Actions("sandbox_packs", "open"));
        Assert.Equal(string.Empty, model.Headline("sandbox_packs"));
        Assert.Equal(string.Empty, model.EmptyState("sandbox_packs"));
        Assert.Equal(string.Empty, model.ViewError("sandbox_packs"));
        Assert.Equal(0, model.RowCount("sandbox_packs"));
        Assert.Equal("sandbox_packs", PolicyModel.TitleOf("sandbox_packs"));
    }

    [Fact]
    public void The_chain_catalog_is_grouped_by_domain_with_the_blocking_ones_counted()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        Assert.Equal(26, model.Catalog.Chains.Count);
        Assert.Equal(4, model.BlockingChains);
        var rows = model.ChainRows();
        Assert.Equal(new[] { "SQL", "Kubernetes", "Cloud", "Host", "Credentials", "Data egress", "Network", "Security controls" }, rows.Select(r => r.Label).Distinct());
        Assert.Equal("Other", PolicyModel.ChainDomainLabel("something-new"));

        // A chain whose domain the catalog does not name goes last, under "Other".
        var extra = new PolicyModel(model.Catalog with { Chains = model.Catalog.Chains.Append(new ToolChain("chain.x", "New", "HIGH", "new-domain", false, 1, 30, Array.Empty<string>(), string.Empty)).ToArray() });
        Assert.Equal("Other", extra.ChainRows()[^1].Label);
    }

    [Fact]
    public void A_posture_row_explains_where_each_value_comes_from_and_keeps_the_llm_thresholds_apart()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        var detail = model.Detail("posture", "global");

        Assert.Equal("Posture \u00B7 global", detail.Title);
        Assert.Equal("The global default logs only; in action mode it would block CRITICAL and alert on MEDIUM+.", detail.Lines[0]);
        Assert.Contains("Tool calls (observe, global mode):", detail.Lines);
        Assert.Contains("  CRITICAL  log (would block)", detail.Lines);
        Assert.Contains("Levels from the default pack.", detail.Lines);
        Assert.Contains("Rule pack: default (default levels)", detail.Lines);
        Assert.Contains("LLM traffic through the guardrail proxy: the default policy blocks CRITICAL, alerts at MEDIUM+.", detail.Lines);

        // The posture table's columns are the tool-call levels; the policies table's are the LLM thresholds.
        Assert.Contains("Blocks at", model.Table("posture").Columns.Select(c => c.Header));
        Assert.Equal(new[] { "LLM block", "LLM alert" }, model.Table("policies").Columns.Select(c => c.Header).Where(h => h.StartsWith("LLM", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_opt_in_pack_detail_lists_its_rules_and_what_turning_it_on_asserts()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        var detail = model.Detail("optin", "database-destruction-protection", model.ScopeRow("global"));

        Assert.Equal("Database destruction protection", detail.Title);
        Assert.Contains("global: off. Turning it on tells DefenseClaw that every connector using the global pack works with a protected database.", detail.Lines);
        Assert.Contains("Rules (3):", detail.Lines);
        Assert.Contains("  impact.sql_unbounded_delete \u00B7 CRITICAL \u00B7 SQL DELETE without a WHERE clause", detail.Lines);

        var staged = model.Detail("optin", "ssh-authorized-keys-protection", model.ScopeRow("global"));
        Assert.Contains("Staged: this pack is a contract only and can't be turned on yet.", staged.Lines);
        Assert.Empty(model.Actions("optin", "ssh-authorized-keys-protection", model.ScopeRow("global")));
    }

    [Fact]
    public void A_chain_detail_says_what_it_looks_at_and_requires()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());
        var chain = model.Catalog.Chains.First(c => c.Id == "chain.secret_read_then_egress");

        var detail = model.Detail("chains", chain.Id);

        Assert.Equal(chain.Title, detail.Title);
        Assert.Equal("\u25D0 Alert only \u00B7 CRITICAL \u00B7 Data egress", detail.Lines[0]);
        Assert.Equal("Looks at the last 8 tool calls within 30 minutes.", detail.Lines[1]);
        Assert.StartsWith("Requires: same session, matching identity when known", detail.Lines[2], StringComparison.Ordinal);
        Assert.Equal("id: chain.secret_read_then_egress", detail.Lines[^1]);
    }

    // ---- empty and failed views ------------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_catalog_says_what_to_do_and_a_failed_part_says_why_instead()
    {
        var empty = new PolicyModel(new PolicyCatalog());

        Assert.Equal("No scopes found. Set up the guardrail first: defenseclaw setup guardrail", empty.EmptyState("posture"));
        Assert.Equal("The chain catalog was not found in this install.", empty.EmptyState("chains"));
        Assert.Equal("No named policies found. Create one with: defenseclaw policy create NAME", empty.EmptyState("policies"));
        Assert.Equal("No rule packs found.", empty.EmptyState("packs"));
        Assert.Equal("No opt-in protection packs were found in this install.", empty.EmptyState("optin"));
        Assert.Equal("The scope's rule pack has no rule files.", empty.EmptyState("families"));
        Assert.Equal("Tool-call levels are set per scope below \u00B7 no policy is active for LLM traffic", empty.Headline("posture"));

        var failed = new PolicyModel(new PolicyCatalog { PoliciesError = "exit 1", PackError = "no packs", PostureError = "no settings" });
        Assert.Equal(string.Empty, failed.EmptyState("policies"));
        Assert.Equal("Could not read policies: exit 1", failed.Headline("policies"));
        Assert.Equal("Could not read rule packs: no packs", failed.Headline("packs"));
        Assert.Equal("Could not read the protection settings: no settings", failed.Headline("posture"));
        Assert.Equal(string.Empty, failed.EmptyState("posture"));
        Assert.Equal("no settings", failed.ViewError("chains"));
    }

    // ---- scenarios of the pinned runtime's own tests ---------------------------------------------------------------------------

    private const string DefaultPack = "/p/guardrail/default";
    private const string StrictPack = "/p/guardrail/strict";

    private static ScopePosture Posture(string scope, string pack, string packFolder, string packSource, string blockAt, string alertAt, string levelsSource, string ownBlock, string ownAlert, string mode = "observe", string modeSource = "global") =>
        new(scope, mode, modeSource, "off", pack, packFolder, packSource, Array.Empty<string>(), blockAt, alertAt, levelsSource, ownBlock, ownAlert);

    /// <summary>The scenario of <c>levels_model()</c>: global blocks HIGH; codex is on the strict pack and alerts LOW; claudecode blocks MEDIUM itself.</summary>
    private static PolicyModel LevelsModel(bool multi = true, string globalBlock = "HIGH")
    {
        var settings = new GuardrailSettings(
            "observe",
            false,
            "HIGH",
            globalBlock,
            string.Empty,
            multi
                ? new Dictionary<string, ConnectorGuardrailSettings>(StringComparer.Ordinal)
                {
                    ["codex"] = new(string.Empty, null, null, string.Empty, "LOW"),
                    ["claudecode"] = new(string.Empty, null, null, "MEDIUM", string.Empty),
                }
                : new Dictionary<string, ConnectorGuardrailSettings>(StringComparer.Ordinal));
        var packs = new ListPacksDocument(
            new ScopePack("global", "default", DefaultPack, "global"),
            multi
                ? new[] { new ScopePack("codex", "strict", StrictPack, "override"), new ScopePack("claudecode", "default", DefaultPack, "global") }
                : new[] { new ScopePack("codex", "default", DefaultPack, "global") },
            Array.Empty<RulePackEntry>());
        var protection = new ProtectionDocument(
            Array.Empty<ProtectionPack>(),
            new[]
            {
                new ProtectionScope("global", "default", DefaultPack, Array.Empty<string>()),
                new ProtectionScope("codex", "strict", StrictPack, Array.Empty<string>()),
                new ProtectionScope("claudecode", "default", DefaultPack, Array.Empty<string>()),
            });

        var postures = PolicyPostureComposer.Compose(packs, protection, settings, out var problems);
        Assert.Empty(problems);
        return new PolicyModel(new PolicyCatalog
        {
            Postures = postures,
            GlobalPack = packs.Global,
            ConnectorPacks = packs.Connectors,
            MultiConnector = multi,
        });
    }

    [Fact]
    public void A_posture_row_carries_the_levels_the_catalog_resolved()
    {
        var model = LevelsModel();

        var rows = model.Table("posture").Rows.ToDictionary(r => r.Key, r => r.Cells.Select(c => c.Text).ToArray());

        // the global HIGH beats the strict pack's MEDIUM+
        Assert.Equal(new[] { "HIGH+", "LOW+" }, rows["codex"][2..4]);
        Assert.Equal(new[] { "MEDIUM+", "MEDIUM+" }, rows["claudecode"][2..4]);
        Assert.Equal(new[] { "HIGH+", "MEDIUM+" }, rows["global"][2..4]);
    }

    [Fact]
    public void A_global_level_reaches_every_connector_without_its_own()
    {
        var model = LevelsModel();
        var global = model.ScopeRow("global")!;

        var change = model.LevelChangeFor("block", global, "CRITICAL");

        Assert.Equal((string.Empty, "CRITICAL"), (change.Connector, change.Value));
        Assert.Equal(new[] { "claudecode" }, change.KeepOwn);
        Assert.Equal(new[] { "global", "codex" }, change.Weakened().Select(e => e.Scope));
        Assert.Equal("blocks CRITICAL instead of HIGH+ for the global default and codex", change.LoosenedText());

        // Clearing the global value hands codex back to its strict pack: stricter there.
        var cleared = model.LevelChangeFor("block", global, PolicyLevels.Inherit);
        Assert.Equal(new Dictionary<string, string> { ["global"] = "CRITICAL", ["codex"] = "MEDIUM+" }, cleared.Effects.ToDictionary(e => e.Scope, e => e.After.BlockAt));
        Assert.Equal(new[] { "global" }, cleared.Weakened().Select(e => e.Scope));
    }

    [Fact]
    public void The_pickers_current_choice_and_the_choices_that_loosen_are_the_runtimes()
    {
        var model = LevelsModel();
        var claude = model.ScopeRow("claudecode")!;

        Assert.Equal("MEDIUM+", model.LevelCurrent("block", claude));
        var weaker = new[] { "CRITICAL", "HIGH+", "MEDIUM+", PolicyLevels.Inherit }
            .Where(choice => model.LevelChangeFor("block", claude, choice).Weakened().Count > 0)
            .ToArray();
        Assert.Equal(new[] { "CRITICAL", "HIGH+", PolicyLevels.Inherit }, weaker);

        var codex = model.ScopeRow("codex")!;
        Assert.Equal(PolicyLevels.Inherit, model.LevelCurrent("block", codex));
        Assert.Contains("HIGH+", model.LevelInheritText("block", codex), StringComparison.Ordinal);
        Assert.Contains("CRITICAL", model.LevelInheritText("block", model.ScopeRow("global")!), StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_loosens_only_where_a_scope_ends_up_catching_fewer_severities()
    {
        var model = LevelsModel();
        var claude = model.ScopeRow("claudecode")!;
        var codex = model.ScopeRow("codex")!;
        var global = model.ScopeRow("global")!;

        bool Loosens(ScopePosture row, string kind, string choice) => model.LevelChangeFor(kind, row, choice).Weakened().Count > 0;

        Assert.True(Loosens(claude, "block", PolicyLevels.Inherit)); // MEDIUM+ -> the global HIGH+
        Assert.False(Loosens(codex, "block", "MEDIUM+"));
        Assert.True(Loosens(global, "block", "CRITICAL"));
        Assert.False(Loosens(global, "alert", "LOW+"));

        // An alert level above the block level only loosens up to the block level.
        Assert.True(Loosens(codex, "alert", "CRITICAL"));
        Assert.Equal("HIGH+", model.LevelChangeFor("alert", codex, "CRITICAL").EffectFor("codex")!.After.AlertAt);
    }

    [Fact]
    public void The_detail_says_where_the_levels_come_from()
    {
        var model = LevelsModel();

        Assert.Equal("set for codex", PolicyLevels.LevelOrigin("override", "codex", "strict", "strict"));
        Assert.Equal("set globally", PolicyLevels.LevelOrigin("global", "codex", "strict", "strict"));
        Assert.Equal("from the strict pack", PolicyLevels.LevelOrigin("pack", "codex", "strict", "strict"));
        Assert.Equal("from the protected-codex pack (strict levels)", PolicyLevels.LevelOrigin("pack", "codex", "protected-codex", "strict"));

        Assert.Equal("Block level set globally; alert level set for codex.", model.LevelsLine(model.ScopeRow("codex")!));
        Assert.Contains(model.LevelsLine(model.ScopeRow("codex")!), model.Detail("posture", "codex").Lines);
        Assert.Equal("Block level set for claudecode; alert level from the default pack.", model.LevelsLine(model.ScopeRow("claudecode")!));
    }

    [Fact]
    public void A_single_connector_install_changes_the_global_value()
    {
        var model = LevelsModel(multi: false, globalBlock: string.Empty);
        var codex = model.ScopeRow("codex")!;

        Assert.Equal(string.Empty, model.CommandConnector(codex));
        var change = model.LevelChangeFor("block", codex, "HIGH+");

        Assert.Equal(string.Empty, change.Connector);
        Assert.Equal("HIGH+", change.EffectFor("codex")!.After.BlockAt);

        var action = model.Actions("posture", "codex").First(a => a.Kind == PolicyActionKind.ToolBlockLevel && a.Title == "HIGH+");
        Assert.Equal(new[] { "guardrail", "block-at", "HIGH" }, action.Argv);
        Assert.Contains("This install has one connector, so this sets the global level.", action.Consequence.Details);

        // With connector blocks in the config the same change names the connector.
        var multi = LevelsModel();
        Assert.Equal(new[] { "guardrail", "block-at", "HIGH", "--connector", "claudecode" }, multi.Actions("posture", "claudecode").First(a => a.Kind == PolicyActionKind.ToolBlockLevel && a.Title == "HIGH+").Argv);
    }

    [Fact]
    public void A_pack_switch_says_when_set_levels_win_over_the_pack()
    {
        var multi = LevelsModel();
        var packs = new[] { new RulePackEntry("default", DefaultPack, "preset", Array.Empty<string>()), new RulePackEntry("strict", StrictPack, "preset", Array.Empty<string>()) };
        var model = new PolicyModel(multi.Catalog with { Packs = packs });

        string[] Held(string scope, string pack) =>
            model.Actions("packs", scope).First(a => a.Kind == PolicyActionKind.UsePack && a.Title == pack).Consequence.Details.Where(d => d.StartsWith("Tool calls still", StringComparison.Ordinal)).ToArray();

        // claudecode blocks MEDIUM itself, so the default pack's CRITICAL does not apply.
        Assert.Equal(new[] { "Tool calls still block at MEDIUM+ (not the pack's CRITICAL), as set with block-at / alert-at." }, Held("claudecode", "default"));

        // Strict blocks MEDIUM+ anyway: nothing to say.
        Assert.Empty(Held("claudecode", "strict"));
    }

    [Fact]
    public void Scope_names_resolve_for_the_scope_chooser()
    {
        var model = LevelsModel();

        Assert.Equal(new[] { "global", "codex", "claudecode" }, model.Scopes);
        Assert.Same(model.ScopeRow("global"), model.ScopeRow(string.Empty));
        Assert.Null(model.ScopeRow("nobody"));
        Assert.Same(model.ScopeRow("global"), model.ScopeRowOrGlobal("nobody"));
        Assert.Equal(new[] { "global" }, new PolicyModel(new PolicyCatalog()).Scopes);
        Assert.Equal(new[] { "codex" }, model.OwnSetting("pack"));
        Assert.Empty(model.OwnSetting("mode"));
    }

    [Fact]
    public void A_composed_pack_is_labelled_with_its_base_and_how_many_opt_in_packs_it_carries()
    {
        var catalog = PolicyModelFixtures.Phase2Catalog();
        var folder = @"C:\Users\operator\.defenseclaw\policies\guardrail\protected-codex\strict";
        var codex = new ScopePosture("codex", "action", "override", "off", "protected-codex", folder, "override", new[] { "kubernetes-production-protection" }, "MEDIUM+", "LOW+", "pack", string.Empty, string.Empty);
        var model = new PolicyModel(catalog with
        {
            Postures = catalog.Postures.Append(codex).ToArray(),
            ConnectorPacks = new[] { new ScopePack("codex", "protected-codex", folder, "override") },
            PackBases = new Dictionary<string, string>(StringComparer.Ordinal) { [folder] = "strict" },
            MultiConnector = true,
        });

        Assert.Equal("strict+1", model.ScopePackLabel(codex));
        Assert.Contains("Rule pack: protected-codex = strict + 1 opt-in pack; its folder name gives it strict levels (the gateway reads them from there).", model.Detail("posture", "codex").Lines);
        Assert.Equal("strict+1 (own)", model.Table("posture").Rows.Single(r => r.Key == "codex").Cells[5].Text);
        Assert.Equal("action (own)", model.Table("posture").Rows.Single(r => r.Key == "codex").Cells[1].Text);
        Assert.Equal("Opt-in: Kubernetes production protection", model.Detail("posture", "codex").Lines.Single(l => l.StartsWith("Opt-in:", StringComparison.Ordinal)));
        Assert.Equal("0 of 5", model.Table("posture").Rows[0].Cells[6].Text);
        Assert.Equal("1 of 5", model.Table("posture").Rows.Single(r => r.Key == "codex").Cells[6].Text);
    }

    [Fact]
    public void Rows_are_found_by_what_the_filter_reads()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        Assert.Contains(model.Table("optin").Rows, r => r.SearchText.Contains("kubectl", StringComparison.OrdinalIgnoreCase) || r.SearchText.Contains("Kubernetes", StringComparison.Ordinal));
        Assert.Contains(model.Table("chains").Rows, r => r.SearchText.Contains("xp_cmdshell", StringComparison.Ordinal));
        Assert.Contains(model.Table("policies").Rows, r => r.Key == "strict" && r.SearchText.Contains("Maximum security", StringComparison.Ordinal));
    }
}
