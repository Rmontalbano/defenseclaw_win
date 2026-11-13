using System.Text.Json;
using DefenseClaw.Core.ClaudeCode;

namespace DefenseClaw.Tests;

/// <summary>
/// The <c>cowork</c> object of <c>defenseclaw status --json</c> (runtime 1.0.8). One synthetic fixture per reason code, the protected case, the key being
/// absent (a user without Claude Desktop gets nothing: no row, no warning) and members of the wrong type.
/// </summary>
public class CoworkProtectionParseTests
{
    private const string Zip = "C:\\\\Users\\\\example\\\\.defenseclaw\\\\cowork\\\\defenseclaw-cowork-plugin.zip";

    private static string Status(string cowork) => "{\"environment\":\"windows\",\"cowork\":" + cowork + "}";

    private static CoworkProtection? Parse(string cowork) => CoworkProtection.FromStatusJson(Status(cowork));

    [Fact]
    public void A_protected_state_reads_every_member()
    {
        var state = Parse(
            "{\"protected\":true,\"detail\":\"protected, action mode: policy hits are blocked (plugin 1.0.20261101120000; last Cowork event 2026-11-01 12:30 UTC)\"," +
            "\"step\":\"\",\"reason_code\":\"\",\"last_event_at\":\"2026-11-01T12:30:00Z\",\"plugin_version\":\"1.0.20261101120000\",\"plugin_zip\":\"" + Zip + "\"}");

        Assert.NotNull(state);
        Assert.True(state.Protected);
        Assert.StartsWith("protected, action mode", state.Detail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, state.Step);
        Assert.Equal(string.Empty, state.ReasonCode);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero), state.LastEventAt);
        Assert.Equal("1.0.20261101120000", state.PluginVersion);
        Assert.Equal("C:\\Users\\example\\.defenseclaw\\cowork\\defenseclaw-cowork-plugin.zip", state.PluginZip);
        Assert.Equal("C:\\Users\\example\\.defenseclaw\\cowork", state.PluginFolder);
        Assert.False(state.IsNotBuilt || state.AwaitsUpload || state.IsClaudeCodeOff || state.IsGatewayCondition);
    }

    [Theory]
    [InlineData(CoworkReasonCodes.PluginNotBuilt, true, false, false, false)]
    [InlineData(CoworkReasonCodes.NoEvents, false, true, false, false)]
    [InlineData(CoworkReasonCodes.ClaudeCodeOff, false, false, true, false)]
    [InlineData(CoworkReasonCodes.GatewayDown, false, false, false, true)]
    [InlineData(CoworkReasonCodes.ClaudeCodeNotStarted, false, false, false, true)]
    [InlineData(CoworkReasonCodes.HealthUnavailable, false, false, false, true)]
    [InlineData(CoworkReasonCodes.PluginStale, false, false, false, false)]
    public void Each_reason_code_of_a_state_that_is_not_protected_reads_as_its_kind(string code, bool notBuilt, bool awaitsUpload, bool claudeCodeOff, bool gateway)
    {
        var state = Parse("{\"protected\":false,\"detail\":\"not protected: example\",\"step\":\"example step\",\"reason_code\":\"" + code + "\",\"last_event_at\":null,\"plugin_version\":null,\"plugin_zip\":null}");

        Assert.NotNull(state);
        Assert.False(state.Protected);
        Assert.Equal(code, state.ReasonCode);
        Assert.Equal("not protected: example", state.Detail);
        Assert.Equal("example step", state.Step);
        Assert.Equal((notBuilt, awaitsUpload, claudeCodeOff, gateway), (state.IsNotBuilt, state.AwaitsUpload, state.IsClaudeCodeOff, state.IsGatewayCondition));
        Assert.Null(state.LastEventAt);
        Assert.Null(state.PluginVersion);
        Assert.Null(state.PluginZip);
        Assert.Null(state.PluginFolder);
    }

    [Fact]
    public void The_seven_codes_are_the_whole_list()
    {
        Assert.Equal(7, CoworkReasonCodes.All.Count);
        Assert.Equal(7, CoworkReasonCodes.All.Distinct(StringComparer.Ordinal).Count());
        // cowork.py@1.0.9:685-692: evaluated after cowork_health_unavailable and before cowork_no_events.
        Assert.Equal(CoworkReasonCodes.HealthUnavailable, CoworkReasonCodes.All[4]);
        Assert.Equal(CoworkReasonCodes.PluginStale, CoworkReasonCodes.All[5]);
        Assert.Equal(CoworkReasonCodes.NoEvents, CoworkReasonCodes.All[6]);
        Assert.Equal("cowork_plugin_stale", CoworkReasonCodes.PluginStale);
    }

    [Fact]
    public void A_stale_plugin_is_its_own_kind_and_no_other()
    {
        var state = Parse("{\"protected\":false,\"reason_code\":\"cowork_plugin_stale\"}");

        Assert.NotNull(state);
        Assert.True(state.IsPluginStale);
        Assert.False(state.AwaitsUpload);
        Assert.False(state.IsNotBuilt);
        Assert.False(state.IsGatewayCondition);
    }

    [Fact]
    public void A_state_that_says_protected_is_never_a_kind_of_trouble_whatever_its_code()
    {
        var state = Parse("{\"protected\":true,\"reason_code\":\"cowork_no_events\"}");

        Assert.NotNull(state);
        Assert.False(state.AwaitsUpload);
        Assert.False(state.IsGatewayCondition);
    }

    [Theory]
    [InlineData("{\"environment\":\"windows\"}")]
    [InlineData("{\"cowork\":null}")]
    [InlineData("{\"cowork\":\"protected\"}")]
    [InlineData("{\"cowork\":[true]}")]
    [InlineData("{\"cowork\":true}")]
    [InlineData("{}")]
    public void No_cowork_object_reads_as_nothing_to_say(string json)
    {
        Assert.Null(CoworkProtection.FromStatusJson(json));
    }

    [Fact]
    public void Members_of_the_wrong_type_read_as_empty_and_not_protected()
    {
        var state = Parse("{\"protected\":\"yes\",\"detail\":5,\"step\":[1],\"reason_code\":{},\"last_event_at\":\"not-a-date\",\"plugin_version\":7,\"plugin_zip\":false}");

        Assert.NotNull(state);
        Assert.False(state.Protected);
        Assert.Equal(string.Empty, state.Detail);
        Assert.Equal(string.Empty, state.Step);
        Assert.Equal(string.Empty, state.ReasonCode);
        Assert.Null(state.LastEventAt);
        Assert.Null(state.PluginVersion);
        Assert.Null(state.PluginZip);
    }

    [Fact]
    public void An_empty_object_is_a_state_that_is_not_protected_with_no_code()
    {
        var state = Parse("{}");

        Assert.NotNull(state);
        Assert.False(state.Protected);
        Assert.False(state.IsNotBuilt || state.AwaitsUpload || state.IsClaudeCodeOff || state.IsGatewayCondition);
    }

    [Fact]
    public void Text_that_is_not_json_throws_a_json_exception()
    {
        _ = Assert.ThrowsAny<JsonException>(() => CoworkProtection.FromStatusJson("not json"));
    }
}
