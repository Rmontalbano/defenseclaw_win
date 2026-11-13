using DefenseClaw.Core.Config;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// A rule pack's posture as the 1.0.9 gateway reads it: the manifest's <c>posture</c> (<c>defenseclaw-pack.json</c>), else the folder's name. Synthetic folders.
/// </summary>
public sealed class RulePackPostureTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Pack(string name, string? manifest)
    {
        var folder = Path.Combine(_temp.Path, name);
        _ = Directory.CreateDirectory(folder);
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(folder, RulePackPosture.ManifestFile), manifest);
        }

        return folder;
    }

    [Fact]
    public void The_built_in_default_folder_reads_default_with_the_1_0_9_manifest_and_without_it()
    {
        Assert.Equal("default", RulePackPosture.For(Pack(@"with\default", "{\"posture\":\"default\"}")));
        Assert.Equal("default", PolicyLevels.PackProfile(Pack(@"with2\default", "{\"posture\":\"default\"}"), null));

        // The 1.0.8 layout: the same folder name, no manifest.
        Assert.Equal("default", RulePackPosture.For(Pack(@"without\default", null)));
        Assert.Equal("default", PolicyLevels.PackProfile(Pack(@"without2\default", null)));
    }

    [Theory]
    [InlineData("strict", "{\"posture\":\"strict\"}", "strict")]
    [InlineData("permissive", "{\"posture\":\"permissive\"}", "permissive")]
    [InlineData("mypack", "{\"posture\":\"strict\"}", "strict")]
    [InlineData("mypack", "{\"posture\":\" Permissive \"}", "permissive")]
    [InlineData("strict", "{\"posture\":\"default\"}", "default")]
    public void A_manifest_posture_wins_over_the_folder_name(string name, string manifest, string expected)
    {
        var folder = Pack(name, manifest);
        Assert.Equal(expected, RulePackPosture.For(folder));
        Assert.Equal(expected, PolicyLevels.PackProfile(folder, PackPostures.Read(new[] { folder }, f => RulePackPosture.FromManifest(f))));
    }

    [Fact]
    public void PackProfile_and_ResolveLevels_read_no_file_and_use_the_postures_they_are_given()
    {
        var folder = Pack("mypack", "{\"posture\":\"strict\"}");

        // Without postures the name decides, even though the folder holds a strict manifest: nothing was read from the disk.
        Assert.Equal("default", PolicyLevels.PackProfile(folder));
        Assert.Equal(4, PolicyLevels.ResolveLevels(folder).BlockRank);

        var reads = new List<string>();
        var postures = PackPostures.Read(new[] { folder, folder.ToUpperInvariant(), string.Empty, null }, f =>
        {
            reads.Add(f);
            return RulePackPosture.FromManifest(f);
        });

        // Each distinct folder is read once, however many rows use it.
        Assert.Single(reads);
        Assert.Equal("strict", PolicyLevels.PackProfile(folder, postures));
        Assert.Equal(2, PolicyLevels.ResolveLevels(folder, default, null, postures).BlockRank);
        Assert.Equal("default", PolicyLevels.PackProfile(Path.Combine(_temp.Path, "other"), postures));
        for (var i = 0; i < 5; i++)
        {
            _ = PolicyLevels.PackProfile(folder, postures);
            _ = PolicyLevels.ProfileLevelsFor(folder, postures);
        }

        Assert.Single(reads);
    }

    [Theory]
    [InlineData("mypack", "{\"posture\":\"strict\"", "default")]
    [InlineData("mypack", "not json", "default")]
    [InlineData("mypack", "[\"strict\"]", "default")]
    [InlineData("mypack", "{\"posture\":\"paranoid\"}", "default")]
    [InlineData("mypack", "{\"posture\":\"..\\\\strict\"}", "default")]
    [InlineData("mypack", "{\"posture\":\"/etc/strict\"}", "default")]
    [InlineData("mypack", "{\"posture\":5}", "default")]
    [InlineData("mypack", "{\"base\":\"strict\"}", "default")]
    [InlineData("strict", "{\"posture\":\"paranoid\"}", "strict")]
    [InlineData("permissive", "garbage", "permissive")]
    public void A_manifest_that_cannot_be_used_falls_back_to_the_folder_name(string name, string manifest, string expected) =>
        Assert.Equal(expected, RulePackPosture.For(Pack(name, manifest)));

    [Theory]
    [InlineData("{\"Posture\":\"strict\"}", "strict")]
    [InlineData("{\"POSTURE\":\"permissive\"}", "permissive")]
    [InlineData("{\"posture\":\"default\",\"POSTURE\":\"strict\"}", "strict")]
    [InlineData("{\"POSTURE\":\"strict\",\"posture\":\"default\"}", "default")]
    [InlineData("{\"posture\":\"strict\",\"posture\":\"permissive\"}", "permissive")]
    [InlineData("{\"posture\":\"strict\",\"Posture\":null}", "strict")]
    public void The_manifest_key_matches_the_way_Gos_json_decoder_does_and_the_last_match_wins(string manifest, string expected) =>
        Assert.Equal(expected, RulePackPosture.FromManifest(@"X:\packs\mypack", _ => manifest));

    [Theory]
    [InlineData("{\"posture\":\"default\",\"POSTURE\":5}")]
    [InlineData("{\"posture\":5,\"Posture\":\"strict\"}")]
    [InlineData("{\"Posture\":[\"strict\"]}")]
    public void A_matching_key_with_a_value_that_is_not_a_string_means_no_posture(string manifest) =>
        Assert.Null(RulePackPosture.FromManifest(@"X:\packs\mypack", _ => manifest));

    [Fact]
    public void A_manifest_over_1_MiB_is_ignored()
    {
        var big = "{\"posture\":\"strict\",\"pad\":\"" + new string('x', RulePackPosture.MaxManifestBytes) + "\"}";
        Assert.Equal("default", RulePackPosture.For(Pack("mypack", big)));
        Assert.Equal("default", RulePackPosture.For(@"X:\packs\mypack", _ => big));
        Assert.Equal("strict", RulePackPosture.For(@"X:\packs\mypack", _ => "{\"posture\":\"strict\"}"));
    }

    [Fact]
    public void Only_the_manifest_file_in_the_folder_is_read()
    {
        var read = new List<string>();
        _ = RulePackPosture.For(@"X:\packs\mypack", path =>
        {
            read.Add(path);
            return "{\"posture\":\"strict\",\"path\":\"X:\\\\elsewhere\"}";
        });

        Assert.Equal(new[] { Path.Combine(@"X:\packs\mypack", "defenseclaw-pack.json") }, read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void No_folder_reads_default(string? folder) => Assert.Equal("default", RulePackPosture.For(folder, _ => "{\"posture\":\"strict\"}"));

    // ------------------------------------------------------------------ the hook outage tier note

    [Fact]
    public void A_connectors_rule_pack_dir_folder_gives_its_manifest_posture_then_its_name()
    {
        var yaml = "guardrail:\n  connectors:\n    codex:\n      rule_pack_dir: X:\\packs\\mypack\n";
        var guardrail = ConfigStore.Parse(yaml).Config.Guardrail;

        Assert.Equal("strict", HookFailModeRule.KnownPolicyTier(guardrail, "codex", _ => "{\"posture\":\"strict\"}"));
        Assert.Null(HookFailModeRule.KnownPolicyTier(guardrail, "codex", _ => null));
        Assert.Null(HookFailModeRule.KnownPolicyTier(guardrail, "codex", _ => "{\"posture\":\"odd\"}"));

        var named = ConfigStore.Parse("guardrail:\n  connectors:\n    codex:\n      rule_pack_dir: X:\\packs\\strict\n").Config.Guardrail;
        Assert.Equal("strict", HookFailModeRule.KnownPolicyTier(named, "codex", _ => null));
        Assert.Equal("permissive", HookFailModeRule.KnownPolicyTier(named, "codex", _ => "{\"posture\":\"permissive\"}"));
    }

    [Fact]
    public void A_custom_pack_name_stays_unknown_even_with_a_folder_that_could_be_read()
    {
        var guardrail = ConfigStore.Parse("guardrail:\n  rule_pack: team-pack\n").Config.Guardrail;
        Assert.Null(HookFailModeRule.KnownPolicyTier(guardrail, "codex", _ => "{\"posture\":\"strict\"}"));
        Assert.Null(HookFailModeRule.KnownPolicyTier(guardrail, "codex"));
    }
}
