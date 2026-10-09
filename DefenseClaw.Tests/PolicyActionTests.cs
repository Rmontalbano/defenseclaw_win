using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// What the seven-view Policies panel can change: the argv of each command, the guard that lets nothing else through, and for every action the
/// consequence it states and whether it protects less (which is what asks for an acknowledgement). The commands are those of the pinned runtime
/// (<c>policy_state.py</c> intents, source commit 95159fd); the set is the Mac app's list of mutations.
/// </summary>
public sealed class PolicyActionTests
{
    private static async Task<PolicyModel> ConnectorsModel()
    {
        var data = new NoDataFiles();
        var read = await new PolicyModelReader(PolicyModelFixtures.Runner(PolicyModelFixtures.Connectors), data).ReadAsync();
        Assert.True(read.IsComplete);

        // Folders the way a Windows runtime prints them, so validation can be asked for.
        return new PolicyModel(read.Catalog);
    }

    private sealed class NoDataFiles : IPolicyDataFiles
    {
        public IReadOnlyList<ToolChain> ReadToolChains(IReadOnlyList<NamedPolicy> policies) => Array.Empty<ToolChain>();

        public IReadOnlyList<RuleFamily> ReadRuleFamilies(string packFolder, IReadOnlyList<NamedPolicy> policies) => Array.Empty<RuleFamily>();

        public string? ReadPackBase(string packFolder) => null;
    }

    // ---- the commands ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_intent_builds_the_exact_argv_of_the_runtimes_own()
    {
        Assert.Equal(new[] { "policy", "activate", "strict" }, PolicyIntents.Activate("strict").Args);
        Assert.Equal(new[] { "guardrail", "use-pack", "strict" }, PolicyIntents.UsePack("strict").Args);
        Assert.Equal(new[] { "guardrail", "use-pack", @"C:\Users\operator\packs\mine", "--connector", "codex" }, PolicyIntents.UsePack(@"C:\Users\operator\packs\mine", "codex").Args);
        Assert.Equal(new[] { "guardrail", "mode", "action", "--connector", "codex" }, PolicyIntents.Mode("action", "codex").Args);
        Assert.Equal(new[] { "guardrail", "block-at", "HIGH", "--connector", "codex" }, PolicyIntents.Level("block", "HIGH+", "codex").Args);
        Assert.Equal(new[] { "guardrail", "alert-at", "inherit" }, PolicyIntents.Level("alert", PolicyLevels.Inherit).Args);
        Assert.Equal(new[] { "guardrail", "alert-at", "LOW" }, PolicyIntents.Level("alert", "LOW+").Args);
        Assert.Equal(new[] { "guardrail", "block-at", "CRITICAL" }, PolicyIntents.Level("block", "CRITICAL").Args);
        Assert.Equal(new[] { "policy", "edit", "guardrail", "--block-threshold", "3", "-p", "strict" }, PolicyIntents.Threshold("block", "HIGH+", "strict").Args);
        Assert.Equal(new[] { "policy", "edit", "guardrail", "--alert-threshold", "1" }, PolicyIntents.Threshold("alert", "LOW+").Args);
        Assert.Equal(new[] { "guardrail", "hilt", "on", "--min-severity", "HIGH", "--yes" }, PolicyIntents.Hilt("HIGH+").Args);
        Assert.Equal(new[] { "guardrail", "hilt", "off", "--connector", "codex", "--yes" }, PolicyIntents.Hilt("off", "codex").Args);
        Assert.Equal(new[] { "guardrail", "protection", "enable", "privacy-high-assurance" }, PolicyIntents.Protection("privacy-high-assurance", enable: true).Args);
        Assert.Equal(new[] { "guardrail", "protection", "disable", "privacy-high-assurance", "--connector", "codex" }, PolicyIntents.Protection("privacy-high-assurance", enable: false, "codex").Args);
        Assert.Equal(new[] { "guardrail", "validate-pack", @"C:\p\strict", "--json" }, PolicyIntents.ValidatePack(@"C:\p\strict"));
    }

    [Fact]
    public void An_intent_refuses_a_value_the_cli_would_misread()
    {
        Assert.Throws<ArgumentException>(() => PolicyIntents.Activate("--help"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Activate("../x"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Activate(new string('a', PolicyIntents.MaxNameLength + 1)));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Mode("enforce"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Mode("action", "--yes"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Mode("action", "co dex"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Level("block", "none"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Level("both", "HIGH+"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Threshold("block", "inherit"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Threshold("block", "HIGH+", "-p"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Hilt("sometimes"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.Protection("--all", enable: true));
        Assert.Throws<ArgumentException>(() => PolicyIntents.UsePack("-r"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.UsePack("relative\\path"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.UsePack("C:\\p\\x\ny"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.ValidatePack("strict"));
    }

    [Fact]
    public void The_guard_lets_through_the_seven_changes_and_the_reads_and_nothing_else()
    {
        var changes = new[]
        {
            new[] { "guardrail", "mode", "observe" },
            new[] { "guardrail", "mode", "action", "--connector", "codex" },
            new[] { "guardrail", "block-at", "HIGH" },
            new[] { "guardrail", "alert-at", "inherit", "--connector", "claudecode" },
            new[] { "guardrail", "hilt", "off", "--yes" },
            new[] { "guardrail", "hilt", "on", "--min-severity", "LOW", "--connector", "codex", "--yes" },
            new[] { "guardrail", "use-pack", "strict" },
            new[] { "guardrail", "use-pack", @"C:\p\mine", "--connector", "codex" },
            new[] { "guardrail", "protection", "enable", "cloud-production-protection" },
            new[] { "guardrail", "protection", "disable", "cloud-production-protection", "--connector", "codex" },
            new[] { "policy", "activate", "strict" },
            new[] { "policy", "edit", "guardrail", "--block-threshold", "3" },
            new[] { "policy", "edit", "guardrail", "--alert-threshold", "2", "-p", "team" },
        };
        Assert.All(changes, c => Assert.True(PolicyActionGuard.IsAllowedChange(c), string.Join(' ', c)));

        var refused = new[]
        {
            new[] { "policy", "delete", "strict" },
            new[] { "policy", "create", "x" },
            new[] { "policy", "edit", "actions", "--severity", "high" },
            new[] { "policy", "edit", "guardrail", "--block-threshold", "9" },
            new[] { "policy", "edit", "guardrail", "--cisco-trust-level", "none" },
            new[] { "policy", "edit", "guardrail", "--block-threshold", "3", "--policy-name", "x" },
            new[] { "policy", "activate", "strict", "--no-reload" },
            new[] { "guardrail", "disable" },
            new[] { "guardrail", "enable" },
            new[] { "guardrail", "judge", "add", "codex" },
            new[] { "guardrail", "fail-mode", "open" },
            new[] { "guardrail", "mode", "enforce" },
            new[] { "guardrail", "mode", "action", "--connector" },
            new[] { "guardrail", "mode", "action", "--restart" },
            new[] { "guardrail", "block-at", "high" },
            new[] { "guardrail", "block-at", "HIGH+" },
            new[] { "guardrail", "hilt", "on", "--yes" },
            new[] { "guardrail", "hilt", "off" },
            new[] { "guardrail", "hilt", "on", "--min-severity", "inherit", "--yes" },
            new[] { "guardrail", "protection", "reset", "x" },
            new[] { "guardrail", "protection", "list" },
            new[] { "guardrail", "use-pack", "--clear" },
            new[] { "guardrail", "use-pack", "strict", "--no-validate" },
            new[] { "setup", "reset", "--yes" },
            new[] { "guardrail", "mode", "action\0" },
            Array.Empty<string>(),
        };
        Assert.All(refused, c => Assert.False(PolicyActionGuard.IsAllowedChange(c), string.Join(' ', c)));

        // a validation is a read, never a change
        var validation = new[] { "guardrail", "validate-pack", @"C:\p\strict", "--json" };
        Assert.True(PolicyActionGuard.IsPackValidation(validation));
        Assert.True(PolicyActionGuard.IsAllowedRead(validation));
        Assert.False(PolicyActionGuard.IsAllowedChange(validation));
        Assert.False(PolicyActionGuard.IsPackValidation(new[] { "guardrail", "validate-pack", "strict", "--json" }));
        Assert.False(PolicyActionGuard.IsPackValidation(new[] { "guardrail", "validate-pack", @"C:\p\strict" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "guardrail", "mode", "observe" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "policy", "list" }));
        Assert.False(PolicyActionGuard.IsAllowedRead(new[] { "config", "show", "--section", "guardrail", "--format", "json", "--reveal" }));
        Assert.True(PolicyActionGuard.IsAllowedRead(new[] { "policy", "validate" }));
    }

    // ---- every action of the scenarios -----------------------------------------------------------------------------------

    [Fact]
    public async Task Every_action_the_model_offers_passes_the_guard_and_states_a_warning_exactly_when_it_protects_less()
    {
        var connectors = await ConnectorsModel();
        var models = new[] { new PolicyModel(PolicyModelFixtures.Phase2Catalog()), connectors };
        var count = 0;

        foreach (var model in models)
        {
            // The opt-in packs are turned on per scope; the other views list their rows once.
            var scopes = model.Postures.ToArray();
            foreach (var (view, perScope) in new[] { ("posture", false), ("policies", false), ("packs", false), ("optin", true) })
            {
                foreach (var scope in perScope ? scopes : new[] { scopes[0] })
                {
                    foreach (var row in model.Table(view, scope).Rows)
                    {
                        foreach (var action in model.Actions(view, row.Key, scope))
                        {
                            count++;
                            var shown = $"{view}/{row.Key}: {string.Join(' ', action.Argv)}";
                            Assert.True(action.IsRead ? PolicyActionGuard.IsPackValidation(action.Argv) : PolicyActionGuard.IsAllowedChange(action.Argv), shown);

                            // A warning is stated exactly when the action protects less: that is what asks for the acknowledgement.
                            Assert.True(action.Weakens == action.Consequence.Warning.Length > 0, shown);
                            Assert.False(action.IsRead && action.Weakens, shown);
                            Assert.NotEmpty(action.Consequence.Heading);
                            Assert.NotEmpty(action.Consequence.ConfirmLabel);
                        }
                    }
                }
            }
        }

        Assert.True(count > 80, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_fresh_installs_global_posture_offers_the_changes_with_the_current_choices_marked()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        var actions = model.Actions("posture", "global");

        Assert.Equal(
            new[] { "Mode", "Tool-call block level", "Tool-call alert level", "Human approval", "Rule pack", "Validate a rule pack" },
            actions.Select(a => a.Group).Distinct());
        string[] Current(string group) => actions.Where(a => a.Group == group && a.IsCurrent).Select(a => a.Title).ToArray();
        Assert.Equal(new[] { "Log only (observe)" }, Current("Mode"));
        Assert.Equal(new[] { "Use the pack's level (CRITICAL)" }, Current("Tool-call block level"));
        Assert.Equal(new[] { "Use the pack's level (MEDIUM+)" }, Current("Tool-call alert level"));
        Assert.Equal(new[] { "Off" }, Current("Human approval"));
        Assert.Equal(new[] { "default" }, Current("Rule pack"));

        // Nothing on a fresh observe-mode install protects less when it is tightened.
        Assert.DoesNotContain(actions.Where(a => a.Group == "Mode"), a => a.Weakens);

        // block: three levels and "inherit"; alert: four and "inherit"; approval: off and four levels
        Assert.Equal(new[] { 4, 5, 5 }, new[] { "Tool-call block level", "Tool-call alert level", "Human approval" }.Select(g => actions.Count(a => a.Group == g)));
    }

    [Fact]
    public void A_held_severity_is_named_when_approval_is_turned_off()
    {
        var catalog = PolicyModelFixtures.Phase2Catalog();
        // blocks CRITICAL, asks a person from HIGH+
        var model = new PolicyModel(catalog with { Postures = new[] { catalog.Postures[0] with { Hilt = "HIGH+", Mode = "action" } } });

        var off = model.Actions("posture", "global").Single(a => a.Kind == PolicyActionKind.Approval && a.Title == "Off");

        Assert.True(off.Weakens);
        Assert.Equal("This weakens protection: HIGH findings on global are no longer held for a person.", off.Consequence.Warning);
        Assert.Equal("Turn off human approval on the global default?", off.Consequence.Heading);
        Assert.Equal("HIGH+ → off", off.Consequence.Summary);
    }

    [Fact]
    public async Task Observe_mode_a_higher_level_and_no_approval_each_need_the_acknowledgement()
    {
        var model = await ConnectorsModel();

        // claudecode follows the global action mode: switching it to observe stops it blocking.
        var toObserve = model.Actions("posture", "claudecode").Single(a => a.Kind == PolicyActionKind.Mode && a.Argv.Contains("observe"));
        Assert.True(toObserve.Weakens);
        Assert.Equal(new[] { "guardrail", "mode", "observe", "--connector", "claudecode" }, toObserve.Argv);
        Assert.Equal("Switch claudecode to observe mode?", toObserve.Consequence.Heading);
        Assert.Equal("action \u2192 observe", toObserve.Consequence.Summary);
        Assert.Equal("This weakens protection: claudecode stops blocking; findings are only logged, and its hooks fail open while the gateway is down unless their fail mode is set to closed.", toObserve.Consequence.Warning);
        Assert.Contains("Only this connector changes; the others keep their mode. A running gateway restarts.", toObserve.Consequence.Details);

        // codex observes already; going to action is stricter
        var codexToAction = model.Actions("posture", "codex").Single(a => a.Kind == PolicyActionKind.Mode && a.Argv.Contains("action"));
        Assert.False(codexToAction.Weakens);

        // the global default is in action mode: its switch names the connectors that keep their own mode
        var global = model.Actions("posture", "global").Single(a => a.Kind == PolicyActionKind.Mode && a.Argv.Contains("observe"));
        Assert.True(global.Weakens);
        Assert.Equal(new[] { "guardrail", "mode", "observe" }, global.Argv);
        Assert.Contains("Connectors with their own mode keep it: codex.", global.Consequence.Details);

        // approval on from HIGH+ on the global default: off, and asking from CRITICAL only, protect less
        var approvals = model.Actions("posture", "global").Where(a => a.Kind == PolicyActionKind.Approval).ToDictionary(a => a.Title);
        Assert.True(approvals["Off"].Weakens);
        Assert.True(approvals["CRITICAL"].Weakens);
        Assert.False(approvals["MEDIUM+"].Weakens);
        Assert.True(approvals["HIGH+"].IsCurrent);
        Assert.Equal(new[] { "guardrail", "hilt", "off", "--yes" }, approvals["Off"].Argv);

        // the global default already blocks HIGH, so approval holds nothing back there: the warning says so plainly
        Assert.Equal("This weakens protection: global asks a person for fewer findings.", approvals["Off"].Consequence.Warning);

        // a higher tool-call block level on claudecode, which blocks MEDIUM+ itself
        var levels = model.Actions("posture", "claudecode").Where(a => a.Kind == PolicyActionKind.ToolBlockLevel).ToDictionary(a => a.Title);
        Assert.True(levels["CRITICAL"].Weakens);
        Assert.Equal("This weakens protection: blocks CRITICAL instead of MEDIUM+ for claudecode.", levels["CRITICAL"].Consequence.Warning);
        Assert.False(levels["MEDIUM+"].Weakens);
        Assert.True(levels["MEDIUM+"].IsCurrent);
        Assert.Equal(new[] { "guardrail", "block-at", "CRITICAL", "--connector", "claudecode" }, levels["CRITICAL"].Argv);
    }

    [Fact]
    public async Task Turning_an_opt_in_pack_off_protects_less_and_turning_one_on_does_not()
    {
        var model = await ConnectorsModel();
        var codex = model.ScopeRow("codex")!;
        var global = model.ScopeRow("global")!;

        var off = Assert.Single(model.Actions("optin", "kubernetes-production-protection", codex));
        Assert.True(off.Weakens);
        Assert.Equal("Turn off", off.Title);
        Assert.Equal(new[] { "guardrail", "protection", "disable", "kubernetes-production-protection", "--connector", "codex" }, off.Argv);
        Assert.Equal("Turn off Kubernetes production protection for codex?", off.Consequence.Heading);
        Assert.Equal("This weakens protection: Kubernetes production protection is turned off for codex.", off.Consequence.Warning);
        Assert.Contains("That is the last opt-in pack, so codex goes back to its base pack.", off.Consequence.Details);

        var on = Assert.Single(model.Actions("optin", "privacy-high-assurance", global));
        Assert.False(on.Weakens);
        Assert.Equal(new[] { "guardrail", "protection", "enable", "privacy-high-assurance" }, on.Argv);
        Assert.Equal("Turn on Privacy high assurance for the global pack?", on.Consequence.Heading);
        Assert.Equal("Turning it on tells DefenseClaw that every connector using the global pack works with high-assurance personal data.", on.Consequence.Summary);
        Assert.Contains("Not covered, they have their own pack; turn it on there too: codex.", on.Consequence.Details);
        Assert.Contains(on.Consequence.Details, d => d.StartsWith("Builds guardrail\\protected-global\\default in your policy folder from default", StringComparison.Ordinal));
        Assert.Equal(string.Empty, on.Consequence.Warning);

        // a staged pack has nothing to turn on
        Assert.Empty(model.Actions("optin", "ssh-authorized-keys-protection", global));
    }

    [Fact]
    public void A_policy_that_protects_less_than_the_active_one_needs_the_acknowledgement_and_a_stricter_one_does_not()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        var permissive = model.Actions("policies", "permissive").Single(a => a.Kind == PolicyActionKind.Activate);
        Assert.True(permissive.Weakens);
        Assert.Equal(new[] { "policy", "activate", "permissive" }, permissive.Argv);
        Assert.Equal("Activate the permissive policy?", permissive.Consequence.Heading);
        Assert.Equal("default \u2192 permissive", permissive.Consequence.Summary);
        Assert.Equal(
            "This weakens protection: alerts on HIGH+ instead of MEDIUM+; blocks installs of CRITICAL instead of HIGH+; the firewall allows by default instead of denying.",
            permissive.Consequence.Warning);
        Assert.Contains("block CRITICAL \u2192 CRITICAL \u00B7 alert MEDIUM+ \u2192 HIGH+ \u00B7 installs HIGH+ \u2192 CRITICAL", permissive.Consequence.Details);
        Assert.Contains("Its guardrail thresholds govern LLM traffic through the proxy; tool-call blocking is unchanged (see Posture).", permissive.Consequence.Details);

        var strict = model.Actions("policies", "strict").Single(a => a.Kind == PolicyActionKind.Activate);
        Assert.False(strict.Weakens);
        Assert.Contains("4 scanner overrides", string.Join(' ', strict.Consequence.Details), StringComparison.Ordinal);

        var active = model.Actions("policies", "default").Single(a => a.Kind == PolicyActionKind.Activate);
        Assert.True(active.IsCurrent);
    }

    [Fact]
    public void A_policys_llm_thresholds_are_edited_apart_from_the_tool_call_levels_and_weaken_only_when_active()
    {
        var catalog = PolicyModelFixtures.Phase2Catalog();
        // strict is the active policy: block MEDIUM+, alert LOW+
        var model = new PolicyModel(catalog with
        {
            Policies = catalog.Policies.Select(p => p with { IsActive = p.Name == "strict" }).ToArray(),
        });

        var blockActions = model.Actions("policies", "strict").Where(a => a.Kind == PolicyActionKind.LlmBlockLevel).ToArray();
        Assert.Equal(new[] { "CRITICAL", "HIGH+", "MEDIUM+" }, blockActions.Select(a => a.Title));
        var block = blockActions.ToDictionary(a => a.Title);
        Assert.True(block["CRITICAL"].Weakens);
        Assert.True(block["HIGH+"].Weakens);
        Assert.False(block["MEDIUM+"].Weakens);
        Assert.True(block["MEDIUM+"].IsCurrent);
        Assert.Equal(new[] { "policy", "edit", "guardrail", "--block-threshold", "4", "-p", "strict" }, block["CRITICAL"].Argv);
        Assert.Equal("This weakens protection: the policy blocks CRITICAL instead of MEDIUM+.", block["CRITICAL"].Consequence.Warning);
        Assert.Contains("LLM traffic through the guardrail proxy only; tool calls keep each scope's levels (Posture view).", block["CRITICAL"].Consequence.Details);
        Assert.Contains("The gateway reloads the policy.", block["CRITICAL"].Consequence.Details);
        Assert.Contains("The built-in strict policy is copied to your policy folder first.", block["CRITICAL"].Consequence.Details);

        // an inactive policy's thresholds are a draft until it is activated: nothing is enforced less
        var draft = model.Actions("policies", "permissive").Where(a => a.Kind == PolicyActionKind.LlmAlertLevel).ToDictionary(a => a.Title);
        Assert.All(draft.Values, a => Assert.False(a.Weakens));
        Assert.Contains("The permissive policy isn't active, so nothing changes until you activate it.", draft["LOW+"].Consequence.Details);
        Assert.Equal(new[] { "policy", "edit", "guardrail", "--alert-threshold", "1", "-p", "permissive" }, draft["LOW+"].Argv);
    }

    [Fact]
    public async Task A_rule_pack_switch_names_the_pack_what_it_replaces_and_validates_the_folder_first()
    {
        var model = await ConnectorsModel();
        var catalog = model.Catalog;

        var actions = model.Actions("packs", "codex");
        var strict = actions.Single(a => a.Kind == PolicyActionKind.UsePack && a.Title == "strict");

        Assert.Equal(new[] { "guardrail", "use-pack", "strict", "--connector", "codex" }, strict.Argv);
        Assert.Equal(@"C:\Users\operator\.defenseclaw\policies\guardrail\strict", strict.PackFolder);
        Assert.Equal("Use the strict rule pack for codex?", strict.Consequence.Heading);
        Assert.Equal("A running gateway restarts to load the new pack.", strict.Consequence.Summary);
        Assert.Contains("codex: protected-codex \u2192 strict", strict.Consequence.Details);
        Assert.Contains("Other connectors keep their pack.", strict.Consequence.Details);

        // a custom pack is switched to by its folder, and each pack can be validated on its own
        var custom = actions.Single(a => a.Kind == PolicyActionKind.UsePack && a.Title == "protected-codex");
        Assert.Equal(new[] { "guardrail", "use-pack", @"C:\Users\operator\.defenseclaw\policies\guardrail\protected-codex\strict", "--connector", "codex" }, custom.Argv);
        Assert.True(custom.IsCurrent);
        var validate = actions.Where(a => a.Kind == PolicyActionKind.ValidatePack).ToArray();
        Assert.Equal(catalog.Packs.Count, validate.Length);
        Assert.All(validate, a => Assert.True(a.IsRead));
        Assert.Equal(new[] { "guardrail", "validate-pack", @"C:\Users\operator\.defenseclaw\policies\guardrail\strict", "--json" }, validate.Single(a => a.Title == "strict").Argv);

        // the global switch lists the connectors whose own pack it clears; to a looser preset than a pack in use, it protects less
        var global = model.Actions("packs", "global").Single(a => a.Kind == PolicyActionKind.UsePack && a.Title == "permissive");
        Assert.True(global.Weakens);
        Assert.Contains("Clears the own pack of: codex", global.Consequence.Details);
        Assert.Equal("permissive is a looser preset than default, protected-codex.", global.Consequence.Warning);
        Assert.False(model.Actions("packs", "global").Single(a => a.Kind == PolicyActionKind.UsePack && a.Title == "strict").Weakens);
    }

    [Fact]
    public void A_view_with_nothing_to_change_has_no_actions()
    {
        var model = new PolicyModel(PolicyModelFixtures.Phase2Catalog());

        Assert.Empty(model.Actions("chains", "chain.secret_read_then_egress"));
        Assert.Empty(model.Actions("families", "command", model.ScopeRow("global")));
        Assert.Empty(model.Actions("posture", "nobody"));
        Assert.Empty(model.Actions("policies", "nobody"));
        Assert.Empty(model.Actions("packs", "nobody"));
        Assert.Empty(model.Actions("optin", "nobody", model.ScopeRow("global")));
    }

    [Fact]
    public void A_policy_whose_name_the_cli_cannot_be_handed_gets_no_actions()
    {
        var catalog = PolicyModelFixtures.Phase2Catalog();
        var model = new PolicyModel(catalog with { Policies = catalog.Policies.Append(catalog.Policies[0] with { Name = "-oops", IsActive = false, IsBuiltIn = false }).ToArray() });

        Assert.Empty(model.Actions("policies", "-oops"));
        Assert.NotEmpty(model.Actions("policies", "strict"));
    }
}
