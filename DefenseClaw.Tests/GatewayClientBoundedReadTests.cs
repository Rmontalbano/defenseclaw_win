using System.Net;
using System.Text;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="GatewayClient.GetBoundedJsonAsync"/> (CUST-310), the read the AI Discovery panel makes of <c>GET /api/v1/ai-usage</c>: an
/// answer that grows with what the machine holds is read as it arrives and dropped at a limit, in size and in time, and everything else
/// about the call - the bearer token, the client header, the refusal to talk to a listener that is not the gateway, the way a status
/// becomes a <see cref="GatewayStatus"/> - is exactly what every other read does. Handlers are in memory; nothing here opens a socket.
/// </summary>
public class GatewayClientBoundedReadTests
{
    private const string Token = "fixture-bearer-token-0123456789";
    private const string Route = "api/v1/ai-usage";

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (GatewayClient Client, Scripted Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TimeSpan? timeout = null,
        Func<PortOwnerTrust>? verifyPeer = null)
    {
        var handler = new Scripted(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/"), Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return (new GatewayClient(http, () => new SecretValue(Token), ownsHttpClient: true, verifyPeer), handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A body of a known length that must not be read at all: the length is announced and reading it fails the test.</summary>
    private sealed class AnnouncedContent(long length) : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("the body should not have been read");
        }

        protected override bool TryComputeLength(out long announced)
        {
            announced = length;
            return true;
        }
    }

    /// <summary>A stream with no end and no length, the way a chunked answer that never stops looks to the reader.</summary>
    private sealed class EndlessStream : Stream
    {
        public long Produced { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            Produced += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill((byte)'a');
            Produced += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body whose first byte never arrives.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- what is read ----

    [Fact]
    public async Task An_answer_under_the_limit_is_read_as_a_document_with_the_token_and_the_client_header_on_it()
    {
        var (client, handler) = Build(_ => Json("""{"enabled":true,"signals":[]}"""));

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.True(result.IsOk, result.ErrorMessage);
        using var document = result.Value!;
        Assert.True(document.RootElement.GetProperty("enabled").GetBoolean());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/ai-usage", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal(GatewayClient.DefaultClientHeaderValue, request.Headers.GetValues(GatewayClient.ClientHeaderName).Single());
    }

    [Fact]
    public async Task An_answer_exactly_as_long_as_the_limit_is_read_and_one_byte_more_is_not()
    {
        const string body = """{"enabled":true,"signals":[]}""";
        var exact = Encoding.UTF8.GetByteCount(body);

        var (client, _) = Build(_ => Json(body));
        var fits = await client.GetBoundedJsonAsync(Route, maxBytes: exact);
        Assert.True(fits.IsOk, fits.ErrorMessage);
        fits.Value!.Dispose();

        var (tight, _) = Build(_ => Json(body));
        var over = await tight.GetBoundedJsonAsync(Route, maxBytes: exact - 1);
        Assert.Equal(GatewayStatus.Error, over.Status);
        Assert.Equal(GatewayClient.TooLargeMessage(exact - 1), over.ErrorMessage);
    }

    [Fact]
    public async Task A_byte_order_mark_in_front_of_the_json_does_not_stop_it_being_read()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("""{"enabled":false}""")).ToArray();
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.True(result.IsOk, result.ErrorMessage);
        using var document = result.Value!;
        Assert.False(document.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Multibyte_text_is_decoded_as_utf8()
    {
        var (client, _) = Build(_ => Json("""{"enabled":true,"owner":"Café Notes — 日本語"}"""));

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        using var document = result.Value!;
        Assert.Equal("Café Notes — 日本語", document.RootElement.GetProperty("owner").GetString());
    }

    // ---- what is not read ----

    [Fact]
    public async Task An_answer_that_announces_a_length_over_the_limit_is_refused_without_a_byte_of_it_being_read()
    {
        var content = new AnnouncedContent(length: 5_000_000);
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 4 * 1024 * 1024);

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(GatewayClient.TooLargeMessage(4 * 1024 * 1024), result.ErrorMessage);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Null(result.Value);
        Assert.False(content.WasRead);
    }

    [Fact]
    public async Task An_answer_with_no_announced_length_is_dropped_the_moment_it_passes_the_limit()
    {
        var endless = new EndlessStream();
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(endless) });

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 100_000);

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(GatewayClient.TooLargeMessage(100_000), result.ErrorMessage);

        // It stopped within a buffer of the limit: an unbounded read of this body would never have returned.
        Assert.InRange(endless.Produced, 100_000, 100_000 + (64 * 1024));
    }

    [Fact]
    public async Task A_limit_below_one_byte_is_a_mistake_of_the_caller_not_a_result()
    {
        var (client, handler) = Build(_ => Json("{}"));

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetBoundedJsonAsync(Route, maxBytes: 0));

        Assert.Empty(handler.Requests);
    }

    // ---- the same statuses as every other read ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GatewayStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, GatewayStatus.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, GatewayStatus.Error)]
    [InlineData(HttpStatusCode.NotFound, GatewayStatus.Error)]
    public async Task A_status_other_than_success_is_interpreted_as_it_is_for_any_read(HttpStatusCode http, GatewayStatus expected)
    {
        var (client, _) = Build(_ => Json("404 page not found", http));

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.Equal(expected, result.Status);
        Assert.Equal((int)http, result.HttpStatusCode);
    }

    [Fact]
    public async Task An_error_envelope_that_says_not_connected_is_not_connected()
    {
        var (client, _) = Build(_ => Json("""{"error":"gateway: not connected"}""", HttpStatusCode.BadGateway));

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.Equal(GatewayStatus.NotConnected, result.Status);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_an_error_naming_the_parse_and_not_a_document()
    {
        var (client, _) = Build(_ => Json("<html>proxy page</html>"));

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.StartsWith("could not parse gateway response", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_refused_connection_is_unreachable()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("refused", null, HttpStatusCode.ServiceUnavailable));

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.Equal(GatewayStatus.Unreachable, result.Status);
    }

    // ---- time ----

    [Fact]
    public async Task A_body_that_never_arrives_ends_as_unreachable_at_the_clients_timeout()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }, timeout: TimeSpan.FromMilliseconds(300));

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.Equal(GatewayStatus.Unreachable, result.Status);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_that_cancels_gets_the_cancellation_and_not_a_timeout_result()
    {
        using var source = new CancellationTokenSource();
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });

        var pending = client.GetBoundedJsonAsync(Route, maxBytes: 1024, cancellationToken: source.Token);
        await source.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---- who gets the token ----

    [Fact]
    public async Task A_listener_that_is_not_the_gateway_gets_no_request_from_a_bounded_read_either()
    {
        var (client, handler) = Build(_ => Json("{}"), verifyPeer: () => PortOwnerTrust.Other);

        var result = await client.GetBoundedJsonAsync(Route, maxBytes: 1024);

        Assert.Equal(GatewayStatus.Error, result.Status);
        Assert.Equal(GatewayClient.PeerRefusedMessage, result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_unbounded_reads_still_read_whatever_length_they_are_given()
    {
        // The default read is unchanged: a 6 MiB answer comes back whole.
        var padding = new string('x', 6 * 1024 * 1024);
        var (client, _) = Build(_ => Json("{\"pad\":\"" + padding + "\"}"));

        var result = await client.GetRawJsonAsync(Route);

        Assert.True(result.IsOk, result.ErrorMessage);
        using var document = result.Value!;
        Assert.Equal(padding.Length, document.RootElement.GetProperty("pad").GetString()!.Length);
    }
}
