using System.Net;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Exercises the client against the captured 0.8.7 payloads through a fake handler, and
/// pins the auth rule measured by hand: /health is unauthenticated, everything else
/// carries a bearer token.
/// </summary>
public class GatewayClientTests
{
    private const string Token = "fixture-bearer-token-0123456789";

    private static (GatewayClient Client, FakeHttpMessageHandler Handler) Build(
        Action<FakeHttpMessageHandler> configure,
        string? token = Token)
    {
        var handler = new FakeHttpMessageHandler();
        configure(handler);

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
        var secret = token is null ? null : new SecretValue(token);
        return (new GatewayClient(http, () => secret, ownsHttpClient: true), handler);
    }

    [Fact]
    public async Task Health_parses_the_captured_payload()
    {
        var (client, _) = Build(h => h.MapFixture("/health", FixtureFiles.Health));

        var result = await client.GetHealthAsync();

        Assert.Equal(GatewayStatus.Ok, result.Status);
        var health = result.Value!;
        Assert.Equal("0.8.7", health.Provenance!.BinaryVersion);
        Assert.Equal(1623131, health.UptimeMs);
        Assert.True(health.Api!.IsRunning);
        Assert.Equal("127.0.0.1:18970", health.Api.DetailString("addr"));
        Assert.True(health.ApplicationProtection!.IsDisabled);
        Assert.Equal("application protection disabled", health.ApplicationProtection.LastError);
        Assert.Equal("claudecode", health.Connector!.Name);
        Assert.Equal(156, health.Connector.Requests);
        Assert.Equal(50, health.Connector.ToolInspections);
        Assert.Single(health.Connectors);
        Assert.Equal("enhanced", health.AiDiscovery!.DetailString("mode"));
        Assert.Equal(8, health.Services().Count());
    }

    /// <summary>
    /// 0.8.10 drift-check: same schema as 0.8.7, captured live after the machine upgrade on
    /// 2026-07-30. Confirms the client still parses the payload correctly against a real
    /// post-upgrade capture rather than only ever exercising the 0.8.7 fixture.
    /// </summary>
    [Fact]
    public async Task Health_parses_the_captured_0810_payload()
    {
        var (client, _) = Build(h => h.MapFixture("/health", "health-0.8.10.json"));

        var result = await client.GetHealthAsync();

        Assert.Equal(GatewayStatus.Ok, result.Status);
        var health = result.Value!;
        Assert.Equal("0.8.10", health.Provenance!.BinaryVersion);
        Assert.Single(health.Connectors);
        Assert.Equal("claudecode", health.Connectors[0].Name);
        Assert.True(health.AiDiscovery!.IsRunning);
        Assert.True(health.ApplicationProtection!.IsDisabled);
    }

    [Fact]
    public async Task Health_is_sent_without_an_authorization_header()
    {
        var (client, handler) = Build(h => h.MapFixture("/health", FixtureFiles.Health));

        await client.GetHealthAsync();

        // Measured against the live 0.8.7 gateway: /health is the only endpoint that
        // does not require a bearer token.
        Assert.Null(handler.RequestFor("/health")!.Headers.Authorization);
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/alerts")]
    [InlineData("/skills")]
    [InlineData("/mcps")]
    [InlineData("/tools/catalog")]
    [InlineData("/enforce/blocked")]
    [InlineData("/enforce/allowed")]
    public async Task Every_other_endpoint_carries_the_bearer_token(string path)
    {
        var (client, handler) = Build(h => h
            .MapFixture("/status", FixtureFiles.Status)
            .MapFixture("/alerts", FixtureFiles.Alerts)
            .MapFixture("/skills", FixtureFiles.Skills)
            .MapFixture("/mcps", FixtureFiles.Mcps)
            .MapFixture("/tools/catalog", FixtureFiles.ToolsCatalog)
            .MapFixture("/enforce/blocked", FixtureFiles.EnforceBlocked)
            .MapFixture("/enforce/allowed", FixtureFiles.EnforceAllowed));

        _ = path switch
        {
            "/status" => await Call(client.GetStatusAsync()),
            "/alerts" => await Call(client.GetAlertsAsync()),
            "/skills" => await Call(client.GetSkillsAsync()),
            "/mcps" => await Call(client.GetMcpsAsync()),
            "/tools/catalog" => await Call(client.GetToolsCatalogAsync()),
            "/enforce/blocked" => await Call(client.GetEnforceBlockedAsync()),
            _ => await Call(client.GetEnforceAllowedAsync()),
        };

        var authorization = handler.RequestFor(path)!.Headers.Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal(Token, authorization.Parameter);

        static async Task<bool> Call<T>(Task<GatewayResult<T>> task)
        {
            await task;
            return true;
        }
    }

    [Fact]
    public async Task Status_parses_the_captured_payload()
    {
        var (client, _) = Build(h => h.MapFixture("/status", FixtureFiles.Status));

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Ok, result.Status);
        var status = result.Value!;
        Assert.Equal("claudecode", status.ConnectorMode!.Connector);
        Assert.Equal("observe", status.ConnectorMode.GuardrailMode);
        Assert.Equal("open", status.ConnectorMode.HookFailMode);
        Assert.False(status.ConnectorMode.HookEnforcement);
        Assert.Equal("agent_lifecycle_hooks", status.ConnectorMode.EnforcementSurface);
        Assert.Equal(new[] { "hooks", "otel" }, status.ConnectorMode.Telemetry);
        Assert.False(status.ConnectorMode.HasFailModeMismatch);
        Assert.Single(status.ConnectorModes);
        Assert.Equal(64740, status.Runtime!.Pid);
        Assert.Equal(@"C:\Users\operator\.defenseclaw", status.Runtime.DataDir);
        Assert.Equal("0.8.7", status.Provenance!.BinaryVersion);
        Assert.Equal(164, status.Health!.Connector!.Requests);
    }

    [Fact]
    public async Task Alerts_parse_the_captured_payload_including_finding_attributes()
    {
        var (client, handler) = Build(h => h.MapFixture("/alerts", FixtureFiles.Alerts));

        var result = await client.GetAlertsAsync(limit: 25);

        Assert.Equal(GatewayStatus.Ok, result.Status);
        var alerts = result.Value!;
        Assert.NotEmpty(alerts);

        var first = alerts[0];
        Assert.Equal("0f7aad24-5181-47f9-8743-924b3d4a3977", first.Id);
        Assert.Equal("scan-finding", first.Action);
        Assert.Equal("HIGH", first.Severity);
        Assert.Equal("audit_logger", first.Actor);
        Assert.Equal("CMD-ENV-DUMP", first.RuleId);
        Assert.Equal("Environment variable dump", first.Title);
        Assert.Equal("claudecode:PreToolUse", first.TargetRef);
        Assert.Equal("hook-rules", first.Scanner);
        Assert.Equal(0.8, first.Confidence!.Value, 3);
        Assert.Equal(new[] { "credential" }, first.Tags);
        Assert.Equal(new[] { "sensitive_access" }, first.DataAxes);
        Assert.Equal("$env", first.EvidenceSummary);

        Assert.Contains("limit=25", handler.RequestFor("/alerts")!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_array_payloads_deserialize_to_empty_lists()
    {
        var (client, _) = Build(h => h
            .MapFixture("/mcps", FixtureFiles.Mcps)
            .MapFixture("/enforce/blocked", FixtureFiles.EnforceBlocked)
            .MapFixture("/enforce/allowed", FixtureFiles.EnforceAllowed));

        Assert.Empty((await client.GetMcpsAsync()).Value!);
        Assert.Empty((await client.GetEnforceBlockedAsync()).Value!);
        Assert.Empty((await client.GetEnforceAllowedAsync()).Value!);
    }

    // ----------------------------------------------------------------------------------
    // Go nil slices. The sidecar is written in Go, and `encoding/json` writes a nil slice as
    // `null`, not `[]` (Fixtures/health.json carries "discovered":null). System.Text.Json calls
    // the setter with null after the property initializer ran, so a `= Array.Empty<T>()` default
    // was overwritten and the poll loop's foreach over Connectors threw a NullReferenceException.
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_bare_null_list_body_is_an_empty_list_not_an_error()
    {
        // Go's encoding/json writes a nil slice as `null`; for a list endpoint that is "none".
        var (client, _) = Build(h =>
        {
            h.Map("/alerts", "null");
            h.Map("/enforce/blocked", "null");
        });

        var alerts = await client.GetAlertsAsync();
        var blocked = await client.GetEnforceBlockedAsync();

        Assert.True(alerts.IsOk);
        Assert.Empty(alerts.Value!);
        Assert.True(blocked.IsOk);
        Assert.Empty(blocked.Value!);
    }

    [Fact]
    public async Task A_bare_null_object_body_is_still_an_error()
    {
        var (client, _) = Build(h => h.Map("/health", "null"));

        var health = await client.GetHealthAsync();

        Assert.False(health.IsOk);
    }

    [Fact]
    public async Task Null_connectors_in_health_deserialize_to_an_empty_list()
    {
        var (client, _) = Build(h => h.Map("/health", """{"uptime_ms":5,"connectors":null}"""));

        var health = (await client.GetHealthAsync()).Value!;

        // Assert.Empty enumerates it, which is the operation that used to throw: the monitor and
        // the Overview panel foreach over this list on every poll.
        Assert.NotNull(health.Connectors);
        Assert.Empty(health.Connectors);
    }

    [Fact]
    public async Task Null_connector_modes_and_telemetry_in_status_deserialize_to_empty_lists()
    {
        var (client, _) = Build(h => h.Map(
            "/status",
            """
            {
              "connector_mode": { "connector": "claudecode", "telemetry": null },
              "connector_modes": null
            }
            """));

        var status = (await client.GetStatusAsync()).Value!;

        Assert.NotNull(status.ConnectorModes);
        Assert.Empty(status.ConnectorModes);
        Assert.NotNull(status.ConnectorMode);
        Assert.NotNull(status.ConnectorMode.Telemetry);
        Assert.Empty(status.ConnectorMode.Telemetry);
    }

    [Fact]
    public async Task Null_telemetry_inside_a_connector_mode_entry_is_an_empty_list()
    {
        var (client, _) = Build(h => h.Map(
            "/status",
            """{ "connector_modes": [ { "connector": "claudecode", "telemetry": null } ] }"""));

        var status = (await client.GetStatusAsync()).Value!;

        var mode = Assert.Single(status.ConnectorModes);
        Assert.Equal("claudecode", mode.Connector);
        Assert.Empty(mode.Telemetry);
    }

    [Fact]
    public async Task Absent_list_properties_are_empty_lists_too()
    {
        var (client, _) = Build(h => h
            .Map("/health", "{}")
            .Map("/status", "{}"));

        var health = (await client.GetHealthAsync()).Value!;
        var status = (await client.GetStatusAsync()).Value!;

        Assert.Empty(health.Connectors);
        Assert.Empty(status.ConnectorModes);
    }

    [Fact]
    public async Task Null_elements_inside_a_list_are_dropped()
    {
        // A Go slice of pointers can carry nil entries; a consumer reading connector.Name off one
        // would throw exactly like the null list did.
        var (client, _) = Build(h => h
            .Map("/health", """{ "connectors": [ null, { "name": "claudecode" }, null ] }""")
            .Map("/status", """{ "connector_modes": [ null, { "connector": "claudecode" } ] }"""));

        var health = (await client.GetHealthAsync()).Value!;
        var status = (await client.GetStatusAsync()).Value!;

        Assert.Equal("claudecode", Assert.Single(health.Connectors).Name);
        Assert.Equal("claudecode", Assert.Single(status.ConnectorModes).Connector);
    }

    [Fact]
    public async Task Populated_lists_are_passed_through_untouched()
    {
        var (client, _) = Build(h => h.MapFixture("/status", FixtureFiles.Status));

        var status = (await client.GetStatusAsync()).Value!;

        Assert.Equal(new[] { "hooks", "otel" }, status.ConnectorMode!.Telemetry);
        Assert.Single(status.ConnectorModes);
    }

    [Fact]
    public async Task A_null_alert_id_is_an_empty_string()
    {
        var (client, _) = Build(h => h.Map("/alerts", """[ { "id": null, "severity": "HIGH" } ]"""));

        var alert = Assert.Single((await client.GetAlertsAsync()).Value!);

        Assert.Equal(string.Empty, alert.Id);
        Assert.Equal("HIGH", alert.Severity);
    }

    [Fact]
    public async Task Not_connected_error_payload_maps_to_NotConnected()
    {
        // The live 0.8.7 install answers /skills and /tools/catalog this way until an
        // upstream is configured. It is a degraded subsystem, not a dead sidecar.
        var (client, _) = Build(h => h
            .MapFixture("/skills", FixtureFiles.Skills)
            .MapFixture("/tools/catalog", FixtureFiles.ToolsCatalog));

        var skills = await client.GetSkillsAsync();
        var tools = await client.GetToolsCatalogAsync();

        Assert.Equal(GatewayStatus.NotConnected, skills.Status);
        Assert.Equal("gateway: not connected", skills.ErrorMessage);
        Assert.True(skills.Responded);
        Assert.Null(skills.Value);
        Assert.Equal(GatewayStatus.NotConnected, tools.Status);
    }

    [Fact]
    public async Task Http_401_maps_to_Unauthorized()
    {
        var (client, _) = Build(
            h => h.Map("/status", "{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized),
            token: null);

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Unauthorized, result.Status);
        Assert.Equal(401, result.HttpStatusCode);
        Assert.True(result.Responded);
    }

    [Fact]
    public async Task Connection_refused_maps_to_Unreachable()
    {
        var (client, _) = Build(h => h.MapThrow(
            "/health",
            new HttpRequestException(HttpRequestError.ConnectionError, "connection refused")));

        var result = await client.GetHealthAsync();

        Assert.Equal(GatewayStatus.Unreachable, result.Status);
        Assert.False(result.Responded);
        Assert.Contains("not listening", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(GatewayClient.RefusedMessage, result.ErrorMessage);
    }

    [Fact]
    public async Task Another_transport_failure_is_Unreachable_but_not_the_refused_message()
    {
        var (client, _) = Build(h => h.MapThrow(
            "/health",
            new HttpRequestException(HttpRequestError.NameResolutionError, "no such host")));

        var result = await client.GetHealthAsync();

        Assert.Equal(GatewayStatus.Unreachable, result.Status);
        Assert.NotEqual(GatewayClient.RefusedMessage, result.ErrorMessage);
    }

    [Fact]
    public async Task Server_error_maps_to_Error()
    {
        var (client, _) = Build(h => h.Map("/status", "{}", HttpStatusCode.InternalServerError));

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(500, result.HttpStatusCode);
    }

    [Fact]
    public async Task Malformed_json_maps_to_Error_rather_than_throwing()
    {
        var (client, _) = Build(h => h.Map("/status", "not json at all"));

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
    }

    [Fact]
    public async Task Unknown_fields_are_tolerated_and_kept()
    {
        var (client, _) = Build(h => h.Map(
            "/health",
            """{"uptime_ms":5,"brand_new_field":{"nested":true}}"""));

        var result = await client.GetHealthAsync();

        Assert.Equal(GatewayStatus.Ok, result.Status);
        Assert.Equal(5, result.Value!.UptimeMs);
        Assert.NotNull(result.Value.AdditionalData);
        Assert.True(result.Value.AdditionalData!.ContainsKey("brand_new_field"));
    }

    [Fact]
    public async Task No_token_available_still_sends_the_request_without_auth()
    {
        // The app should surface a real 401 from the gateway rather than refusing to ask.
        var (client, handler) = Build(
            h => h.Map("/status", "{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized),
            token: null);

        var result = await client.GetStatusAsync();

        Assert.Null(handler.RequestFor("/status")!.Headers.Authorization);
        Assert.Equal(GatewayStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Error_message_never_leaks_the_token()
    {
        var (client, _) = Build(h => h.Map("/status", "{\"error\":\"boom\"}", HttpStatusCode.InternalServerError));

        var result = await client.GetStatusAsync();

        Assert.DoesNotContain(Token, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void To_carries_a_failure_across_a_type_change()
    {
        var source = GatewayResult<string>.NotConnected("gateway: not connected", 200);

        var converted = source.To<int>();

        Assert.Equal(GatewayStatus.NotConnected, converted.Status);
        Assert.Equal("gateway: not connected", converted.ErrorMessage);
        Assert.Equal(200, converted.HttpStatusCode);
    }
}
