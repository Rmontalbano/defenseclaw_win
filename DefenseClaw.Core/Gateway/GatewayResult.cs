namespace DefenseClaw.Core.Gateway;

/// <summary>
/// Outcome of a gateway call. The app's degraded-mode banner keys off these: only
/// <see cref="Unreachable"/> means "sidecar is down", while <see cref="NotConnected"/>
/// means the sidecar answered but the backing subsystem is not wired up.
/// </summary>
public enum GatewayStatus
{
    /// <summary>2xx with a body we could deserialize.</summary>
    Ok = 0,

    /// <summary>HTTP 401 — no bearer token, or the wrong one.</summary>
    Unauthorized,

    /// <summary>Body was <c>{"error":"gateway: not connected"}</c>. Sidecar alive, feature not.</summary>
    NotConnected,

    /// <summary>Connection refused / DNS / timeout. The sidecar is not listening.</summary>
    Unreachable,

    /// <summary>Any other failure: 5xx, unexpected error payload, malformed JSON.</summary>
    Error,
}

/// <summary>Typed envelope around a gateway response.</summary>
public sealed record GatewayResult<T>
{
    private GatewayResult(GatewayStatus status, T? value, string? errorMessage, int? httpStatusCode)
    {
        Status = status;
        Value = value;
        ErrorMessage = errorMessage;
        HttpStatusCode = httpStatusCode;
    }

    public GatewayStatus Status { get; }

    public T? Value { get; }

    /// <summary>Server-supplied or client-side diagnostic. Never contains the bearer token.</summary>
    public string? ErrorMessage { get; }

    public int? HttpStatusCode { get; }

    public bool IsOk => Status == GatewayStatus.Ok;

    /// <summary>True when the sidecar answered at all — i.e. not <see cref="GatewayStatus.Unreachable"/>.</summary>
    public bool Responded => Status != GatewayStatus.Unreachable;

    public static GatewayResult<T> Ok(T value, int? httpStatusCode = 200) =>
        new(GatewayStatus.Ok, value, null, httpStatusCode);

    public static GatewayResult<T> Unauthorized(string? message = null, int? httpStatusCode = 401) =>
        new(GatewayStatus.Unauthorized, default, message ?? "unauthorized", httpStatusCode);

    public static GatewayResult<T> NotConnected(string? message = null, int? httpStatusCode = null) =>
        new(GatewayStatus.NotConnected, default, message ?? "gateway: not connected", httpStatusCode);

    public static GatewayResult<T> Unreachable(string? message = null) =>
        new(GatewayStatus.Unreachable, default, message ?? "gateway unreachable", null);

    public static GatewayResult<T> Error(string message, int? httpStatusCode = null) =>
        new(GatewayStatus.Error, default, message, httpStatusCode);

    /// <summary>Carries a non-Ok status across a type change without restating the reason.</summary>
    public GatewayResult<TOther> To<TOther>() =>
        new GatewayResult<TOther>(Status, default, ErrorMessage, HttpStatusCode);

    public T ValueOr(T fallback) => IsOk && Value is not null ? Value : fallback;
}
