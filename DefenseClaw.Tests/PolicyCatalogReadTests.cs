using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Reading the pinned runtime's catalog (CUST-293): the JSON its read-only commands print, the scope postures composed from three of those
/// documents, the reader that runs them (each part on its own, the platform's refusal of sandboxes as a state and not a failure), and the
/// runtime's own data files. Synthetic fixtures derived from the Phase 2 capture; a fake runner answers, so nothing here starts a process.
/// </summary>
public sealed class PolicyCatalogReadTests
{
    // ---- the documents ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Policy_list_json_reads_every_field_of_the_runtimes_summary()
    {
        Assert.True(PolicyCatalogJson.TryParsePolicies(RuntimeFixtures.Read("cli/policy-list.json"), out var policies, out var error), error);

        Assert.Equal(new[] { "default", "permissive", "strict" }, policies.Select(p => p.Name));
        var @default = policies[0];
        Assert.True(@default.IsBuiltIn);
        Assert.True(@default.IsActive);
        Assert.Equal(("CRITICAL", "MEDIUM+", "HIGH+", "deny"), (@default.BlockAt, @default.AlertAt, @default.InstallBlockAt, @default.FirewallDefault));
        Assert.Equal(false, @default.Hilt);
        Assert.Null(policies[1].Hilt);
        Assert.Equal(4, @default.ScannerOverrides);
        Assert.False(@default.AddsWebhooks);
        Assert.False(@default.SetsCisco);
        Assert.False(@default.IsEdited);
        Assert.EndsWith("/policies/default.yaml", @default.SourcePath, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_packs_document_gives_the_global_pack_the_connectors_and_every_pack()
    {
        Assert.True(PolicyCatalogJson.TryParseListPacks(RuntimeFixtures.Read("cli/guardrail-list-packs.connectors.json"), out var document, out var error), error);

        Assert.Equal(new ScopePack("global", "default", @"C:\Users\operator\.defenseclaw\policies\guardrail\default", "default"), document.Global);
        Assert.Equal(new[] { "claudecode", "codex" }, document.Connectors.Select(c => c.Scope));
        Assert.Equal("override", document.Connectors[1].Source);
        Assert.Equal(new[] { "default", "strict", "permissive", "protected-codex" }, document.Packs.Select(p => p.Name));
        Assert.True(document.Packs[0].IsPreset);
        Assert.False(document.Packs[3].IsPreset);
        Assert.Equal(new[] { "global", "claudecode" }, document.Packs[0].UsedBy);
    }

    [Fact]
    public void The_protection_document_gives_the_packs_with_their_rules_and_what_each_scope_has_on()
    {
        Assert.True(PolicyCatalogJson.TryParseProtection(RuntimeFixtures.Read("cli/guardrail-protection-list.connectors.json"), out var document, out var error), error);

        Assert.Equal(6, document.Packs.Count);
        Assert.Equal(5, document.Packs.Count(p => !p.IsStaged));
        var privacy = document.Packs[0];
        Assert.Equal("privacy-high-assurance", privacy.Name);
        Assert.Equal(13, privacy.RuleCount);
        Assert.Equal(13, privacy.Rules.Count);
        Assert.Equal(new ProtectionRule("ENT-BULK-SSN", "CRITICAL", "US Social Security Number"), privacy.Rules[0]);
        Assert.True(document.Packs[^1].IsStaged);
        Assert.Equal(new[] { "kubernetes-production-protection" }, document.Scopes.Single(s => s.Scope == "codex").Enabled);
        Assert.Empty(document.Scopes.Single(s => s.Scope == "global").Enabled);
    }

    [Fact]
    public void The_guardrail_config_keeps_what_decides_a_posture_and_which_connectors_have_their_own()
    {
        Assert.True(PolicyCatalogJson.TryParseGuardrailSettings(RuntimeFixtures.Read("cli/config-show-guardrail.connectors.json"), out var settings, out var error), error);

        Assert.Equal(("action", true, "HIGH", "HIGH", string.Empty), (settings.Mode, settings.HiltEnabled, settings.HiltMinSeverity, settings.BlockAt, settings.AlertAt));
        Assert.Equal(2, settings.Connectors.Count);
        var claude = settings.ConnectorBlock("claudecode")!;
        Assert.Equal(("MEDIUM", false), (claude.BlockAt, claude.HasOwnHilt));
        var codex = settings.ConnectorBlock("codex")!;
        Assert.Equal(("observe", "LOW", true, false), (codex.Mode, codex.AlertAt, codex.HasOwnHilt, codex.HiltEnabled));

        // config.yaml can spell a connector another way; the lookup finds the same one the runtime would.
        Assert.Same(claude, settings.ConnectorBlock("claudecode"));
        var spelled = settings with { Connectors = new Dictionary<string, ConnectorGuardrailSettings>(StringComparer.Ordinal) { ["Claude-Code"] = claude } };
        Assert.Same(claude, spelled.ConnectorBlock("claudecode"));
        Assert.Null(spelled.ConnectorBlock("codex"));
        Assert.Equal("claudecode", GuardrailSettings.NormalizeConnector("claude_code"));
        Assert.Equal("openhands", GuardrailSettings.NormalizeConnector("Open-Hands"));
    }

    [Fact]
    public void The_fresh_guardrail_config_has_no_connector_and_the_defaults()
    {
        Assert.True(PolicyCatalogJson.TryParseGuardrailSettings(RuntimeFixtures.Read("cli/config-show-guardrail.json"), out var settings, out var error), error);

        Assert.Equal(("observe", false, "HIGH", string.Empty, string.Empty), (settings.Mode, settings.HiltEnabled, settings.HiltMinSeverity, settings.BlockAt, settings.AlertAt));
        Assert.Empty(settings.Connectors);
    }

    [Fact]
    public void Pack_validation_decodes_all_three_outcomes_as_the_runtime_does()
    {
        var valid = PolicyCatalogJson.ParseValidation(
            0,
            """{"valid": true, "summary": {"rule_count": 40, "enabled_rule_count": 38, "rule_file_count": 5, "digest": "bbbbbbbbbbbbbbbbbbbb"}}""");
        Assert.True(valid.IsValid);
        Assert.Equal((38, 40, 5), (valid.EnabledRuleCount, valid.RuleCount, valid.RuleFileCount));
        Assert.Equal("valid: 38/40 rules enabled across 5 files \u00B7 digest bbbbbbbbbbbb", valid.Summary);

        var invalid = PolicyCatalogJson.ParseValidation(1, """{"valid": false, "error": {"path": "rules/x.yaml", "code": "bad_pattern", "reason": "bad regex"}}""");
        Assert.False(invalid.IsValid);
        Assert.Equal("invalid", invalid.State);
        Assert.Equal("bad regex (at rules/x.yaml)", invalid.Message);

        var unavailable = PolicyCatalogJson.ParseValidation(2, string.Empty);
        Assert.Equal("unavailable", unavailable.State);
        Assert.StartsWith("validator unavailable:", unavailable.Summary, StringComparison.Ordinal);

        // Exit 0 without "valid": true is not a valid pack; text that is not JSON is not either.
        Assert.False(PolicyCatalogJson.ParseValidation(0, "not json").IsValid);
        Assert.False(PolicyCatalogJson.ParseValidation(0, """{"valid": "yes"}""").IsValid);
        Assert.False(PolicyCatalogJson.ParseValidation(1, """{"valid": true}""").IsValid);
        Assert.Equal("exit 1", PolicyCatalogJson.ParseValidation(1, "{}").Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("[1, 2]")]
    public void Output_that_is_not_the_document_is_an_error_never_an_empty_catalog(string text)
    {
        Assert.False(PolicyCatalogJson.TryParsePolicies(text, out var policies, out var error));
        Assert.Empty(policies);
        Assert.NotEmpty(error);
        Assert.False(PolicyCatalogJson.TryParseListPacks(text, out _, out _));
        Assert.False(PolicyCatalogJson.TryParseProtection(text, out _, out _));
        Assert.False(PolicyCatalogJson.TryParseGuardrailSettings(text, out _, out _));
    }

    [Fact]
    public void A_banner_line_before_the_document_is_skipped_and_a_document_of_the_wrong_shape_is_named()
    {
        Assert.True(PolicyCatalogJson.TryParsePolicies("note: something\n" + RuntimeFixtures.Read("cli/policy-list.json"), out var policies, out _));
        Assert.Equal(3, policies.Count);

        Assert.False(PolicyCatalogJson.TryParsePolicies("""{"version": 1}""", out _, out var error));
        Assert.Equal("policy list --json printed no policies list.", error);
        Assert.False(PolicyCatalogJson.TryParseListPacks("""{"packs": []}""", out _, out error));
        Assert.Equal("guardrail list-packs --json printed no global pack.", error);
        Assert.False(PolicyCatalogJson.TryParseProtection("""{"scopes": []}""", out _, out error));
        Assert.Equal("guardrail protection list --json printed no packs list.", error);
        Assert.False(PolicyCatalogJson.TryParseGuardrailSettings("""{"llm": {}}""", out _, out error));
        Assert.Equal("config show --section guardrail printed no guardrail section.", error);

        // A row without a name is dropped, not turned into a blank policy.
        Assert.True(PolicyCatalogJson.TryParsePolicies("""{"policies": [{"description": "nameless"}, {"name": "ok"}]}""", out policies, out _));
        Assert.Equal(new[] { "ok" }, policies.Select(p => p.Name));
    }

    [Fact]
    public void The_chain_catalog_and_a_composed_packs_manifest_read_as_the_runtime_reads_them()
    {
        var chains = PolicyCatalogJson.ParseToolChains(RuntimeFixtures.Read("policy-model/tool-chains.json"));
        Assert.Equal(26, chains.Count);
        Assert.Equal(4, chains.Count(c => c.CanBlock));
        var first = chains[0];
        Assert.Equal(("chain.guardrails_off_then_egress", "Guardrails disabled before external egress", "HIGH", "security-controls", false), (first.Id, first.Title, first.Severity, first.Domain, first.CanBlock));
        Assert.Equal((8, 1800, "Alert-only"), (first.EventWindow, first.TimeWindowSeconds, first.Note));
        Assert.Equal(new[] { "same session" }, first.Requires);

        Assert.Empty(PolicyCatalogJson.ParseToolChains("""{"version": 2, "chains": []}"""));
        Assert.Empty(PolicyCatalogJson.ParseToolChains("""{"version": 1}"""));
        Assert.Empty(PolicyCatalogJson.ParseToolChains("nope"));

        Assert.Equal("strict", PolicyCatalogJson.ParsePackBase("""{"version": 1, "base": "C:\\p\\strict", "base_name": "strict", "protection": ["a"]}"""));
        Assert.Equal("strict", PolicyCatalogJson.ParsePackBase("""{"version": 1, "base": "C:\\p\\strict", "protection": []}"""));
        Assert.Null(PolicyCatalogJson.ParsePackBase("""{"version": 2, "base": "x", "protection": []}"""));
        Assert.Null(PolicyCatalogJson.ParsePackBase("""{"version": 1, "protection": []}"""));
    }

    // ---- the postures ----------------------------------------------------------------------------------------------------

    private static (ListPacksDocument Packs, ProtectionDocument Protection, GuardrailSettings Settings) Documents(string suffix)
    {
        Assert.True(PolicyCatalogJson.TryParseListPacks(RuntimeFixtures.Read($"cli/guardrail-list-packs{suffix}.json"), out var packs, out var error), error);
        Assert.True(PolicyCatalogJson.TryParseProtection(RuntimeFixtures.Read($"cli/guardrail-protection-list{suffix}.json"), out var protection, out error), error);
        Assert.True(PolicyCatalogJson.TryParseGuardrailSettings(RuntimeFixtures.Read($"cli/config-show-guardrail{suffix}.json"), out var settings, out error), error);
        return (packs, protection, settings);
    }

    [Fact]
    public void The_fresh_install_composes_the_posture_the_runtime_reported()
    {
        var (packs, protection, settings) = Documents(string.Empty);

        var postures = PolicyPostureComposer.Compose(packs, protection, settings, out var problems);

        Assert.Empty(problems);
        using var phase2 = PolicyModelFixtures.Phase2();
        var expected = phase2.RootElement.GetProperty("raw_catalog").GetProperty("postures")[0];
        var posture = Assert.Single(postures);
        Assert.Equal(expected.GetProperty("scope").GetString(), posture.Scope);
        Assert.Equal(expected.GetProperty("mode").GetString(), posture.Mode);
        Assert.Equal(expected.GetProperty("mode_source").GetString(), posture.ModeSource);
        Assert.Equal(expected.GetProperty("hilt").GetString(), posture.Hilt);
        Assert.Equal(expected.GetProperty("pack").GetString(), posture.Pack);
        Assert.Equal(expected.GetProperty("pack_path").GetString(), posture.PackFolder);
        Assert.Equal(expected.GetProperty("pack_source").GetString(), posture.PackSource);
        Assert.Equal(expected.GetProperty("block_at").GetString(), posture.BlockAt);
        Assert.Equal(expected.GetProperty("alert_at").GetString(), posture.AlertAt);
        Assert.Equal(expected.GetProperty("levels_source").GetString(), posture.LevelsSource);
        Assert.Equal(expected.GetProperty("own_block_at").GetString(), posture.OwnBlockAt);
        Assert.Equal(expected.GetProperty("own_alert_at").GetString(), posture.OwnAlertAt);
        Assert.Empty(posture.Protection);
    }

    [Fact]
    public void Two_connectors_compose_with_their_own_values_and_the_inherited_ones()
    {
        var (packs, protection, settings) = Documents(".connectors");

        var postures = PolicyPostureComposer.Compose(packs, protection, settings, out var problems).ToDictionary(p => p.Scope);

        Assert.Empty(problems);
        Assert.Equal(new[] { "global", "claudecode", "codex" }, postures.Keys);

        // global: action, approval on from HIGH, blocks HIGH (set), alerts from the default pack
        var global = postures["global"];
        Assert.Equal(("action", "HIGH+", "HIGH+", "MEDIUM+", "global", "HIGH", string.Empty), (global.Mode, global.Hilt, global.BlockAt, global.AlertAt, global.LevelsSource, global.OwnBlockAt, global.OwnAlertAt));

        // claudecode: follows the global mode and approval, blocks MEDIUM itself
        var claude = postures["claudecode"];
        Assert.Equal(("action", "global", "HIGH+", "MEDIUM+", "MEDIUM+", "override", "MEDIUM"), (claude.Mode, claude.ModeSource, claude.Hilt, claude.BlockAt, claude.AlertAt, claude.LevelsSource, claude.OwnBlockAt));
        Assert.Equal("default", claude.Pack);
        Assert.Equal("default", claude.PackSource);

        // codex: its own mode, its own approval block (off, which replaces the global one), its own composed pack and an own alert level
        var codex = postures["codex"];
        Assert.Equal(("observe", "override", "off", "protected-codex", "override"), (codex.Mode, codex.ModeSource, codex.Hilt, codex.Pack, codex.PackSource));
        Assert.Equal(("HIGH+", "LOW+", "override", string.Empty, "LOW"), (codex.BlockAt, codex.AlertAt, codex.LevelsSource, codex.OwnBlockAt, codex.OwnAlertAt));
        Assert.Equal(new[] { "kubernetes-production-protection" }, codex.Protection);
    }

    [Fact]
    public void The_scenario_of_the_runtimes_own_test_composes_to_its_expected_table()
    {
        // test_policy_catalog_levels.py::test_scope_postures_carry_the_resolved_levels_and_where_they_come_from
        var settings = new GuardrailSettings(
            "observe",
            false,
            "HIGH",
            "HIGH",
            string.Empty,
            new Dictionary<string, ConnectorGuardrailSettings>(StringComparer.Ordinal)
            {
                ["codex"] = new(string.Empty, null, null, string.Empty, "CRITICAL"),
                ["claudecode"] = new(string.Empty, null, null, "MEDIUM", string.Empty),
                ["hermes"] = new(string.Empty, null, null, string.Empty, string.Empty),
            });
        var packs = new ListPacksDocument(
            new ScopePack("global", "default", "/p/guardrail/default", "global"),
            new[]
            {
                new ScopePack("codex", "strict", "/p/guardrail/strict", "override"),
                new ScopePack("claudecode", "default", "/p/guardrail/default", "global"),
                new ScopePack("hermes", "strict", "/p/guardrail/strict", "override"),
            },
            Array.Empty<RulePackEntry>());
        var protection = new ProtectionDocument(
            Array.Empty<ProtectionPack>(),
            packs.Connectors.Select(c => new ProtectionScope(c.Scope, c.Pack, c.Folder, Array.Empty<string>())).Append(new ProtectionScope("global", "default", "/p/guardrail/default", Array.Empty<string>())).ToArray());

        var got = PolicyPostureComposer.Compose(packs, protection, settings, out _)
            .ToDictionary(p => p.Scope, p => (p.BlockAt, p.AlertAt, p.LevelsSource, p.OwnBlockAt, p.OwnAlertAt));

        Assert.Equal(
            new Dictionary<string, (string, string, string, string, string)>
            {
                ["global"] = ("HIGH+", "MEDIUM+", "global", "HIGH", string.Empty),

                // alert CRITICAL is clamped to the global block level HIGH
                ["codex"] = ("HIGH+", "HIGH+", "override", string.Empty, "CRITICAL"),
                ["claudecode"] = ("MEDIUM+", "MEDIUM+", "override", "MEDIUM", string.Empty),

                // a global value replaces the strict pack's MEDIUM+
                ["hermes"] = ("HIGH+", "LOW+", "global", string.Empty, string.Empty),
            },
            got);
    }

    [Fact]
    public void A_scope_the_protection_list_does_not_name_is_reported_not_guessed()
    {
        var (packs, protection, settings) = Documents(".connectors");
        var short_ = new ProtectionDocument(protection.Packs, protection.Scopes.Where(s => s.Scope != "codex").ToArray());

        var postures = PolicyPostureComposer.Compose(packs, short_, settings, out var problems);

        Assert.Equal("The opt-in pack list has no row for codex.", Assert.Single(problems));
        Assert.Empty(postures.Single(p => p.Scope == "codex").Protection);
    }

    // ---- the reader ------------------------------------------------------------------------------------------------------

    private sealed class FakeData(IReadOnlyList<ToolChain>? chains = null, string? baseName = null) : IPolicyDataFiles
    {
        public List<string> FamilyReads { get; } = new();

        public IReadOnlyList<ToolChain> ReadToolChains(IReadOnlyList<NamedPolicy> policies) => chains ?? Array.Empty<ToolChain>();

        public IReadOnlyList<RuleFamily> ReadRuleFamilies(string packFolder, IReadOnlyList<NamedPolicy> policies)
        {
            FamilyReads.Add(packFolder);
            return new[] { new RuleFamily("command", 3, 3, "Execution") };
        }

        public string? ReadPackBase(string packFolder) => packFolder.EndsWith(@"protected-codex\strict", StringComparison.Ordinal) ? baseName : null;
    }

    [Fact]
    public async Task A_complete_read_builds_every_part_and_asks_only_the_catalog_reads()
    {
        var asked = new List<string>();
        var data = new FakeData(PolicyCatalogJson.ParseToolChains(RuntimeFixtures.Read("policy-model/tool-chains.json")), "strict");
        var reader = new PolicyModelReader(PolicyModelFixtures.Runner(PolicyModelFixtures.Connectors, asked), data);

        var read = await reader.ReadAsync();

        Assert.True(read.IsComplete);
        Assert.True(read.ReadAnything);
        Assert.All(asked, a => Assert.True(PolicyActionGuard.IsAllowedRead(a.Split(' '))));
        Assert.Equal(PolicyActionGuard.CatalogReads.Select(r => string.Join(' ', r)).Order(StringComparer.Ordinal), asked.Order(StringComparer.Ordinal));

        var catalog = read.Catalog;
        Assert.Equal(3, catalog.Policies.Count);
        Assert.Equal("default", catalog.GlobalPack!.Pack);
        Assert.Equal(new[] { "claudecode", "codex" }, catalog.ConnectorPacks.Select(c => c.Scope));
        Assert.Equal(new[] { "global", "claudecode", "codex" }, catalog.Postures.Select(p => p.Scope));
        Assert.Equal(6, catalog.Protection.Count);
        Assert.True(catalog.MultiConnector);
        Assert.Equal(@"C:\Users\operator\.defenseclaw\policies", catalog.PolicyFolder);
        Assert.Equal(26, catalog.Chains.Count);
        Assert.Equal("strict", catalog.PackBases[@"C:\Users\operator\.defenseclaw\policies\guardrail\protected-codex\strict"]);

        // one family read per distinct pack folder: default (global + claudecode) and the composed one
        Assert.Equal(2, data.FamilyReads.Count);
        Assert.Equal(2, catalog.Families.Count);
        Assert.Equal(string.Empty, catalog.PoliciesError + catalog.PackError + catalog.PostureError);
    }

    [Fact]
    public async Task The_reader_never_asks_about_sandboxes_whatever_the_runtime_would_answer()
    {
        var asked = new List<string>();
        var reader = new PolicyModelReader(PolicyModelFixtures.Runner(PolicyModelFixtures.Fresh, asked), new FakeData());

        var read = await reader.ReadAsync();

        Assert.True(read.IsComplete);
        Assert.Equal(4, asked.Count);
        Assert.DoesNotContain(asked, a => a.Contains("sandbox", StringComparison.OrdinalIgnoreCase) || a.Contains("openshell", StringComparison.OrdinalIgnoreCase));

        // and the guard would not let it
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "sandbox", "pack", "list", "-o", "json" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "config", "show", "--section", "openshell", "--format", "json" }));
        Assert.Equal(4, PolicyActionGuard.CatalogReads.Count);
    }

    [Fact]
    public async Task A_part_that_fails_keeps_the_rows_it_had_names_the_problem_and_makes_the_read_partial()
    {
        var good = (await new PolicyModelReader(PolicyModelFixtures.Runner(PolicyModelFixtures.Fresh), new FakeData()).ReadAsync()).Catalog;
        var overrides = new Dictionary<string, Func<CliInvocation>>
        {
            ["policy list --json"] = () => PolicyModelFixtures.Invocation(1, string.Empty, "Error: policy directory is unreadable"),
            ["guardrail protection list --json"] = () => PolicyModelFixtures.Invocation(0, "not json at all"),
        };
        var reader = new PolicyModelReader(PolicyModelFixtures.Runner(PolicyModelFixtures.Fresh, overrides: overrides), new FakeData());

        var read = await reader.ReadAsync(good);

        Assert.False(read.IsComplete);
        Assert.Equal(2, read.Problems.Count);
        Assert.Contains("'defenseclaw policy list --json' failed: Error: policy directory is unreadable", read.Problems);
        var catalog = read.Catalog;

        // the policies of the last good read are still there, marked as unreadable now
        Assert.Equal(3, catalog.Policies.Count);
        Assert.Equal("'defenseclaw policy list --json' failed: Error: policy directory is unreadable", catalog.PoliciesError);

        // the rule packs were read now
        Assert.Equal(string.Empty, catalog.PackError);
        Assert.Equal("guardrail protection list --json printed nothing this app can read.", catalog.PostureError);
        Assert.Equal(good.Postures, catalog.Postures);
    }

    [Fact]
    public async Task A_part_that_never_completed_says_why_and_a_truncated_one_is_not_read()
    {
        var overrides = new Dictionary<string, Func<CliInvocation>>
        {
            ["guardrail list-packs --json"] = () => PolicyModelFixtures.Invocation(null, string.Empty, string.Empty, "timed out after 120 s"),
        };
        var reader = new PolicyModelReader(PolicyModelFixtures.Runner(PolicyModelFixtures.Fresh, overrides: overrides), new FakeData());

        var read = await reader.ReadAsync();

        Assert.Equal("'defenseclaw guardrail list-packs --json' did not complete: timed out after 120 s", read.Catalog.PackError);
        Assert.Null(read.Catalog.GlobalPack);

        // Without the pack list the scopes cannot be built, and the posture part says so.
        Assert.Equal("The scopes cannot be built without the rule pack list.", read.Catalog.PostureError);
        Assert.Empty(read.Catalog.Postures);
        Assert.False(read.IsComplete);
    }

    [Fact]
    public async Task Nothing_read_is_reported_as_nothing_read()
    {
        Task<CliInvocation> Fail(IReadOnlyList<string> argv, CancellationToken token) =>
            Task.FromResult(PolicyModelFixtures.Invocation(1, string.Empty, "boom"));
        var reader = new PolicyModelReader(Fail, new FakeData());

        var read = await reader.ReadAsync();

        Assert.False(read.ReadAnything);
        Assert.False(read.IsComplete);
        Assert.True(read.Catalog.IsEmpty);
    }

    [Fact]
    public async Task A_missing_cli_surfaces_to_the_caller()
    {
        Task<CliInvocation> Missing(IReadOnlyList<string> argv, CancellationToken token) => throw new CliNotFoundException("defenseclaw", Array.Empty<string>());
        var reader = new PolicyModelReader(Missing, new FakeData());

        await Assert.ThrowsAsync<CliNotFoundException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task The_reader_never_runs_more_than_three_reads_at_once()
    {
        var running = 0;
        var peak = 0;
        var gate = new object();
        async Task<CliInvocation> Slow(IReadOnlyList<string> argv, CancellationToken token)
        {
            lock (gate)
            {
                running++;
                peak = Math.Max(peak, running);
            }

            await Task.Delay(30, token).ConfigureAwait(false);
            lock (gate)
            {
                running--;
            }

            return await PolicyModelFixtures.Runner(PolicyModelFixtures.Fresh)(argv, token).ConfigureAwait(false);
        }

        _ = await new PolicyModelReader(Slow, new FakeData()).ReadAsync();

        Assert.InRange(peak, 1, PolicyModelReader.MaxParallelReads);
    }

    // ---- the runtime's own data files ----------------------------------------------------------------------------------------

    [Fact]
    public void The_data_folder_is_found_from_a_built_in_policys_path_and_only_from_that()
    {
        var policies = new[]
        {
            new NamedPolicy("mine", string.Empty, false, false, @"C:\Users\operator\.defenseclaw\policies\mine.yaml", "CRITICAL", "MEDIUM+", "none", "deny", null, 0, false, false, false),
            new NamedPolicy("default", string.Empty, true, true, @"C:\Users\operator\.defenseclaw\policies\default.yaml", "CRITICAL", "MEDIUM+", "none", "deny", null, 0, false, false, true),
            new NamedPolicy("strict", string.Empty, true, false, @"C:\Users\operator\AppData\Local\Programs\DefenseClaw\runtime\defenseclaw\_data/policies/strict.yaml", "CRITICAL", "MEDIUM+", "none", "deny", null, 0, false, false, false),
        };

        // a custom policy and an edited built-in say nothing about the runtime's folder; the unedited built-in does
        Assert.Equal(@"C:\Users\operator\AppData\Local\Programs\DefenseClaw\runtime\defenseclaw\_data", FileSystemPolicyData.DataFolderFrom(policies));
        Assert.Null(FileSystemPolicyData.DataFolderFrom(policies.Take(2).ToArray()));
        Assert.Null(FileSystemPolicyData.DataFolderFrom(Array.Empty<NamedPolicy>()));
        Assert.Null(FileSystemPolicyData.DataFolderFrom(new[] { policies[2] with { SourcePath = "relative/policies/strict.yaml" } }));
    }

    private static string WriteRules(string folder, string file, string category, int rules, int disabled = 0)
    {
        var rulesFolder = Path.Combine(folder, "rules");
        _ = Directory.CreateDirectory(rulesFolder);
        var lines = new List<string> { "version: 1", $"category: {category}", "rules:" };
        for (var i = 0; i < rules; i++)
        {
            lines.Add($"  - id: R-{category}-{i}");
            lines.Add("    pattern: '(?i)x[0-9]+'");
            lines.Add($"    title: \"Rule {i}\"");
            lines.Add("    severity: HIGH");
            if (i < disabled)
            {
                lines.Add("    enabled: false");
            }
        }

        var path = Path.Combine(rulesFolder, file);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        return path;
    }

    [Fact]
    public void Rule_families_are_the_default_packs_replaced_by_the_packs_own_files_in_the_runtimes_order()
    {
        using var temp = new TempDirectory();
        var bundled = Path.Combine(temp.Path, "default");
        WriteRules(bundled, "commands.yaml", "command", 5);
        WriteRules(bundled, "secrets.yaml", "secret", 3);
        WriteRules(bundled, "c2.yaml", "c2", 2);
        WriteRules(bundled, "local-patterns.yaml", "ignored", 9);

        // The pack changes one family (two of four rules off) and adds one the runtime does not name.
        var custom = Path.Combine(temp.Path, "custom");
        WriteRules(custom, "commands.yaml", "command", 4, disabled: 2);
        WriteRules(custom, "extra.yaml", "impact", 2);
        WriteRules(custom, "off.yaml", "secret", 3, disabled: 3);

        var families = FileSystemPolicyData.Families(custom, bundled);

        Assert.Equal(new[] { "command", "secret", "c2", "impact" }, families.Select(f => f.Name));
        Assert.Equal((4, 2), (families[0].Rules, families[0].Enabled));
        Assert.StartsWith("Execution, reverse shells", families[0].Description, StringComparison.Ordinal);

        // a family whose rules are all off in the pack does not replace the default's
        Assert.Equal((3, 3), (families[1].Rules, families[1].Enabled));
        Assert.Equal(string.Empty, families[3].Description);

        // the default pack itself is the default families, and a pack with no rule files adds nothing
        Assert.Equal(new[] { "command", "secret", "c2" }, FileSystemPolicyData.Families(bundled, bundled).Select(f => f.Name));
        Assert.Equal(3, FileSystemPolicyData.Families(Path.Combine(temp.Path, "empty"), bundled).Count);
        Assert.Empty(FileSystemPolicyData.Families(Path.Combine(temp.Path, "empty"), null));
    }

    [Fact]
    public void The_phase_2_counts_come_out_of_files_shaped_like_the_runtimes()
    {
        using var temp = new TempDirectory();
        var pack = Path.Combine(temp.Path, "default");
        using var phase2 = PolicyModelFixtures.Phase2();
        var expected = phase2.RootElement.GetProperty("raw_catalog").GetProperty("families").EnumerateObject().Single().Value.EnumerateArray()
            .Select(f => (Name: f.GetProperty("name").GetString()!, Rules: f.GetProperty("rules").GetInt32(), Enabled: f.GetProperty("enabled").GetInt32()))
            .ToArray();
        foreach (var (name, rules, enabled) in expected)
        {
            WriteRules(pack, name + ".yaml", name, rules, disabled: rules - enabled);
        }

        var families = FileSystemPolicyData.Families(pack, null);

        Assert.Equal(expected, families.Select(f => (f.Name, f.Rules, f.Enabled)).ToArray());
    }

    [Fact]
    public void Files_that_are_missing_oversized_or_not_yaml_are_empty_answers_not_errors()
    {
        using var temp = new TempDirectory();
        var data = new FileSystemPolicyData();
        var policies = new[]
        {
            new NamedPolicy("default", string.Empty, true, true, Path.Combine(temp.Path, "_data", "policies", "default.yaml"), "CRITICAL", "MEDIUM+", "none", "deny", null, 0, false, false, false),
        };

        Assert.Empty(data.ReadToolChains(policies));
        Assert.Null(data.ReadPackBase(Path.Combine(temp.Path, "nope")));
        Assert.Null(data.ReadPackBase(string.Empty));

        var guardrail = Path.Combine(temp.Path, "_data", "policies", "guardrail");
        _ = Directory.CreateDirectory(guardrail);
        File.WriteAllText(Path.Combine(guardrail, "tool-chains.json"), RuntimeFixtures.Read("policy-model/tool-chains.json"));
        Assert.Equal(26, data.ReadToolChains(policies).Count);

        File.WriteAllText(Path.Combine(guardrail, "tool-chains.json"), new string('x', (int)FileSystemPolicyData.MaxFileBytes + 1));
        Assert.Empty(data.ReadToolChains(policies));

        var pack = Path.Combine(temp.Path, "pack");
        _ = Directory.CreateDirectory(Path.Combine(pack, "rules"));
        File.WriteAllText(Path.Combine(pack, "rules", "broken.yaml"), "category: [unclosed\nrules: {");
        File.WriteAllText(Path.Combine(pack, "rules", "plain.yaml"), "just a string\n");
        Assert.Empty(FileSystemPolicyData.Families(pack, null));

        File.WriteAllText(Path.Combine(pack, "defenseclaw-pack.json"), """{"version": 1, "base": "C:\\p\\strict", "protection": ["a"]}""");
        Assert.Equal("strict", data.ReadPackBase(pack));
    }

    // ---- the backend choice from the probe -----------------------------------------------------------------------------

    private static RuntimeCapabilities Probe(string set)
    {
        string? Screen(string file) => File.Exists(Path.Combine(FixtureFiles.Directory, "runtime-" + set, file)) ? File.ReadAllText(Path.Combine(FixtureFiles.Directory, "runtime-" + set, file)) : null;
        var screens = new RuntimeProbeScreens(
            Screen("version.json"),
            Screen("root.txt"),
            Screen("setup.txt"),
            Screen("guardrail.txt"),
            Screen("config.txt"),
            Screen("sandbox.txt"),
            Screen("acp.txt"),
            Screen("setup-redaction.txt"));
        return RuntimeProbe.Evaluate(screens, "fixture", "fingerprint", DateTimeOffset.UnixEpoch).Capabilities;
    }

    [Fact]
    public void The_backend_follows_the_probe_and_an_unknown_runtime_keeps_the_0810_surface()
    {
        var newer = Probe("95159fd");
        var older = Probe("0.8.10");

        Assert.True(newer.Has(RuntimeCapability.PolicyModel));
        Assert.False(older.Has(RuntimeCapability.PolicyModel));

        Assert.Same(SevenViewPolicyBackend.Instance, PolicyBackends.For(newer));
        Assert.Same(Release0810PolicyBackend.Instance, PolicyBackends.For(older));
        Assert.Same(Release0810PolicyBackend.Instance, PolicyBackends.For(RuntimeCapabilities.Unknown));
        Assert.Same(Release0810PolicyBackend.Instance, PolicyBackends.For(null));
        Assert.True(PolicyRuntimeCapabilities.From(newer).HasSevenViewPanel);
        Assert.False(PolicyRuntimeCapabilities.From(null).HasSevenViewPanel);
    }

    [Fact]
    public void The_seven_view_backend_lists_policies_as_json_and_builds_only_the_commands_its_panel_has()
    {
        var backend = SevenViewPolicyBackend.Instance;

        Assert.Equal(new[] { "policy", "list", "--json" }, backend.ListArgv);
        Assert.Equal(new[] { "policy", "show", "strict", "--json" }, backend.ShowArgv("strict"));
        Assert.Equal(new[] { "policy", "activate", "strict" }, backend.ActivateArgv("strict"));
        Assert.Throws<ArgumentException>(() => backend.ShowArgv("--help"));
        Assert.Throws<NotSupportedException>(() => backend.DeleteArgv("x", false));
        Assert.Throws<NotSupportedException>(() => backend.CreateArgv(new DefenseClaw.Core.Policy.PolicyCreate { Name = "x" }));
        Assert.False(backend.Capabilities.CanCreate || backend.Capabilities.CanEdit || backend.Capabilities.CanDelete);

        Assert.True(backend.TryParseList(RuntimeFixtures.Read("cli/policy-list.json"), out var listing, out _));
        Assert.Equal("default", listing.ActiveName);
        Assert.Equal(3, listing.Policies.Count);
        Assert.False(backend.TryParseDetail("{}", out var detail, out var error));
        Assert.Null(detail);
        Assert.NotEmpty(error);
    }
}
