using System.Net;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Whoever binds the loopback port answers, so the bearer token goes only to the DefenseClaw gateway: the
/// port's owner must be <c>defenseclaw-gateway</c> from the install directory and its <c>/health</c> must
/// parse. Redirects are never followed, and a token that cannot be a header value never escapes as an
/// exception.
/// </summary>
public class GatewayClientPeerTests
{
    private const string Token = "fixture-bearer-token-0123456789";
    private const string Bin = @"C:\fake\install\bin";
    private const string GatewayImage = Bin + @"\defenseclaw-gateway.exe";

    private sealed class MutablePortInspector : IPortOwnerInspector
    {
        public PortOwner? Owner { get; set; }

        public PortOwner? FindListener(int port) => Owner;
    }

    private static PortOwner RealGateway => new(4242, "defenseclaw-gateway", "127.0.0.1", 18970, GatewayImage);

    private static PortOwner SomeRandomServer => new(9001, "SomeRandomServer", "127.0.0.1", 18970, @"C:\tools\server.exe");

    private static DefenseClawPaths InstalledPaths(string dataDirectory) =>
        new(
            dataDirectory: dataDirectory,
            binDirectory: Bin,
            searchPath: Array.Empty<string>(),
            fileExists: p => p.StartsWith(Bin, StringComparison.OrdinalIgnoreCase) || File.Exists(p));

    private static (GatewayClient Client, FakeHttpMessageHandler Handler) Build(
        Func<PortOwnerTrust> verify,
        Action<FakeHttpMessageHandler> configure,
        string? token = Token)
    {
        var handler = new FakeHttpMessageHandler();
        configure(handler);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
        var secret = token is null ? null : new SecretValue(token);
        return (new GatewayClient(http, () => secret, ownsHttpClient: true, verifyPeer: verify), handler);
    }

    private static void Healthy(FakeHttpMessageHandler h) => h
        .MapFixture("/health", FixtureFiles.Health)
        .MapFixture("/status", FixtureFiles.Status)
        .MapFixture("/alerts", FixtureFiles.Alerts);

    // ---- the seam, over a fake handler ----------------------------------------------------------

    [Fact]
    public async Task A_verified_gateway_gets_a_health_probe_first_and_then_the_token()
    {
        var (client, handler) = Build(() => PortOwnerTrust.Gateway, Healthy);

        var result = await client.GetStatusAsync();

        Assert.True(result.IsOk);
        Assert.Equal(new[] { "/health", "/status" }, handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Equal(Token, handler.Requests[1].Headers.Authorization!.Parameter);

        // The probe is remembered: a second call is one request.
        _ = await client.GetAlertsAsync();
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Nothing_listening_reads_as_unreachable_and_sends_nothing()
    {
        var (client, handler) = Build(() => PortOwnerTrust.Unknown, Healthy);

        var result = await client.GetAlertsAsync();

        // Unreachable, as before: the panels' "gateway stopped" fallbacks key off it.
        Assert.Equal(GatewayStatus.Unreachable, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(PortOwnerTrust.Other)]
    [InlineData(PortOwnerTrust.WslRelay)]
    public async Task A_peer_that_is_not_the_gateway_is_refused_without_a_request(PortOwnerTrust trust)
    {
        var (client, handler) = Build(() => trust, Healthy);

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(GatewayClient.PeerRefusedMessage, result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_wsl_relay_is_still_readable_through_health()
    {
        var (client, handler) = Build(() => PortOwnerTrust.WslRelay, Healthy);

        var health = await client.GetHealthAsync();

        Assert.True(health.IsOk);
        Assert.Null(handler.RequestFor("/health")!.Headers.Authorization);
    }

    [Fact]
    public async Task A_health_that_does_not_parse_means_no_token_even_for_the_right_owner()
    {
        var (client, handler) = Build(
            () => PortOwnerTrust.Gateway,
            h => h.Map("/health", "<html>404</html>", HttpStatusCode.NotFound).MapFixture("/status", FixtureFiles.Status));

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(new[] { "/health" }, handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task A_dead_health_endpoint_stays_unreachable_for_the_callers_fallbacks()
    {
        var (client, handler) = Build(
            () => PortOwnerTrust.Gateway,
            h => h.DefaultException = new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var result = await client.GetAlertsAsync();

        Assert.Equal(GatewayStatus.Unreachable, result.Status);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task A_listener_swapped_after_the_last_probe_is_caught_on_the_next_request()
    {
        var trust = PortOwnerTrust.Gateway;
        var (client, handler) = Build(() => trust, Healthy);

        Assert.True((await client.GetStatusAsync()).IsOk);
        var before = handler.Requests.Count;

        trust = PortOwnerTrust.Other;
        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(before, handler.Requests.Count);
    }

    [Fact]
    public async Task A_client_without_a_peer_check_behaves_exactly_as_before()
    {
        var handler = new FakeHttpMessageHandler().MapFixture("/status", FixtureFiles.Status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
        using var client = new GatewayClient(http, () => new SecretValue(Token), ownsHttpClient: true);

        var result = await client.GetStatusAsync();

        Assert.True(result.IsOk);
        Assert.Single(handler.Requests);
        Assert.Equal(Token, handler.Requests[0].Headers.Authorization!.Parameter);
    }

    // ---- tokens that cannot be header values --------------------------------------------------------

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("nul\0inside")]
    [InlineData("tökén-non-ascii")]
    [InlineData("trailing ")]
    public async Task A_token_that_is_not_a_valid_header_value_is_an_error_result_not_an_exception(string token)
    {
        var handler = new FakeHttpMessageHandler().MapFixture("/status", FixtureFiles.Status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") };
        using var client = new GatewayClient(http, () => new SecretValue(token), ownsHttpClient: true);

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(GatewayClient.UnsendableTokenMessage, result.ErrorMessage);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(token.Trim(), result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task A_format_or_operation_exception_out_of_the_send_is_an_error_result_that_does_not_echo_it(Type exceptionType)
    {
        var (client, _) = Build(
            verify: () => PortOwnerTrust.Gateway,
            configure: h => h
                .MapFixture("/health", FixtureFiles.Health)
                .MapThrow("/status", (Exception)Activator.CreateInstance(exceptionType, $"The format of value 'Bearer {Token}' is invalid.")!));

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.DoesNotContain(Token, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(exceptionType.Name, result.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- what a stray listener sees on the wire ---------------------------------------------------------

    [Fact]
    public async Task A_stray_listener_answering_404_never_receives_the_token()
    {
        using var stray = new RawHttpListener(_ => RawHttpListener.Response(404, "{\"error\":\"not found\"}"));
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        var inspector = new MutablePortInspector { Owner = SomeRandomServer with { Port = stray.Port } };
        var paths = InstalledPaths(temp.Path);
        var verifier = new GatewayPeerVerifier(paths, inspector);
        using var client = GatewayClient.Create(stray.Port, () => new SecretValue(Token), verifyPeer: verifier.ForPort(stray.Port));

        var status = await client.GetStatusAsync();
        var alerts = await client.GetAlertsAsync(25);
        var health = await client.GetHealthAsync();

        Assert.False(status.IsOk);
        Assert.False(alerts.IsOk);
        Assert.False(health.IsOk);
        Assert.All(stray.Requests, r => Assert.Null(r.Authorization));
        Assert.DoesNotContain(stray.Requests, r => r.Path.StartsWith("/status", StringComparison.Ordinal) || r.Path.StartsWith("/alerts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_detector_names_the_stranger_and_the_real_gateway_case_still_authenticates()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        var paths = InstalledPaths(temp.Path);
        var inspector = new MutablePortInspector();
        var expectedAuthorization = $"Bearer {Token}";

        using var listener = new RawHttpListener(request => request.Path switch
        {
            "/health" => RawHttpListener.Response(200, FixtureFiles.ReadText(FixtureFiles.Health)),
            "/status" when request.Authorization == expectedAuthorization =>
                RawHttpListener.Response(200, FixtureFiles.ReadText(FixtureFiles.Status)),
            _ => RawHttpListener.Response(401, "{\"error\":\"unauthorized\"}"),
        });

        var verifier = new GatewayPeerVerifier(paths, inspector);
        using var client = GatewayClient.Create(listener.Port, () => new SecretValue(Token), verifyPeer: verifier.ForPort(listener.Port));
        var detector = new InstallStateDetector(paths, client, inspector);

        // 1. Something else owns the port: it is reported as such, and the token stays home.
        inspector.Owner = SomeRandomServer with { Port = listener.Port };
        var stranger = await detector.DetectAsync(listener.Port, client);
        var refused = await client.GetStatusAsync();

        Assert.Equal(PortOwnerTrust.Other, stranger.OwnerTrust);
        Assert.Contains("SomeRandomServer", stranger.Detail, StringComparison.Ordinal);
        Assert.Equal(GatewayStatus.Error, refused.Status);
        Assert.All(listener.Requests, r => Assert.Null(r.Authorization));

        // 2. The real gateway owns it: the same client authenticates.
        inspector.Owner = RealGateway with { Port = listener.Port };
        var real = await detector.DetectAsync(listener.Port, client);
        var status = await client.GetStatusAsync();

        Assert.Equal(PortOwnerTrust.Gateway, real.OwnerTrust);
        Assert.Equal(InstallState.Running, real.State);
        Assert.True(status.IsOk);
        var statusRequest = Assert.Single(listener.Requests, r => r.Path == "/status");
        Assert.Equal(expectedAuthorization, statusRequest.Authorization);
        Assert.All(listener.Requests.Where(r => r.Path == "/health"), r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task A_redirect_from_even_the_real_gateway_is_not_followed_with_the_token()
    {
        using var elsewhere = new RawHttpListener(_ => RawHttpListener.Response(200, "{}"));
        using var listener = new RawHttpListener(request => request.Path switch
        {
            "/health" => RawHttpListener.Response(200, FixtureFiles.ReadText(FixtureFiles.Health)),
            _ => RawHttpListener.Response(302, string.Empty, ("Location", $"http://127.0.0.1:{elsewhere.Port}/status")),
        });
        using var client = GatewayClient.Create(
            listener.Port,
            () => new SecretValue(Token),
            verifyPeer: () => PortOwnerTrust.Gateway);

        var result = await client.GetStatusAsync();

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Contains("302", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(elsewhere.Requests);
    }
}
