using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests;

/// <summary>
/// The gateway client sends <c>Authorization: Bearer …</c> over plain HTTP to 127.0.0.1. <see cref="HttpClient.DefaultProxy"/>
/// honours HTTP_PROXY / ALL_PROXY and the system proxy and does not exempt loopback, so a client that consults it hands the
/// request - header included - to the proxy instead of the gateway. The handler must therefore never use a proxy.
/// </summary>
public class GatewayClientHandlerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public void The_handler_neither_uses_a_proxy_nor_follows_redirects()
    {
        using var handler = GatewayClient.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task A_proxy_that_is_configured_anyway_is_not_asked_to_carry_the_request()
    {
        var proxyListener = new TcpListener(IPAddress.Loopback, 0);
        var targetListener = new TcpListener(IPAddress.Loopback, 0);
        proxyListener.Start();
        targetListener.Start();
        try
        {
            var proxyPort = ((IPEndPoint)proxyListener.LocalEndpoint).Port;
            var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;

            using var handler = GatewayClient.CreateHandler();

            // What the environment (HTTP_PROXY) or the system settings would have supplied.
            handler.Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}");

            using var http = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{targetPort}/"),
                Timeout = Patience,
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, "status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-test-token");

            var proxySaw = AcceptAsync(proxyListener);
            var targetSaw = AcceptAsync(targetListener);

            using var response = await http.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Whichever listener answered is the one that saw the request: it has to be the gateway's, never the proxy's.
            var first = await Task.WhenAny(proxySaw, targetSaw).WaitAsync(Patience);
            Assert.Same(targetSaw, first);

            var seenByTarget = await targetSaw;
            Assert.NotNull(seenByTarget);
            Assert.Contains("synthetic-test-token", seenByTarget, StringComparison.Ordinal);
        }
        finally
        {
            proxyListener.Stop();
            targetListener.Stop();
        }
    }

    /// <summary>Accepts one connection, reads the request head, answers 200 and returns what was read; null if the listener is stopped first.</summary>
    private static async Task<string?> AcceptAsync(TcpListener listener)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var answer = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
            await stream.WriteAsync(answer);
            return text;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }
}
