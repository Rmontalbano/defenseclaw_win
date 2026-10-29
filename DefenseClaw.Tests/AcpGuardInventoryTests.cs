using System.Text.Json;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-294: the gateway's two operator ACP reads, <c>GET /v1/acp/catalog</c> and <c>GET /v1/acp/profiles</c>, as 1.0.4 writes them (synthetic fixtures built
/// from <c>acp.Catalog</c> and <c>config.ACPProfile</c>). A runtime without the routes (404/405), a stopped gateway and a refused token are "nothing to show",
/// never an error; an id the runtime would not accept never reaches a command line.
/// </summary>
public sealed class AcpGuardInventoryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0.4", "acp", name));

    private static GatewayResult<JsonDocument> Ok(string json) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(json));

    [Fact]
    public void The_catalog_lists_the_built_in_editors_and_agents_with_their_support_and_executable()
    {
        var read = AcpGuardReader.Catalog(Ok(Fixture("acp-catalog.synthetic.json")));

        Assert.Equal(AcpReadStatus.Ok, read.Status);
        var catalog = read.Value!;
        Assert.Equal(new[] { "zed", "jetbrains" }, catalog.Clients.Select(c => c.Id));
        Assert.Equal(new AcpCatalogClient("zed", "Zed", "tested"), catalog.Clients[0]);
        Assert.Equal(new[] { "kiro", "cursor", "opencode", "hermes", "copilot", "devin" }, catalog.Agents.Select(a => a.Id));
        Assert.Equal(new AcpCatalogAgent("kiro", "Kiro", "tested", "kiro-cli"), catalog.Agents[0]);
        Assert.Equal("cataloged", catalog.Agents.Single(a => a.Id == "copilot").Support);
        Assert.Equal("schema-v1.21.0", catalog.SchemaVersion);
    }

    [Fact]
    public void The_profiles_are_the_loaded_policy_by_name_with_mode_fail_mode_and_the_pairs_they_admit()
    {
        var read = AcpGuardReader.Profiles(Ok(Fixture("acp-profiles.synthetic.json")));

        Assert.True(read.IsOk);
        var profiles = read.Value!;
        Assert.True(profiles.Enabled);
        Assert.Equal("observe", profiles.Mode);
        Assert.Equal("default", profiles.DefaultProfile);
        Assert.Equal(new[] { "default", "locked" }, profiles.Profiles.Select(p => p.Name));
        var locked = profiles.Profiles[1];
        Assert.Equal("action", locked.Mode);
        Assert.Equal("closed", locked.FailMode);
        Assert.Equal(new[] { "zed" }, locked.AllowedClients);
        Assert.Equal(new[] { "devin" }, locked.AllowedAgents);
    }

    [Fact]
    public void An_unconfigured_guard_answers_null_profiles_which_is_an_empty_list()
    {
        // Go writes a nil map as null; a fresh install has no acp section at all.
        var read = AcpGuardReader.Profiles(Ok("""{"default_profile":"","enabled":false,"mode":"observe","profiles":null}"""));

        Assert.True(read.IsOk);
        Assert.False(read.Value!.Enabled);
        Assert.Empty(read.Value.Profiles);
        Assert.Equal(string.Empty, read.Value.DefaultProfile);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    public void A_runtime_without_the_routes_is_unsupported_not_an_error(int code)
    {
        Assert.Equal(AcpReadStatus.Unsupported, AcpGuardReader.Catalog(GatewayResult<JsonDocument>.Error("gateway returned HTTP " + code, code)).Status);
        Assert.Equal(AcpReadStatus.Unsupported, AcpGuardReader.Profiles(GatewayResult<JsonDocument>.Error("gateway returned HTTP " + code, code)).Status);
    }

    [Fact]
    public void A_stopped_gateway_a_refused_token_or_an_answer_of_another_shape_is_nothing_to_show()
    {
        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardReader.Catalog(GatewayResult<JsonDocument>.Unreachable()).Status);
        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardReader.Profiles(GatewayResult<JsonDocument>.Unauthorized()).Status);
        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardReader.Catalog(Ok("""{"agents":"none"}""")).Status);
        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardReader.Profiles(Ok("""["default"]""")).Status);
        Assert.Equal(AcpReadStatus.Unavailable, AcpGuardReader.Catalog(GatewayResult<JsonDocument>.Error("gateway returned HTTP 500", 500)).Status);
    }

    [Fact]
    public void Only_ids_the_runtime_accepts_are_kept_so_none_can_smuggle_a_flag_onto_the_command_line()
    {
        var catalog = AcpGuardReader.ParseCatalog(JsonDocument.Parse("""
            {"clients":[{"id":"--managed","name":"x"},{"id":"Zed Editor","name":"y"},{"id":"zed","name":"Zed"},{"id":"zed","name":"Zed again"}],
             "agents":[{"id":"kiro;calc","name":"k"},{"id":"kiro","name":"Kiro","command":"kiro-cli"}]}
            """).RootElement)!;
        Assert.Equal(new[] { "zed" }, catalog.Clients.Select(c => c.Id));
        Assert.Equal("Zed", catalog.Clients[0].Name);
        Assert.Equal(new[] { "kiro" }, catalog.Agents.Select(a => a.Id));

        var profiles = AcpGuardReader.ParseProfiles(JsonDocument.Parse("""
            {"enabled":true,"mode":"action","default_profile":"../x","profiles":{"--activate":{"mode":"action"},"ok":{"mode":"sideways","allowed_agents":["kiro","-x"]}}}
            """).RootElement)!;
        Assert.Equal(new[] { "ok" }, profiles.Profiles.Select(p => p.Name));
        Assert.Equal(string.Empty, profiles.Profiles[0].Mode); // not observe or action: shown as not set
        Assert.Equal(new[] { "kiro" }, profiles.Profiles[0].AllowedAgents);
        Assert.Equal(string.Empty, profiles.DefaultProfile);
        Assert.Equal("action", profiles.Mode);
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("kiro-only", true)]
    [InlineData("p_2", true)]
    [InlineData("", false)]
    [InlineData("Default", false)]
    [InlineData("-x", false)]
    [InlineData("(another)", false)]
    [InlineData("a b", false)]
    public void Stable_names_follow_the_runtime_pattern(string value, bool expected) => Assert.Equal(expected, AcpGuardReader.IsStableName(value));
}
