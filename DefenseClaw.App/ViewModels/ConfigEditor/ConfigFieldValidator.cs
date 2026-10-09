using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.RegularExpressions;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>How serious a field's validation result is. Only <see cref="Error"/> turns Review &amp; Save off.</summary>
public enum FieldSeverity
{
    None = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// What <see cref="ConfigFieldValidator"/> says about one field's value: nothing, a warning, or an error. The message is a fixed sentence
/// about the <i>kind</i> of problem and never carries the value, so a secret typed into the wrong box is not echoed back by its own warning.
/// </summary>
public sealed record FieldValidation(FieldSeverity Severity, string Message)
{
    public static FieldValidation Ok { get; } = new(FieldSeverity.None, string.Empty);

    public bool IsOk => Severity == FieldSeverity.None;

    public bool IsWarning => Severity == FieldSeverity.Warning;

    public bool IsError => Severity == FieldSeverity.Error;

    /// <summary>The line shown under the field. The severity is a word as well as a colour, so colour is never the only carrier.</summary>
    public string DisplayText => Severity switch
    {
        FieldSeverity.Error => "Error: " + Message,
        FieldSeverity.Warning => "Warning: " + Message,
        _ => string.Empty,
    };

    /// <summary>
    /// The same finding for a value the operator did not change. A value that is already in config.yaml is never what blocks a save (the
    /// pin's <c>blocking_validation_errors</c> and the Mac's <c>firstValidationError</c> look at changed fields only), so an error becomes a
    /// warning that says so, and anything milder is unchanged.
    /// </summary>
    public FieldValidation AsExistingValue() =>
        IsError ? new FieldValidation(FieldSeverity.Warning, "already in config.yaml, kept as is: " + Message) : this;
}

/// <summary>
/// The Config editor's field checks: <c>validate_config_field</c> of the 0.8.10 TUI (<c>tui/services/setup_state.py:479-524</c>), rule for
/// rule and in its order, with the same wording. A field is checked by what its key says it is (<c>port</c>, <c>timeout</c>, <c>url</c>,
/// <c>_env</c>, <c>dedup_window</c>, <c>tls_skip_verify</c>) and by its kind, exactly as the TUI's Setup config panel does; that panel
/// lists a fixed catalogue, this form lists every key the file has, so the places where a key-name match would reach a field the TUI
/// never showed are narrowed and each one is listed on its rule below.
/// <para>
/// <b>What blocks.</b> Errors block Review &amp; Save; warnings (<c>tls_skip_verify</c> on, an uncommon URL scheme, a value that looks like a
/// secret) never do. Only a value the operator changed can block (<see cref="FieldValidation.AsExistingValue"/>).
/// </para>
/// <para>
/// <b>Sources.</b> The 0.8.10 TUI is the baseline. Three details come from the other two references: the Mac 1.1.26
/// (<c>ConfigEditorDefinitions.swift:77-130</c>) and the pinned source (commit 95159fd, <c>setup_state.py:594-647</c>) both allow port 0 for
/// <c>openshell.ingress_port</c> / <c>openshell.egress_port</c> (0 derives it from <c>gateway.api_port</c>) and both refuse a negative
/// <c>openshell.*</c> integer; the pin returns early for a bool, int or choice that is empty and was empty (<c>:600-602</c>, "unset: the
/// runtime default applies"). None of the three can make 0.8.10 refuse something it accepted, so they are not gated.
/// </para>
/// </summary>
public static partial class ConfigFieldValidator
{
    private const string UnsetLabel = "(blank)";

    /// <summary>The words in a key that make an integer non-negative (<c>setup_state.py:497</c>).</summary>
    private static readonly string[] NonNegativeMarkers = { "timeout", "interval", "retries", "max_" };

    /// <summary>Key fragments that name a secret (<c>_is_secret_name</c>, <c>setup_state.py:926-931</c>).</summary>
    private static readonly string[] SecretNameMarkers = { "password", "secret", "token", "api_key", "apikey", "access_key", "private_key" };

    /// <summary>Key fragments that name a URL (<c>_looks_like_url_field</c>, <c>setup_state.py:934-935</c>).</summary>
    private static readonly string[] UrlMarkers = { "url", "endpoint", "api_base", "base_url" };

    /// <summary>The prefixes that mark a value as a credential (<c>looks_like_secret_value</c>, <c>setup_state.py:593</c>).</summary>
    private static readonly string[] SecretPrefixes = { "sk-", "ghp_", "gho_", "ghs_", "AIza", "AKIA", "ASIA", "eyJ" };

    private const string SecretGuidance =
        "Keep secrets out of config.yaml: store it with `defenseclaw keys set <ENV_NAME>` (it is kept in .env) and enter only that name here.";

    /// <summary>
    /// Checks one field.
    /// </summary>
    /// <param name="key">The full dotted path (<c>gateway.api_port</c>): the TUI matches on its own dotted keys, so does this.</param>
    /// <param name="kind">What the control is. <see cref="FormFieldKind.Secret"/> is the TUI's <c>password</c>.</param>
    /// <param name="value">The value as text (a bool as <c>true</c>/<c>false</c>, an integer in invariant digits). Trimmed, as Python's <c>strip()</c>.</param>
    /// <param name="original">The text the field had when it was loaded; only its emptiness matters here (<c>setup_state.py</c> pin <c>:600</c>).</param>
    /// <param name="options">The allowed values of a <see cref="FormFieldKind.Choice"/>, or null.</param>
    public static FieldValidation Validate(
        string key,
        FormFieldKind kind,
        string? value,
        string? original = null,
        IReadOnlyList<string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        var text = (value ?? string.Empty).Trim();

        // Unset in config.yaml and still unset: the runtime default applies (pin setup_state.py:600-602).
        if (text.Length == 0 && string.IsNullOrWhiteSpace(original) && kind is FormFieldKind.Bool or FormFieldKind.Int or FormFieldKind.Choice)
        {
            return FieldValidation.Ok;
        }

        if (kind == FormFieldKind.Bool && text is not ("true" or "false"))
        {
            return Error("expected true or false");
        }

        if (kind == FormFieldKind.Choice && options is { Count: > 0 } && !options.Contains(text, StringComparer.Ordinal))
        {
            // The TUI joins the options as they are; a blank one would print as a bare comma.
            return Error("choose one of: " + string.Join(", ", options.Select(o => o.Length == 0 ? UnsetLabel : o)));
        }

        if (kind == FormFieldKind.Int)
        {
            if (!TryParseInteger(text, out var number))
            {
                return Error("expected an integer");
            }

            var derivedPort = key is "openshell.ingress_port" or "openshell.egress_port";
            if (IsPortKey(key) && (number < (derivedPort ? 0 : 1) || number > 65535))
            {
                return Error(derivedPort ? "port must be between 0 and 65535" : "port must be between 1 and 65535");
            }

            if ((key.StartsWith("openshell.", StringComparison.Ordinal) || NonNegativeMarkers.Any(m => key.Contains(m, StringComparison.Ordinal))) && number < 0)
            {
                return Error("value must be zero or greater");
            }
        }

        // A name that is an env var NAME. The TUI also tests the label for " Env"; this form derives its labels from the keys
        // ("Include Env Var Names" for include_env_var_names, a bool), so the label test would turn unrelated fields into names.
        var isEnvName = kind == FormFieldKind.EnvName || (kind == FormFieldKind.String && IsEnvNameKey(key));
        if (isEnvName && text.Length > 0 && !LooksLikeEnvName(text))
        {
            return LooksLikeSecretValue(text)
                ? Warning("this looks like a secret value, not an env var name. " + SecretGuidance)
                : Error("env var names must match A-Z, 0-9, and underscores");
        }

        // The URL rule is for text. The TUI applies it to every kind; here it would turn `endpoint_timeout_ms: 5000` (an integer) or an
        // `*_url_env` key (a variable NAME) into "expected a URL".
        if (kind == FormFieldKind.String && !isEnvName && text.Length > 0 && LooksLikeUrlField(key))
        {
            if (IsOtlpEndpointField(key) && !text.Contains("://", StringComparison.Ordinal))
            {
                return ValidateHostPort(text) ? FieldValidation.Ok : Error("expected a URL with scheme and host or host:port");
            }

            if (!TryParseUrl(text, out var url) || url.Scheme.Length == 0 || url.Netloc.Length == 0 || url.Host.Length == 0)
            {
                return Error("expected a URL with scheme and host");
            }

            if (!string.IsNullOrEmpty(url.Username) || !string.IsNullOrEmpty(url.Password))
            {
                return Error("URL must not embed credentials");
            }

            if (url.Scheme is not ("http" or "https" or "grpc"))
            {
                return Warning("uncommon URL scheme");
            }
        }

        if (key.Contains("dedup_window", StringComparison.Ordinal) && text.Length > 0 && !LooksLikeGoDurationOrSeconds(text))
        {
            return Error("duration must be like 30s, 1m, or a seconds integer");
        }

        if (key.Contains("tls_skip_verify", StringComparison.Ordinal) && text == "true")
        {
            return Warning("TLS verification is disabled; dev-only");
        }

        if (kind != FormFieldKind.Secret && IsSecretName(key) && LooksLikeSecretValue(text))
        {
            return Warning("secret-like value will be saved inline in config.yaml. " + SecretGuidance);
        }

        return FieldValidation.Ok;
    }

    /// <summary>
    /// True for the keys <see cref="Validate"/> treats as a port: <c>port</c> is a whole word of the last segment (<c>port</c>,
    /// <c>api_port</c>, <c>ingress_port</c>). The TUI matches the substring, which also catches <c>max_export_batch_size</c>,
    /// <c>transport_retries</c> and <c>support_level</c>; its catalogue has no such key, this form lists every key in the file.
    /// </summary>
    public static bool IsPortKey(string key)
    {
        var last = key[(key.LastIndexOf('.') + 1)..];
        return last.Split('_', '-').Contains("port", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary><c>is_config_env_name_field</c> by key (<c>setup_state.py:569-572</c>): the key ends in <c>_env</c>, or is an <c>.api_key_env</c> / <c>.token_env</c>.</summary>
    public static bool IsEnvNameKey(string key) =>
        key.EndsWith("_env", StringComparison.Ordinal) ||
        key.Contains(".api_key_env", StringComparison.Ordinal) ||
        key.Contains(".token_env", StringComparison.Ordinal);

    /// <summary><c>_looks_like_url_field</c> (<c>setup_state.py:934-935</c>).</summary>
    public static bool LooksLikeUrlField(string key) => UrlMarkers.Any(m => key.Contains(m, StringComparison.Ordinal));

    /// <summary>
    /// <c>_is_otlp_endpoint_field</c> (<c>setup_state.py:938-939</c>). The 0.8.10 function is a stub that answers false for every key (its v8
    /// observability destinations are lists of mappings, edited by <c>setup observability</c>), so its host:port branch can never run there. The
    /// branch is ported whole and this is the predicate it needs: a key named <c>endpoint</c> inside an <c>otel</c> or <c>otlp</c> block, which
    /// is where an older config wrote an exporter address as <c>host:4317</c>.
    /// </summary>
    public static bool IsOtlpEndpointField(string key)
    {
        if (!key.EndsWith("endpoint", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = key.Split('.');
        return segments.Length > 1 &&
               segments[..^1].Any(s => s.Contains("otel", StringComparison.OrdinalIgnoreCase) || s.Contains("otlp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>looks_like_env_name</c>: <c>[A-Z_][A-Z0-9_]*</c> over the whole trimmed text (<c>setup_state.py:583-584</c>).</summary>
    public static bool LooksLikeEnvName(string value) => EnvNamePattern().IsMatch(value.Trim());

    /// <summary><c>_is_secret_name</c> by key (<c>setup_state.py:926-931</c>); the TUI also tests the label, which here would be the humanised key.</summary>
    public static bool IsSecretName(string key)
    {
        var lowered = key.Trim().ToLowerInvariant();
        return lowered.Length > 0 && SecretNameMarkers.Any(m => lowered.Contains(m, StringComparison.Ordinal));
    }

    /// <summary><c>looks_like_secret_value</c> (<c>setup_state.py:587-600</c>): a credential prefix, a bearer token, a PEM header, a JWT's shape, or 32+ characters that are not a name.</summary>
    public static bool LooksLikeSecretValue(string value)
    {
        var stripped = value.Trim();
        if (stripped.Length == 0)
        {
            return false;
        }

        if (SecretPrefixes.Any(p => stripped.StartsWith(p, StringComparison.Ordinal)) ||
            stripped.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
            stripped.Contains("-----BEGIN ", StringComparison.Ordinal))
        {
            return true;
        }

        if (stripped.Count(c => c == '.') == 2 && stripped.Length > 40)
        {
            return true;
        }

        return stripped.Length >= 32 && !LooksLikeEnvName(stripped);
    }

    /// <summary><c>_validate_host_port</c> (<c>setup_state.py:942-950</c>): a non-empty host, a colon, and a port from 1 to 65535.</summary>
    public static bool ValidateHostPort(string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var host = value[..colon].Trim().Trim('[', ']');
        return host.Length > 0 && TryParseInteger(value[(colon + 1)..], out var port) && port >= 1 && port <= 65535;
    }

    /// <summary><c>_looks_like_go_duration_or_seconds</c> (<c>setup_state.py:953-962</c>): empty, digits only, or one or more number-and-unit pairs (<c>1h30m</c>, <c>500ms</c>).</summary>
    public static bool LooksLikeGoDurationOrSeconds(string value)
    {
        var stripped = value.Trim();
        return stripped.Length == 0 || stripped.All(char.IsDigit) || GoDurationPattern().IsMatch(stripped);
    }

    /// <summary>
    /// Python's <c>int(text)</c> for the text the editor can hold: an optional sign, then digits that may be grouped with single
    /// underscores (<c>1_000</c>). Surrounding white space is allowed; anything else, including an empty string, is not an integer.
    /// </summary>
    internal static bool TryParseInteger(string text, out BigInteger number)
    {
        number = BigInteger.Zero;
        var trimmed = text.Trim();
        return IntegerPattern().IsMatch(trimmed) &&
               BigInteger.TryParse(trimmed.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>The parts of a URL the checks look at, as Python's <c>urlparse</c> reads them.</summary>
    internal readonly record struct ParsedUrl(string Scheme, string Netloc, string? Username, string? Password, string Host);

    /// <summary>
    /// <c>urlparse(value)</c> as Python 3.13 (the 0.8.10 runtime's) splits it: a scheme before the first colon when it is a letter followed by
    /// scheme characters, a network location when the rest starts with <c>//</c>, the userinfo before the last <c>@</c> of it. Returns false where
    /// Python raises <c>ValueError</c> (an unbalanced or invalid bracketed host), which the caller reports as a URL that is not one.
    /// </summary>
    internal static bool TryParseUrl(string value, out ParsedUrl parsed)
    {
        parsed = default;

        // urlsplit: lstrip the C0 control characters and space, and drop tab, CR and LF wherever they are.
        var url = value.TrimStart(C0ControlOrSpace);
        url = url.Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        var scheme = string.Empty;
        var colon = url.IndexOf(':');
        if (colon > 0 && IsAsciiLetter(url[0]) && url.AsSpan(0, colon).IndexOfAnyExcept(SchemeCharacters) < 0)
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }

        var netloc = string.Empty;
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var end = url.Length;
            foreach (var delimiter in new[] { '/', '?', '#' })
            {
                var at = url.IndexOf(delimiter, 2);
                if (at >= 0)
                {
                    end = Math.Min(end, at);
                }
            }

            netloc = url[2..end];

            var open = netloc.IndexOf('[');
            var close = netloc.IndexOf(']');
            if ((open >= 0) != (close >= 0))
            {
                return false;
            }

            if (open >= 0 && !IsValidBracketedHost(netloc[(open + 1)..].Split(']')[0]))
            {
                return false;
            }
        }

        string? username = null;
        string? password = null;
        var hostinfo = netloc;
        var lastAt = netloc.LastIndexOf('@');
        if (lastAt >= 0)
        {
            var userinfo = netloc[..lastAt];
            hostinfo = netloc[(lastAt + 1)..];
            var firstColon = userinfo.IndexOf(':');
            if (firstColon >= 0)
            {
                username = userinfo[..firstColon];
                password = userinfo[(firstColon + 1)..];
            }
            else
            {
                username = userinfo;
            }
        }

        string host;
        var bracket = hostinfo.IndexOf('[');
        if (bracket >= 0)
        {
            host = hostinfo[(bracket + 1)..].Split(']')[0];
        }
        else
        {
            host = hostinfo.Split(':')[0];
        }

        parsed = new ParsedUrl(scheme, netloc, username, password, host);
        return true;
    }

    /// <summary>Python's <c>_WHATWG_C0_CONTROL_OR_SPACE</c>: U+0000 to U+0020.</summary>
    private static readonly char[] C0ControlOrSpace = Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray();

    /// <summary>Python's <c>scheme_chars</c>.</summary>
    private static readonly System.Buffers.SearchValues<char> SchemeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.");

    private static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    /// <summary>Python's <c>_check_bracketed_host</c>: an IPv6 literal, with or without a zone (<c>fe80::1%eth0</c>), or an IPvFuture literal.</summary>
    private static bool IsValidBracketedHost(string host)
    {
        if (host.StartsWith('v'))
        {
            return IpFuturePattern().IsMatch(host);
        }

        // ipaddress splits the zone off first and refuses an empty one or a second '%'.
        var address = host;
        var percent = host.IndexOf('%');
        if (percent >= 0)
        {
            var zone = host[(percent + 1)..];
            if (zone.Length == 0 || zone.Contains('%', StringComparison.Ordinal))
            {
                return false;
            }

            address = host[..percent];
        }

        return IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static FieldValidation Error(string message) => new(FieldSeverity.Error, message);

    private static FieldValidation Warning(string message) => new(FieldSeverity.Warning, message);

    [GeneratedRegex(@"\A[A-Z_][A-Z0-9_]*\z")]
    private static partial Regex EnvNamePattern();

    // The TUI's pattern: no sign, a digit before any dot, and the units ns, us, µs (U+00B5), ms, s, m, h.
    [GeneratedRegex(@"\A(?:\d+(?:\.\d+)?(?:ns|us|µs|ms|s|m|h))+\z")]
    private static partial Regex GoDurationPattern();

    [GeneratedRegex(@"\A[+-]?[0-9]+(?:_[0-9]+)*\z")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"\Av[a-fA-F0-9]+\..+\z")]
    private static partial Regex IpFuturePattern();
}
