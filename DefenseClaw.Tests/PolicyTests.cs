using DefenseClaw.Core.Policy;

namespace DefenseClaw.Tests;

/// <summary>
/// The Policies parsers, comparison, command builders and backend choice (CUST-281). The reports below are synthetic, in the shape the
/// 0.8.10 <c>policy list</c> / <c>policy show</c> print (<c>commands/cmd_policy.py</c>).
/// </summary>
public sealed class PolicyTests
{
    public const string ListReport =
        "Available policies:\r\n\r\n" +
        "  * default [built-in] [active]\r\n      Balanced fixture policy\r\n" +
        "    bare-builtin [built-in]\r\n" +
        "    strict [built-in]\r\n      Strict fixture policy\r\n" +
        "    team-baseline\r\n      A custom fixture policy\r\n\r\n" +
        "  Activate a policy: defenseclaw policy activate <name>\r\n" +
        "  Show details:      defenseclaw policy show <name>\r\n";

    public static string Show(
        string name = "fixture",
        string install = "block",
        string runtime = "disable",
        int block = 4,
        int alert = 2,
        bool hilt = false,
        string trust = "full",
        string firewallDefault = "deny",
        int domains = 9,
        string ports = "[443, 80]",
        string extraOverride = "") =>
        $"Policy: {name}\n  A fixture\n\n" +
        "Admission:\n  scan_on_install:        True\n  allow_list_bypass_scan: True\n\n" +
        "Severity Actions:\n" +
        $"  CRITICAL    install={install,-5}  file=quarantine  runtime={runtime}\n" +
        "  HIGH        install=none   file=none        runtime=enable\n" +
        "  INFO        install=none   file=none        runtime=enable\n" +
        extraOverride +
        $"\nGuardrail:\n  block_threshold:    {block} (severity rank)\n  alert_threshold:    {alert} (severity rank)\n" +
        $"  hilt:               enabled={(hilt ? "True" : "False")} min=HIGH\n  cisco_trust_level:  {trust}\n" +
        "  patterns:\n    injection: 12 pattern(s)\n  severity_mappings:\n    injection: HIGH\n\n" +
        $"Firewall:\n  default_action:        {firewallDefault}\n  blocked_destinations:  2 entries\n  allowed_domains:       {domains} entries\n  allowed_ports:         {ports}\n\n" +
        "Enforcement:\n  max_enforcement_delay_seconds: 2\n\nAudit:\n  retention_days: 90\n";

    private static PolicyDetail Parse(string text)
    {
        Assert.True(PolicyTextParser.TryParseDetail(text, out var detail, out var error), error);
        return detail!;
    }

    // ------------------------------------------------------------------ list

    [Fact]
    public void List_reads_names_tags_the_active_marker_and_descriptions_with_crlf()
    {
        Assert.True(PolicyTextParser.TryParseList(ListReport, out var listing, out var error), error);

        Assert.Equal(new[] { "default", "bare-builtin", "strict", "team-baseline" }, listing.Policies.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { true, true, true, false }, listing.Policies.Select(p => p.IsBuiltIn).ToArray());
        Assert.Equal("default", listing.ActiveName);
        Assert.Equal("Balanced fixture policy", listing.Policies[0].Description);
        Assert.Equal(string.Empty, listing.Policies[1].Description);
        Assert.Equal("A custom fixture policy", listing.Policies[3].Description);
    }

    [Fact]
    public void List_ignores_colour_codes_and_reads_an_empty_install_as_no_policies()
    {
        Assert.True(PolicyTextParser.TryParseList("\u001b[1mAvailable policies:\u001b[0m\n\n  * \u001b[1mdefault\u001b[0m\u001b[2m [built-in]\u001b[0m \u001b[32m[active]\u001b[0m\n", out var coloured, out _));
        Assert.Equal("default", Assert.Single(coloured.Policies).Name);
        Assert.True(coloured.Policies[0].IsActive);

        Assert.True(PolicyTextParser.TryParseList("! No policies found.\n", out var none, out _));
        Assert.Empty(none.Policies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):\n  boom")]
    public void List_output_it_does_not_recognise_is_an_error_with_a_sentence(string text)
    {
        Assert.False(PolicyTextParser.TryParseList(text, out var listing, out var error));
        Assert.Empty(listing.Policies);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("my_policy-2.1", true)]
    [InlineData("-rf", false)]
    [InlineData("a b", false)]
    [InlineData("..\\x", false)]
    [InlineData("a/b", false)]
    [InlineData("a..b", false)]
    [InlineData("", false)]
    public void Only_plain_names_may_be_put_on_a_command_line(string name, bool safe)
    {
        Assert.Equal(safe, PolicyNames.IsSafe(name));
        Assert.Equal(safe, new PolicySummary(name, "", false, false).HasSafeName);
    }

    // ------------------------------------------------------------------ show

    [Fact]
    public void Show_reads_every_section()
    {
        var detail = Parse(Show(extraOverride: "\nScanner Overrides:\n  mcp:\n    MEDIUM      install=block  file=quarantine  runtime=block\n"));

        Assert.Equal("fixture", detail.Name);
        Assert.Equal("A fixture", detail.Description);
        Assert.True(detail.ScanOnInstall);
        Assert.Equal(new PolicySeverityAction("block", "quarantine", "disable"), detail.ActionFor("critical"));
        Assert.Equal(PolicySeverityAction.Default, detail.ActionFor("LOW"));
        Assert.Equal("block", detail.EffectiveActionFor("mcp", "MEDIUM").Install);
        Assert.Equal(PolicySeverityAction.Default, detail.EffectiveActionFor("skill", "MEDIUM"));
        Assert.Equal(4, detail.Guardrail!.BlockThreshold);
        Assert.Equal(2, detail.Guardrail.AlertThreshold);
        Assert.False(detail.Guardrail.HiltEnabled);
        Assert.Equal("HIGH", detail.Guardrail.HiltMinSeverity);
        Assert.Equal(12, detail.Guardrail.PatternCounts["injection"]);
        Assert.Equal("HIGH", detail.Guardrail.SeverityMappings["injection"]);
        Assert.Equal("deny", detail.Firewall!.DefaultAction);
        Assert.Equal(9, detail.Firewall.AllowedDomainCount);
        Assert.Equal(new[] { 443, 80 }, detail.Firewall.AllowedPorts);
        Assert.Equal(2, detail.EnforcementDelaySeconds);
        Assert.Equal(90, detail.AuditRetentionDays);
    }

    [Fact]
    public void Show_of_something_else_is_an_error_not_an_empty_policy()
    {
        Assert.False(PolicyTextParser.TryParseDetail("error: policy 'x' not found", out var detail, out var error));
        Assert.Null(detail);
        Assert.NotEmpty(error);
    }

    // ------------------------------------------------------------------ comparison

    [Fact]
    public void Identical_policies_have_no_changes()
    {
        var summary = PolicyDiff.Compare(Parse(Show("a")), Parse(Show("b")));
        Assert.True(summary.IsEmpty);
        Assert.False(summary.Weakens);
    }

    [Fact]
    public void An_action_that_stops_blocking_weakens()
    {
        var summary = PolicyDiff.Compare(Parse(Show("a")), Parse(Show("b", install: "none", runtime: "enable")));

        var line = Assert.Single(summary.Lines);
        Assert.True(line.Weakens);
        Assert.Contains("CRITICAL", line.Text, StringComparison.Ordinal);
        Assert.Contains("install block -> none", line.Text, StringComparison.Ordinal);
        Assert.True(summary.Weakens);
    }

    [Fact]
    public void A_stronger_switch_lists_its_changes_without_weakening()
    {
        var summary = PolicyDiff.Compare(
            Parse(Show("a", install: "none", runtime: "enable", block: 4, hilt: false)),
            Parse(Show("b", install: "block", runtime: "disable", block: 3, hilt: true, firewallDefault: "deny")));

        Assert.NotEmpty(summary.Lines);
        Assert.All(summary.Lines, l => Assert.False(l.Weakens, l.Text));
        Assert.False(summary.Weakens);
    }

    [Theory]
    [InlineData("block", 3, 4)]
    [InlineData("alert", 2, 3)]
    public void A_higher_threshold_weakens(string which, int from, int to)
    {
        var before = which == "block" ? Show(block: from) : Show(alert: from);
        var after = which == "block" ? Show(block: to) : Show(alert: to);

        var summary = PolicyDiff.Compare(Parse(before), Parse(after));
        Assert.True(Assert.Single(summary.Lines).Weakens);
    }

    [Fact]
    public void Guardrail_firewall_and_trust_changes_that_open_things_up_weaken()
    {
        var weaker = Parse(Show(trust: "none", hilt: false, firewallDefault: "allow", domains: 20, ports: "[443, 80, 8080]"));
        var summary = PolicyDiff.Compare(Parse(Show(hilt: true)), weaker);

        var text = string.Join('\n', summary.WeakeningLines.Select(l => l.Text));
        Assert.Contains("Human approval (HILT) on -> off", text, StringComparison.Ordinal);
        Assert.Contains("Cisco trust level full -> none", text, StringComparison.Ordinal);
        Assert.Contains("Default egress action deny -> allow", text, StringComparison.Ordinal);
        Assert.Contains("Allowed domains 9 -> 20", text, StringComparison.Ordinal);
        Assert.Contains("port 8080", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scanner_override_dropped_by_the_target_is_compared_by_effect()
    {
        var withOverride = Parse(Show("a", extraOverride: "\nScanner Overrides:\n  mcp:\n    MEDIUM      install=block  file=quarantine  runtime=block\n"));
        var without = Parse(Show("b"));

        var summary = PolicyDiff.Compare(withOverride, without);
        var line = Assert.Single(summary.Lines);
        Assert.Equal("Scanner overrides", line.Area);
        Assert.True(line.Weakens);
    }

    [Fact]
    public void A_comparison_that_could_not_be_made_counts_as_weakening()
    {
        var summary = PolicyDiff.Compare(null, Parse(Show()));

        Assert.False(summary.IsComplete);
        Assert.True(summary.Weakens);
    }

    // ------------------------------------------------------------------ edit

    [Fact]
    public void Edit_actions_builds_the_argv_and_names_the_policy()
    {
        var edit = new PolicyEdit { Section = PolicyEditSection.Actions, Severity = "high", Runtime = "enable", Install = "none" };

        Assert.Null(edit.Validate());
        Assert.Equal(
            new[] { "policy", "edit", "actions", "--severity", "high", "--runtime", "enable", "--install", "none", "--policy-name", "team-baseline" },
            edit.ToArgv("team-baseline"));
    }

    [Fact]
    public void Edit_scanner_remove_passes_only_remove()
    {
        var edit = new PolicyEdit { Section = PolicyEditSection.Scanner, ScannerType = "mcp", Severity = "medium", RemoveOverride = true, Runtime = "enable" };

        Assert.Null(edit.Validate());
        Assert.Equal(
            new[] { "policy", "edit", "scanner", "--type", "mcp", "--severity", "medium", "--remove", "--policy-name", "p" },
            edit.ToArgv("p"));
    }

    [Fact]
    public void Edit_guardrail_and_firewall_build_their_flags()
    {
        var guardrail = new PolicyEdit
        {
            Section = PolicyEditSection.Guardrail,
            BlockThreshold = 3,
            CiscoTrustLevel = "advisory",
            AddPatternCategory = "injection",
            AddPattern = "ignore previous",
            MappingCategory = "secrets",
            MappingSeverity = "high",
        };
        Assert.Null(guardrail.Validate());
        Assert.Equal(
            new[]
            {
                "policy", "edit", "guardrail", "--block-threshold", "3", "--cisco-trust-level", "advisory",
                "--add-pattern", "injection", "ignore previous", "--set-severity-mapping", "secrets", "HIGH", "--policy-name", "p",
            },
            guardrail.ToArgv("p"));

        var firewall = new PolicyEdit { Section = PolicyEditSection.Firewall, DefaultAction = "deny", AddDomain = "example.test", RemovePort = 80 };
        Assert.Null(firewall.Validate());
        Assert.Equal(
            new[] { "policy", "edit", "firewall", "--default-action", "deny", "--add-domain", "example.test", "--remove-port", "80", "--policy-name", "p" },
            firewall.ToArgv("p"));
    }

    [Fact]
    public void Edit_rejects_nothing_to_change_a_bad_rank_and_values_that_would_read_as_options()
    {
        Assert.NotNull(new PolicyEdit { Section = PolicyEditSection.Actions, Severity = "high" }.Validate());
        Assert.NotNull(new PolicyEdit { Section = PolicyEditSection.Actions }.Validate());
        Assert.NotNull(new PolicyEdit { Section = PolicyEditSection.Guardrail, BlockThreshold = 9 }.Validate());
        Assert.NotNull(new PolicyEdit { Section = PolicyEditSection.Guardrail }.Validate());
        Assert.NotNull(new PolicyEdit { Section = PolicyEditSection.Firewall, AddDomain = "--yes" }.Validate());
        Assert.NotNull(new PolicyEdit { Section = PolicyEditSection.Firewall, AddPort = 70000 }.Validate());
        Assert.NotNull(new PolicyEdit { Section = PolicyEditSection.Guardrail, AddPattern = "x" }.Validate());
        Assert.Throws<ArgumentException>(() => new PolicyEdit { Section = PolicyEditSection.Actions, Severity = "high", Runtime = "enable" }.ToArgv("-x"));
    }

    [Fact]
    public void An_edit_is_applied_to_the_parsed_policy_so_its_effect_can_be_compared()
    {
        var detail = Parse(Show());
        var weaker = new PolicyEdit { Section = PolicyEditSection.Actions, Severity = "critical", Install = "none", Runtime = "enable" };
        var stronger = new PolicyEdit { Section = PolicyEditSection.Actions, Severity = "low", Install = "block" };
        var firewall = new PolicyEdit { Section = PolicyEditSection.Firewall, AddPort = 8080, RemoveBlocked = "10.0.0.1" };

        Assert.True(PolicyDiff.Compare(detail, weaker.ApplyTo(detail)).Weakens);
        Assert.False(PolicyDiff.Compare(detail, stronger.ApplyTo(detail)).Weakens);
        var fw = PolicyDiff.Compare(detail, firewall.ApplyTo(detail));
        Assert.True(fw.Weakens);
        Assert.Equal(2, fw.WeakeningLines.Count);
    }

    // ------------------------------------------------------------------ create and delete

    [Fact]
    public void Create_builds_flags_in_order_and_refuses_protected_or_taken_names()
    {
        var create = new PolicyCreate
        {
            Name = "team-baseline",
            Description = "Fixture",
            FromPreset = "strict",
            ScanOnInstall = false,
            CriticalAction = "block",
            LowAction = "allow",
        };

        Assert.Null(create.Validate(new[] { "default" }));
        Assert.Equal(
            new[] { "policy", "create", "team-baseline", "--description", "Fixture", "--from-preset", "strict", "--no-scan-on-install", "--critical-action", "block", "--low-action", "allow" },
            create.ToArgv());

        Assert.NotNull(new PolicyCreate { Name = "strict" }.Validate());
        Assert.NotNull(create.Validate(new[] { "team-baseline" }));
        Assert.NotNull(new PolicyCreate { Name = "ok", Description = "-oops" }.Validate());
        Assert.NotNull(new PolicyCreate { Name = "../x" }.Validate());
        Assert.NotNull(new PolicyCreate { Name = "ok", HighAction = "quarantine" }.Validate());
    }

    [Fact]
    public void The_0810_backend_builds_each_command_and_only_it_is_chosen_for_that_runtime()
    {
        var backend = Release0810PolicyBackend.Instance;

        Assert.Equal(new[] { "policy", "list" }, backend.ListArgv);
        Assert.Equal(new[] { "policy", "show", "default" }, backend.ShowArgv("default"));
        Assert.Equal(new[] { "policy", "activate", "team-baseline" }, backend.ActivateArgv("team-baseline"));
        Assert.Equal(new[] { "policy", "delete", "team-baseline" }, backend.DeleteArgv("team-baseline", force: false));
        Assert.Equal(new[] { "policy", "delete", "team-baseline", "--force" }, backend.DeleteArgv("team-baseline", force: true));
        Assert.Throws<ArgumentException>(() => backend.ActivateArgv("--help"));

        Assert.Same(backend, PolicyBackends.ForInstalledRuntime());
        Assert.Same(backend, PolicyBackends.Select(PolicyBackends.Available, PolicyRuntimeCapabilities.Release0810));
        Assert.Equal(PolicySurface.NamedPolicies, backend.Surface);

        // A runtime with the seven-view model (CUST-293) gets the other backend, and only it.
        var withModel = new PolicyRuntimeCapabilities(HasSevenViewPanel: true);
        Assert.Same(SevenViewPolicyBackend.Instance, PolicyBackends.Select(PolicyBackends.Available, withModel));
        Assert.False(backend.Supports(withModel));
        Assert.False(SevenViewPolicyBackend.Instance.Supports(PolicyRuntimeCapabilities.Release0810));
        Assert.Equal(PolicySurface.SevenViewModel, SevenViewPolicyBackend.Instance.Surface);
    }
}
