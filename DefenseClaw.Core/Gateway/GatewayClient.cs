using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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
/// This client is deliberately read-only. Every mutation goes through the CLI so the
/// Activity panel can record exact argv — see <c>Cli.CliRunner</c>.
/// </para>
/// </summary>
public sealed class GatewayClient : IGatewayClient, IDisposable
{
    /// <summary>Mutating requests additionally require this header; any value is accepted.</summary>
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
    private readonly bool _ownsHttpClient;

    /// <param name="httpClient">Must have a BaseAddress, or use <see cref="Create"/>.</param>
    /// <param name="tokenProvider">
    /// Re-evaluated per request so a token that appears after startup (env var set,
    /// .env written) is picked up without recreating the client.
    /// </param>
    public GatewayClient(HttpClient httpClient, Func<SecretValue?>? tokenProvider = null, bool ownsHttpClient = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? (static () => null);
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>Builds a client for the loopback sidecar on <paramref name="port"/>.</summary>
    public static GatewayClient Create(int port, Func<SecretValue?>? tokenProvider = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };

        return new GatewayClient(http, tokenProvider, ownsHttpClient: true);
    }

    public Task<GatewayResult<GatewayHealth>> GetHealthAsync(CancellationToken cancellationToken = default) =>
        GetAsync<GatewayHealth>("health", requiresAuth: false, cancellationToken);

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

    private async Task<GatewayResult<T>> GetAsync<T>(string path, bool requiresAuth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (requiresAuth)
        {
            var token = _tokenProvider();
            if (token is { IsEmpty: false })
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Reveal());
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
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

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return GatewayResult<T>.Unreachable(Describe(ex));
            }

            return Interpret<T>(response.StatusCode, body);
        }
    }

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
            return value is null
                ? GatewayResult<T>.Error("gateway returned an empty body", code)
                : GatewayResult<T>.Ok(value, code);
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

    private static string Describe(HttpRequestException ex) =>
        ex.HttpRequestError == HttpRequestError.ConnectionError
            ? "gateway is not listening (connection refused)"
            : $"gateway unreachable: {ex.Message}";

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
