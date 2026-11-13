using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview's rule-pack rows (connector table, scoped Configuration row, policy posture, scoped policy row) name the pack the runtime picks: a
/// config_version 9 <c>rule_pack</c> name was shown as "default" before, on every release.
/// </summary>
public sealed class RulePackRowsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private DefenseClaw.App.Services.AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private OverviewPanelViewModel Panel(string configYaml)
    {
        _services = TestServices.Create(_temp, configYaml);
        return new OverviewPanelViewModel(_services);
    }

    [Fact]
    public void A_global_rule_pack_name_is_shown_not_default()
    {
        var vm = Panel("guardrail:\n  enabled: true\n  rule_pack: strict\n  connectors:\n    codex:\n      mode: observe\n");
        Assert.Equal("strict", vm.RulePackLabel("codex"));
    }

    [Fact]
    public void A_connectors_rule_pack_name_wins_over_the_global_one()
    {
        var vm = Panel("guardrail:\n  enabled: true\n  rule_pack: strict\n  connectors:\n    codex:\n      rule_pack: permissive\n");
        Assert.Equal("permissive", vm.RulePackLabel("codex"));
    }

    [Fact]
    public void The_installers_default_folder_reads_default_as_before()
    {
        var folder = Path.Combine(_temp.Path, "policies", "guardrail", "default");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "defenseclaw-pack.json"), "{\"posture\":\"default\"}");

        var vm = Panel($"guardrail:\n  enabled: true\n  rule_pack_dir: '{folder}'\n  connectors:\n    codex:\n      mode: observe\n");

        Assert.Equal("default", vm.RulePackLabel("codex"));
    }

    [Fact]
    public void A_folder_whose_manifest_names_another_posture_says_so()
    {
        var folder = Path.Combine(_temp.Path, "packs", "team");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "defenseclaw-pack.json"), "{\"posture\":\"strict\"}");

        var vm = Panel($"guardrail:\n  enabled: true\n  connectors:\n    codex:\n      rule_pack_dir: '{folder}'\n");

        Assert.Equal("team (strict)", vm.RulePackLabel("codex"));
    }
}
