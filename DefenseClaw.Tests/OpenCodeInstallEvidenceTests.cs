using System.Text.Json;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-424: which OpenCode the files say this account has, read the way cli/defenseclaw/migrations.py does at tag 1.0.6 (<c>_opencode_configured</c> :3282,
/// <c>_opencode_v2_in_known_places</c> :3344; <c>opencode_requires_v2_upgrade</c> in connector_contracts.py). The files are synthetic and in a scratch directory; no
/// real npm prefix is read, because every call names its prefixes.
/// </summary>
public sealed class OpenCodeInstallEvidenceTests : IDisposable
{
    private readonly TempDirectory _temp = new("dcw-opencode-evidence");
    private readonly string _data;
    private readonly string _npm;
    private readonly string _binary;

    public OpenCodeInstallEvidenceTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_temp.Path, "data")).FullName;
        _npm = Path.Combine(_temp.Path, "npm");
        _binary = Path.Combine(Directory.CreateDirectory(Path.Combine(_temp.Path, "bin")).FullName, "opencode.exe");
        File.WriteAllText(_binary, "synthetic");
    }

    public void Dispose() => _temp.Dispose();

    private IReadOnlyList<string> Prefixes => [_npm];

    private void Configure(string yaml = "guardrail:\n  connectors:\n    opencode:\n      mode: observe\n") => File.WriteAllText(Path.Combine(_data, "config.yaml"), yaml);

    private void Discovery(string version, bool installed = true, string? binary = null) =>
        File.WriteAllText(Path.Combine(_data, "agent_discovery.json"), JsonSerializer.Serialize(new
        {
            agents = new Dictionary<string, object>
            {
                ["opencode"] = new Dictionary<string, object> { ["installed"] = installed, ["version"] = version, ["binary_path"] = binary ?? _binary },
            },
        }));

    private void Package(string version, params string[] package)
    {
        var folder = Directory.CreateDirectory(Path.Combine([_npm, "node_modules", .. package])).FullName;
        File.WriteAllText(Path.Combine(folder, "package.json"), JsonSerializer.Serialize(new { name = string.Join('/', package), version }));
    }

    [Theory]
    [InlineData("opencode v2.2.3", 2)]
    [InlineData("2.2.4", 2)]
    [InlineData("v02.1", 2)]
    [InlineData("1.18.34", 1)]
    [InlineData("nothing", null)]
    [InlineData("", null)]
    public void The_major_is_the_first_number_of_the_version_text(string raw, int? major) => Assert.Equal(major, OpenCodeInstallEvidence.MajorOf(raw));

    [Theory]
    [InlineData("1.18.34", true)]
    [InlineData("3.0.0", true)]
    [InlineData("opencode vgit.5bba024", true)]
    [InlineData("2.2.3", false)]
    [InlineData("", false)]
    public void What_1_0_6_refuses_is_a_source_build_or_a_major_other_than_2(string raw, bool refused) => Assert.Equal(refused, OpenCodeInstallEvidence.RequiresV2Upgrade(raw));

    [Theory]
    [InlineData("guardrail:\n  connectors:\n    opencode: {}\n", true)]
    [InlineData("guardrail:\n  connectors:\n    codex: {}\n", false)]
    [InlineData("claw:\n  mode: opencode\n", true)]
    [InlineData("claw:\n  mode: codex\n", false)]
    [InlineData("guardrail: [not: closed", false)]
    public void Opencode_is_configured_when_the_active_connectors_name_it(string yaml, bool configured)
    {
        Configure(yaml);
        Assert.Equal(configured, OpenCodeInstallEvidence.IsConfigured(_data));
    }

    [Fact]
    public void An_install_that_does_not_guard_opencode_is_not_configured_whatever_is_on_disk()
    {
        Package("2.2.4", "@opencode", "cli");

        Assert.Equal(OpenCodeInstallFacts.NotConfigured, OpenCodeInstallEvidence.Read(_data, Prefixes));
    }

    [Fact]
    public void A_v2_on_record_with_its_program_still_there_is_found()
    {
        Configure();
        Discovery("opencode v2.2.3");

        var facts = OpenCodeInstallEvidence.Read(_data, Prefixes);

        Assert.True(facts.Configured);
        Assert.Equal(new OpenCodeInstall("opencode v2.2.3", _binary), facts.V2);
        Assert.Null(facts.V1);
    }

    [Fact]
    public void A_record_whose_program_is_gone_or_not_installed_proves_nothing_and_the_package_decides()
    {
        Configure();
        Discovery("2.2.3", binary: Path.Combine(_temp.Path, "gone.exe"));
        Assert.Null(OpenCodeInstallEvidence.FindV2(_data, Prefixes));

        Discovery("2.2.3", installed: false);
        Assert.Null(OpenCodeInstallEvidence.FindV2(_data, Prefixes));

        Package("2.2.4", "@opencode", "cli");
        var found = OpenCodeInstallEvidence.FindV2(_data, Prefixes);
        Assert.Equal("2.2.4", found!.Version);
        Assert.EndsWith(Path.Combine("@opencode", "cli"), found.Location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_v1_on_record_and_a_v2_package_beside_it_is_v2_because_the_rollback_check_counts_the_known_places_too()
    {
        Configure();
        Discovery("1.18.34");
        Package("2.2.4", "@opencode", "cli");

        var facts = OpenCodeInstallEvidence.Read(_data, Prefixes);

        Assert.Equal("2.2.4", facts.V2!.Version);
        Assert.Null(facts.V1);
    }

    [Fact]
    public void A_v1_is_found_on_record_and_in_the_opencode_ai_package()
    {
        Configure();
        Discovery("1.18.34");
        var recorded = OpenCodeInstallEvidence.Read(_data, Prefixes);
        Assert.Null(recorded.V2);
        Assert.Equal("1.18.34", recorded.V1!.Version);

        File.Delete(Path.Combine(_data, "agent_discovery.json"));
        Package("1.17.0", "opencode-ai");
        Assert.Equal("1.17.0", OpenCodeInstallEvidence.Read(_data, Prefixes).V1!.Version);
    }

    [Fact]
    public void A_lib_node_modules_prefix_counts_as_upstream_has_it()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_npm, "lib", "node_modules", "@opencode", "cli")).FullName;
        File.WriteAllText(Path.Combine(folder, "package.json"), "{\"version\":\"2.0.1\"}");

        Assert.Equal("2.0.1", OpenCodeInstallEvidence.FindV2(_data, Prefixes)!.Version);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"agents\":[]}")]
    [InlineData("{\"agents\":{\"opencode\":\"2.2.3\"}}")]
    [InlineData("{\"agents\":{\"opencode\":{\"installed\":\"yes\",\"version\":\"2.2.3\"}}}")]
    public void A_malformed_discovery_file_is_nothing_found_and_never_an_error(string text)
    {
        Configure();
        File.WriteAllText(Path.Combine(_data, "agent_discovery.json"), text);

        Assert.Equal(new OpenCodeInstallFacts(true, null, null), OpenCodeInstallEvidence.Read(_data, Prefixes));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"name\":\"@opencode/cli\"}")]
    public void A_malformed_package_file_is_nothing_found_and_never_an_error(string text)
    {
        Configure();
        var folder = Directory.CreateDirectory(Path.Combine(_npm, "node_modules", "@opencode", "cli")).FullName;
        File.WriteAllText(Path.Combine(folder, "package.json"), text);

        Assert.Equal(new OpenCodeInstallFacts(true, null, null), OpenCodeInstallEvidence.Read(_data, Prefixes));
    }

    [Fact]
    public void A_file_over_the_size_cap_is_not_read()
    {
        Configure();
        File.WriteAllText(Path.Combine(_data, "agent_discovery.json"), "{\"agents\":{\"opencode\":{\"installed\":true,\"version\":\"2.2.3\",\"binary_path\":\"x\"}},\"pad\":\"" + new string('a', (2 * 1024 * 1024) + 16) + "\"}");

        Assert.Null(OpenCodeInstallEvidence.FindV2(_data, Prefixes, _ => true));
    }

    [Fact]
    public void A_version_from_a_file_is_written_out_for_display_and_cut_short()
    {
        Configure();
        Discovery("2.‮2.3" + new string('9', 200));

        var version = OpenCodeInstallEvidence.Read(_data, Prefixes).V2!.Version;

        Assert.DoesNotContain("‮", version, StringComparison.Ordinal);
        Assert.True(version.Length <= 32);
    }

    [Fact]
    public void A_missing_data_directory_is_nothing_found()
    {
        var gone = Path.Combine(_temp.Path, "gone");

        Assert.Equal(OpenCodeInstallFacts.NotConfigured, OpenCodeInstallEvidence.Read(gone, Prefixes));
        Assert.Null(OpenCodeInstallEvidence.FindV2(gone, Prefixes));
    }
}
