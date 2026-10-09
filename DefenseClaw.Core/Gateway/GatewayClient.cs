using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Core.Gateway;

/// <summary>Read surface of the local sidecar REST API.</summary>
public interface IGatewayClient
{
    Task<GatewayResult<GatewayHealth>> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<GatewayResult<GatewayStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<GatewayResult<IReadOnlyList<GatewayAlert>>> GetAlertsAsync(int? limit = null, CancellationToken cancellationToken = default);

    Task<GatewayResult<IReadOnlyList<SkillEntry>>> GetSkillsAsync(CancellationToken cancellationToken = default);

    Task<GatewayResult<IReadOnlyList<McpEntry>>> GetMcpsAsync(CancellationToken cancellationToken = default);

    Task<GatewayResult<IReadOnlyList<ToolCatalogEntry>>> GetToolsCatalogAsync(CancellationToken cancellationToken = default);

    Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceBlockedAsync(CancellationToken cancellationToken = default);

    Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceAllowedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Async client for <c>http://127.0.0.1:{gateway.api_port}</c>.
/// <para>
/// Auth, as measured against a live 0.8.7 install (the docs are wrong): <c>GET /health</c>
/// is the <em>only</em> unauthenticated endpoint. Everything else 401s without
/// <c>Authorization: Bearer &lt;token&gt;</c>. The token comes from the ladder in
/// <see cref="TokenResolver"/> and is never logged or surfaced in
/// <see cref="GatewayResult{T}.ErrorMessage"/>.
/// </para>
/// <para>
/// <b>Who gets the token.</b> Whoever binds the loopback port answers, so when the client is
/// built with a <c>verifyPeer</c> check it sends the token — and issues any authenticated
/// request at all — only if that check passes <i>and</i> the last <c>/health</c> through this
/// client parsed as a <see cref="GatewayHealth"/> (probed on the spot when there has been none).
/// Otherwise the call ends without a request leaving: <see cref="GatewayStatus.Unreachable"/> when
/// nothing is listening (so the panels' fallbacks still fire), <see cref="GatewayStatus.Error"/>
/// for a listener that is not the gateway. <see cref="Create"/> never follows redirects: a 3xx from a foreign listener must not
/// be a way to carry the header elsewhere. A client without a <c>verifyPeer</c> keeps the old
/// unconditional behaviour, which is what the unit tests over a fake handler want.
/// </para>
/// <para>
/// This client is deliberately read-only. Every mutation goes through the CLI so the
/// Activity panel can record exact argv — see <c>Cli.CliRunner</c>.
/// </para>
/// </summary>
public sealed class GatewayClient : IGatewayClient, IDisposable
{
    /// <summary>Mutating requests require this header (any non-empty value); this client sends it on every request, reads included.</summary>
    public const string ClientHeaderName = "X-DefenseClaw-Client";

    public const string DefaultClientHeaderValue = "defenseclaw-win";

    /// <summary>Marker the sidecar returns when a subsystem has no upstream wired up.</summary>
    public const string NotConnectedMarker = "not connected";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly Func<SecretValue?> _tokenProvider;
    private readonly Func<PortOwnerTrust>? _verifyPeer;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// True while the last <c>/health</c> through this client answered and parsed. Half of the
    /// "may this peer have the token" check; see the type documentation.
    /// </summary>
    private volatile bool _healthConfirmed;

    /// <param name="httpClient">Must have a BaseAddress, or use <see cref="Create"/>.</param>
    /// <param name="tokenProvider">
    /// Re-evaluated per request so a token that appears after startup (env var set,
    /// .env written) is picked up without recreating the client.
    /// </param>
    /// <param name="verifyPeer">
    /// The peer-verification seam: what the process listening on this client's port is, as far as
    /// the bearer token goes (see <see cref="GatewayPeerVerifier"/>). Only
    /// <see cref="PortOwnerTrust.Gateway"/> lets a request through; <see cref="PortOwnerTrust.Unknown"/>
    /// (nothing listening) reads as unreachable, anything else as a refused peer. Evaluated on
    /// every authenticated request, so a listener replaced since the last poll is not believed.
    /// Null means no check.
    /// </param>
    public GatewayClient(
        HttpClient httpClient,
        Func<SecretValue?>? tokenProvider = null,
        bool ownsHttpClient = false,
        Func<PortOwnerTrust>? verifyPeer = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? (static () => null);
        _ownsHttpClient = ownsHttpClient;
        _verifyPeer = verifyPeer;
    }

    /// <summary>
    /// The handler behind <see cref="Create"/>. <b>No redirects:</b> the token rides the request, and a 3xx from whatever answered
    /// must not be able to carry it to another address (or downgrade it to another scheme). <b>No proxy:</b> this is a plain-HTTP
    /// request to this machine, and <see cref="HttpClient.DefaultProxy"/> (HTTP_PROXY / ALL_PROXY, or the system proxy) does not
    /// exempt loopback - measured: with HTTP_PROXY set, a request for <c>http://127.0.0.1:PORT/</c> goes to the proxy, bearer header
    /// included, in the clear, and never reaches the gateway.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseProxy = false };

    /// <summary>Builds a client for the loopback sidecar on <paramref name="port"/>.</summary>
    public static GatewayClient Create(
        int port,
        Func<SecretValue?>? tokenProvider = null,
        TimeSpan? timeout = null,
        Func<PortOwnerTrust>? verifyPeer = null)
    {
        var handler = CreateHandler();
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };

        return new GatewayClient(http, tokenProvider, ownsHttpClient: true, verifyPeer);
    }

    public async Task<GatewayResult<GatewayHealth>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<GatewayHealth>("health", requiresAuth: false, cancellationToken).ConfigureAwait(false);
        _healthConfirmed = result.IsOk;
        return result;
    }

    public Task<GatewayResult<GatewayStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<GatewayStatusResponse>("status", requiresAuth: true, cancellationToken);

    public Task<GatewayResult<IReadOnlyList<GatewayAlert>>> GetAlertsAsync(int? limit = null, CancellationToken cancellationToken = default)
    {
        var path = limit is > 0
            ? $"alerts?limit={limit.Value.ToString(CultureInfo.InvariantCulture)}"
            : "alerts";

        return GetAsync<IReadOnlyList<GatewayAlert>>(path, requiresAuth: true, cancellationToken);
    }

    public Task<GatewayResult<IReadOnlyList<SkillEntry>>> GetSkillsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<SkillEntry>>("skills", requiresAuth: true, cancellationToken);

    public Task<GatewayResult<IReadOnlyList<McpEntry>>> GetMcpsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<McpEntry>>("mcps", requiresAuth: true, cancellationToken);

    public Task<GatewayResult<IReadOnlyList<ToolCatalogEntry>>> GetToolsCatalogAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ToolCatalogEntry>>("tools/catalog", requiresAuth: true, cancellationToken);

    public Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceBlockedAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<EnforcementEntry>>("enforce/blocked", requiresAuth: true, cancellationToken);

    public Task<GatewayResult<IReadOnlyList<EnforcementEntry>>> GetEnforceAllowedAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<EnforcementEntry>>("enforce/allowed", requiresAuth: true, cancellationToken);

    /// <summary>
    /// Escape hatch for endpoints whose shape drifts between 0.8.x releases: returns the
    /// parsed document with no model mapping.
    /// </summary>
    public Task<GatewayResult<JsonDocument>> GetRawJsonAsync(string path, bool requiresAuth = true, CancellationToken cancellationToken = default) =>
        GetAsync<JsonDocument>(path, requiresAuth, cancellationToken);

    /// <summary>
    /// <see cref="GetRawJsonAsync"/> for an answer that grows with what the machine holds (the AI usage report lists every signal of the
    /// last scan): the body is read as it arrives and the read stops at <paramref name="maxBytes"/>. An answer that says it is longer
    /// (<c>Content-Length</c>) is not read at all, and one that turns out longer is dropped; both end as <see cref="GatewayStatus.Error"/>
    /// naming the limit, never as a half-read document. The read has the client's timeout too: <see cref="HttpClient.Timeout"/> ends
    /// with the headers when the body is streamed, so the body gets the same budget on its own.
    /// </summary>
    public Task<GatewayResult<JsonDocument>> GetBoundedJsonAsync(string path, int maxBytes, bool requiresAuth = true, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        return GetAsync<JsonDocument>(path, requiresAuth, cancellationToken, maxBytes);
    }

    /// <summary>The <see cref="GatewayResult{T}.ErrorMessage"/> of an answer longer than the limit a bounded read was given.</summary>
    public static string TooLargeMessage(int maxBytes) =>
        "the gateway's answer is larger than " + maxBytes.ToString(CultureInfo.InvariantCulture) + " bytes, so it was not read";

    private async Task<GatewayResult<T>> GetAsync<T>(string path, bool requiresAuth, CancellationToken cancellationToken, int? maxBodyBytes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Every request names this client. The gateway's CSRF gate (internal/gateway/api.go apiCSRFProtect, at DefenseClaw source commit
        // 95159fd) rejects a mutating request without a non-empty X-DefenseClaw-Client and lets GET through without it, so this client - which
        // only ever GETs - works either way; sending it always means a gate that is tightened to cover reads does not break the app, and the
        // gateway ignores a header it does not look at (any value is accepted: it is a presence check). /health is sent it too: harmless.
        request.Headers.TryAddWithoutValidation(ClientHeaderName, DefaultClientHeaderValue);

        if (requiresAuth)
        {
            if (await RefusePeerAsync<T>(cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            var token = _tokenProvider();
            if (token is { IsEmpty: false })
            {
                var plain = token.Reveal();
                if (!IsHeaderSafe(plain))
                {
                    // Not sent, and not echoed: FormatException's own message quotes the value.
                    return GatewayResult<T>.Error(UnsendableTokenMessage);
                }

                try
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plain);
                }
                catch (FormatException)
                {
                    return GatewayResult<T>.Error(UnsendableTokenMessage);
                }
            }
        }

        HttpResponseMessage response;
        try
        {
            // A bounded read takes the headers first and streams the body itself (ReadBoundedAsync); an unbounded one lets HttpClient buffer it.
            response = await _http.SendAsync(
                    request,
                    maxBodyBytes is null ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return GatewayResult<T>.Unreachable(Describe(ex));
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as a cancellation.
            return GatewayResult<T>.Unreachable($"gateway timed out: {ex.Message}");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            // A request the client refused to build or send (a header value HttpClient rejects
            // after the fact, a malformed URI). Not a dead sidecar, and the text can quote the
            // header, so it is not passed on.
            return GatewayResult<T>.Error($"the request could not be sent ({ex.GetType().Name})");
        }

        using (response)
        {
            string body;
            try
            {
                if (maxBodyBytes is { } limit)
                {
                    if (await ReadBoundedAsync(response, limit, cancellationToken).ConfigureAwait(false) is not { } bounded)
                    {
                        return GatewayResult<T>.Error(TooLargeMessage(limit), (int)response.StatusCode);
                    }

                    body = bounded;
                }
                else
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex)
            {
                return GatewayResult<T>.Unreachable(Describe(ex));
            }
            catch (IOException ex)
            {
                // The connection dropped while the body was streaming (only a bounded read streams it itself).
                return GatewayResult<T>.Unreachable($"gateway unreachable: {ex.Message}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return GatewayResult<T>.Unreachable("gateway timed out while sending its answer");
            }

            return Interpret<T>(response.StatusCode, body);
        }
    }

    /// <summary>
    /// The body as text, or null when it is longer than <paramref name="maxBytes"/>: an announced length over the limit is refused before a
    /// byte is read, and an unannounced (chunked) body is dropped the moment it passes it. Time-boxed to the client's timeout.
    /// </summary>
    private async Task<string?> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared > maxBytes)
        {
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            budget.CancelAfter(_http.Timeout);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream((int)Math.Min(declared ?? 16 * 1024, maxBytes));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), budget.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public const string PeerRefusedMessage =
        "the process listening on the gateway port is not the DefenseClaw gateway, so nothing was sent";

    public const string HealthRefusedMessage =
        "the gateway's /health did not answer as expected, so nothing was sent";

    public const string UnsendableTokenMessage =
        "the gateway token contains characters that cannot be sent in an HTTP header (whitespace, control or non-ASCII); it was not sent";

    /// <summary>
    /// The two halves of "this peer may have the token": the owner check the caller supplied, and
    /// a <c>/health</c> that parsed. Returns null when both hold — send the request — and otherwise
    /// the result to hand back <i>instead of</i> sending it. Probes <c>/health</c> itself when
    /// there has been none, so a panel that calls before the monitor's first poll still
    /// authenticates.
    /// <para>
    /// Nothing listening (or nothing identifiable) is <see cref="GatewayStatus.Unreachable"/>, as
    /// it always was, so the panels' "gateway stopped, read from SQLite" fallbacks still fire.
    /// </para>
    /// </summary>
    private async Task<GatewayResult<T>?> RefusePeerAsync<T>(CancellationToken cancellationToken)
    {
        if (_verifyPeer is null)
        {
            return null;
        }

        // Cheap and local first: no request goes to a listener that is not the gateway at all. On the
        // pool: the lookup walks the listener table and opens the owning process, which is not work
        // for a UI thread that called this from a panel.
        var trust = await Task.Run(_verifyPeer, cancellationToken).ConfigureAwait(false);
        switch (trust)
        {
            case PortOwnerTrust.Gateway:
                break;
            case PortOwnerTrust.Unknown:
                return GatewayResult<T>.Unreachable("gateway is not listening");
            default:
                return GatewayResult<T>.Error(PeerRefusedMessage);
        }

        if (_healthConfirmed)
        {
            return null;
        }

        var probe = await GetHealthAsync(cancellationToken).ConfigureAwait(false);
        if (probe.IsOk)
        {
            return null;
        }

        return probe.Status == GatewayStatus.Unreachable
            ? GatewayResult<T>.Unreachable(probe.ErrorMessage)
            : GatewayResult<T>.Error(HealthRefusedMessage);
    }

    /// <summary>
    /// True when <paramref name="token"/> is nothing but visible ASCII — what an HTTP header value
    /// can carry unmangled. Anything else (an embedded space, a control character, non-ASCII)
    /// either throws inside HttpClient or is mangled into a 401 or a mislabelled "unreachable".
    /// </summary>
    private static bool IsHeaderSafe(string token)
    {
        foreach (var c in token)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return token.Length > 0;
    }

    /// <summary>An empty array for an <c>IReadOnlyList&lt;X&gt;</c> payload type; null otherwise.</summary>
    private static object? EmptyListFor(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
            ? Array.CreateInstance(type.GetGenericArguments()[0], 0)
            : null;

    /// <summary>Maps an HTTP status plus body onto a <see cref="GatewayResult{T}"/>.</summary>
    internal static GatewayResult<T> Interpret<T>(HttpStatusCode statusCode, string body)
    {
        var code = (int)statusCode;
        var error = TryReadErrorPayload(body);

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return GatewayResult<T>.Unauthorized(error ?? "unauthorized", code);
        }

        // A 200 carrying {"error": "gateway: not connected"} is a degraded subsystem,
        // not a dead sidecar — the banner must say so.
        if (error is not null)
        {
            return error.Contains(NotConnectedMarker, StringComparison.OrdinalIgnoreCase)
                ? GatewayResult<T>.NotConnected(error, code)
                : GatewayResult<T>.Error(error, code);
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            return GatewayResult<T>.Unauthorized("forbidden", code);
        }

        if (code is < 200 or > 299)
        {
            return GatewayResult<T>.Error($"gateway returned HTTP {code}", code);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, JsonOptions);
            if (value is not null)
            {
                return GatewayResult<T>.Ok(value, code);
            }

            // The gateway is Go: encoding/json writes a nil slice as `null`, so for the list
            // endpoints a bare `null` is an empty list, not a failure. For an object payload
            // (health, status) `null` still means the answer carried nothing usable.
            return EmptyListFor(typeof(T)) is T empty
                ? GatewayResult<T>.Ok(empty, code)
                : GatewayResult<T>.Error("gateway returned an empty body", code);
        }
        catch (JsonException ex)
        {
            return GatewayResult<T>.Error($"could not parse gateway response: {ex.Message}", code);
        }
    }

    /// <summary>
    /// Returns the <c>error</c> string when the body is an error envelope, else null.
    /// Array bodies (the normal success shape for listings) are never error envelopes.
    /// </summary>
    private static string? TryReadErrorPayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            return error.ValueKind == JsonValueKind.String ? error.GetString() : error.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <see cref="GatewayResult{T}.ErrorMessage"/> of an <see cref="GatewayStatus.Unreachable"/> result whose cause was a refused connection (nothing listening), as opposed to a timeout or another transport failure.</summary>
    public const string RefusedMessage = "gateway is not listening (connection refused)";

    private static string Describe(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.ConnectionError
            ? RefusedMessage
            : $"gateway unreachable: {ex.Message}";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
