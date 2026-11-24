using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Config;

/// <summary>Which rung of the token ladder produced a value.</summary>
public enum TokenSource
{
    /// <summary>No token anywhere; every authenticated endpoint will 401.</summary>
    None = 0,

    /// <summary>Process environment variable named by <c>gateway.token_env</c>.</summary>
    Environment,

    /// <summary>Same variable, defined in <c>~/.defenseclaw/.env</c>.</summary>
    DotEnvFile,

    /// <summary>Literal <c>gateway.token</c> in config.yaml.</summary>
    ConfigLiteral,
}

/// <summary>Outcome of a ladder walk. Never carries the plaintext token.</summary>
public sealed record TokenResolution(SecretValue? Token, TokenSource Source, string VariableName)
{
    public bool Found => Token is not null;

    /// <summary>
    /// What was wrong with a rung that was passed over — currently: a value with control
    /// characters in it, treated as absent. Names the variable and the rung, never the value.
    /// Null when nothing was skipped; the text to append when a 401 says "no token".
    /// </summary>
    public string? Note { get; init; }

    /// <summary>Safe for logs and banners: names the rung, never the value.</summary>
    public override string ToString()
    {
        var text = Found
            ? $"token from {Source} ({VariableName}), length {Token!.Length}"
            : $"no token found for {VariableName}";

        return Note is null ? text : $"{text}; {Note}";
    }
}

/// <summary>Indirection over the process environment so tests can supply their own.</summary>
public interface IEnvironmentReader
{
    string? GetVariable(string name);
}

/// <summary>Reads the real process environment.</summary>
public sealed class ProcessEnvironmentReader : IEnvironmentReader
{
    public static readonly ProcessEnvironmentReader Instance = new();

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>Fixed map, for tests and for previewing a resolution.</summary>
public sealed class DictionaryEnvironmentReader : IEnvironmentReader
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public DictionaryEnvironmentReader(IReadOnlyDictionary<string, string> values)
    {
        _values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public string? GetVariable(string name) => _values.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Replicates the runtime's gateway-token priority ladder. Up to 1.0.9:
/// <list type="number">
///   <item>process environment variable named by <c>gateway.token_env</c>,</item>
///   <item>that same variable in <c>~/.defenseclaw/.env</c>,</item>
///   <item>a literal <c>gateway.token</c> in config.yaml.</item>
/// </list>
/// From 1.0.10 (<see cref="OpenClawTokenSeparate"/>) the OpenClaw token is not DefenseClaw's: the names tried are
/// <c>gateway.token_env</c> then <c>DEFENSECLAW_GATEWAY_TOKEN</c> (each from the environment, then .env), a name that is
/// <c>OPENCLAW_GATEWAY_TOKEN</c> is skipped, a value equal to the OpenClaw token is skipped, then the literal.
/// Empty or whitespace-only values at a rung are treated as absent and the walk continues.
/// <para>
/// <b>Every rung is trimmed</b> — a token pasted into an environment variable or a config line
/// routinely carries a trailing newline or space, and a header value with one is either a
/// <see cref="FormatException"/> or a 401. A value with a control character <i>inside</i> it
/// (a NUL, an embedded newline, an escape) is not a token anyone meant: it is treated as absent,
/// the walk continues, and <see cref="TokenResolution.Note"/> says which rung was skipped.
/// </para>
/// </summary>
public sealed partial class TokenResolver
{
    private readonly IEnvironmentReader _environment;
    private readonly Func<IReadOnlyDictionary<string, string>> _dotEnvProvider;

    public TokenResolver(
        Func<IReadOnlyDictionary<string, string>> dotEnvProvider,
        IEnvironmentReader? environment = null)
    {
        _dotEnvProvider = dotEnvProvider ?? throw new ArgumentNullException(nameof(dotEnvProvider));
        _environment = environment ?? ProcessEnvironmentReader.Instance;
    }

    /// <summary>Reads the .env file lazily from <paramref name="envFilePath"/> on each resolve.</summary>
    public TokenResolver(string envFilePath, IEnvironmentReader? environment = null)
        : this(() => DotEnvFile.Load(envFilePath), environment)
    {
    }

    /// <summary>
    /// Whether the runtime ignores a secret-reference text used as the gateway token (RuntimeCapability.SecretRefGatewayTokenSkipped,
    /// 1.0.7+). Read at every resolve. The default, and the answer while the runtime is unknown, is false: a 1.0.5/1.0.6 gateway
    /// uses that text as its bearer, so the app sends it too.
    /// </summary>
    public Func<bool> SkipSecretReferences { get; set; } = static () => false;

    /// <summary>
    /// Whether the runtime keeps the OpenClaw token apart from its own API token (RuntimeCapability.OpenClawTokenSeparate, 1.0.10+).
    /// Read at every resolve. The default, and the answer while the runtime is unknown, is false: a 1.0.9 or earlier gateway still
    /// accepts the variable <c>gateway.token_env</c> names, whatever it is called, so the app does too.
    /// </summary>
    public Func<bool> OpenClawTokenSeparate { get; set; } = static () => false;

    public TokenResolution Resolve(DefenseClawConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (OpenClawTokenSeparate())
        {
            return ResolveSeparate(config);
        }

        var variableName = config.Gateway.TokenEnv;
        string? note = null;
        var skipReferences = SkipSecretReferences();

        if (Clean(_environment.GetVariable(variableName), $"{variableName} in the process environment", skipReferences, ref note) is { } fromEnvironment)
        {
            return new TokenResolution(new SecretValue(fromEnvironment), TokenSource.Environment, variableName) { Note = note };
        }

        var dotEnv = _dotEnvProvider();
        dotEnv.TryGetValue(variableName, out var rawDotEnv);
        if (Clean(rawDotEnv, $"{variableName} in .env", skipReferences, ref note) is { } fromDotEnv)
        {
            return new TokenResolution(new SecretValue(fromDotEnv), TokenSource.DotEnvFile, variableName) { Note = note };
        }

        if (Clean(config.Gateway.Token, "gateway.token in config.yaml", skipReferences, ref note) is { } fromConfig)
        {
            return new TokenResolution(new SecretValue(fromConfig), TokenSource.ConfigLiteral, variableName) { Note = note };
        }

        return new TokenResolution(null, TokenSource.None, variableName) { Note = note };
    }

    private const string OpenClawVariable = "OPENCLAW_GATEWAY_TOKEN";
    private const string CanonicalVariable = "DEFENSECLAW_GATEWAY_TOKEN";

    /// <summary>
    /// The 1.0.10 order (config.go ResolvedToken): the OpenClaw token is not DefenseClaw's. Candidate names are
    /// [gateway.token_env, DEFENSECLAW_GATEWAY_TOKEN]; a blank name or one naming OPENCLAW_GATEWAY_TOKEN (any case) is skipped;
    /// a value equal to the OpenClaw token is skipped; then the literal gateway.token, under the same rule.
    /// </summary>
    private TokenResolution ResolveSeparate(DefenseClawConfig config)
    {
        string? note = null;
        var dotEnv = _dotEnvProvider();
        var openClaw = OpenClawToken(dotEnv);

        var configured = config.Gateway.TokenEnv?.Trim();
        if (!string.IsNullOrEmpty(configured) && IsOpenClawName(configured))
        {
            // Names the variable only. 'doctor --fix' rewrites a config that still names it (CHANGELOG 1.0.10).
            note = $"gateway.token_env names {OpenClawVariable}, which DefenseClaw 1.0.10 no longer accepts as its API token. Run 'defenseclaw doctor --fix'.";
        }

        var names = new List<string>(2);
        if (!string.IsNullOrEmpty(configured) && !IsOpenClawName(configured))
        {
            names.Add(configured);
        }

        names.Add(CanonicalVariable);
        var reported = names[0];

        bool Usable(string? value) => value is not null && (openClaw is null || value != openClaw);

        foreach (var name in names)
        {
            if (Clean(_environment.GetVariable(name), $"{name} in the process environment", true, ref note) is { } fromEnvironment && Usable(fromEnvironment))
            {
                return new TokenResolution(new SecretValue(fromEnvironment), TokenSource.Environment, name) { Note = note };
            }

            dotEnv.TryGetValue(name, out var rawDotEnv);
            if (Clean(rawDotEnv, $"{name} in .env", true, ref note) is { } fromDotEnv && Usable(fromDotEnv))
            {
                return new TokenResolution(new SecretValue(fromDotEnv), TokenSource.DotEnvFile, name) { Note = note };
            }
        }

        if (Clean(config.Gateway.Token, "gateway.token in config.yaml", true, ref note) is { } fromConfig && Usable(fromConfig))
        {
            return new TokenResolution(new SecretValue(fromConfig), TokenSource.ConfigLiteral, reported) { Note = note };
        }

        return new TokenResolution(null, TokenSource.None, reported) { Note = note };
    }

    private static bool IsOpenClawName(string name) =>
        name.Trim().Equals(OpenClawVariable, StringComparison.OrdinalIgnoreCase);

    /// <summary>The OpenClaw token the way the runtime reads it: process environment, else .env; a secret reference is not a token. Null when none.</summary>
    private string? OpenClawToken(IReadOnlyDictionary<string, string> dotEnv)
    {
        var fromEnvironment = _environment.GetVariable(OpenClawVariable)?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment) && !IsSecretRefText(fromEnvironment))
        {
            return fromEnvironment;
        }

        dotEnv.TryGetValue(OpenClawVariable, out var raw);
        var fromFile = raw?.Trim();
        return !string.IsNullOrEmpty(fromFile) && !IsSecretRefText(fromFile) ? fromFile : null;
    }

    // Same shape as the runtime's check: one line, braces around a "source" key. No Singleline, so "." stops at a newline.
    [GeneratedRegex(@"^\{.*[""']source[""']\s*:.*\}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex SecretRefText();

    /// <summary>A pathological value that times the pattern out is simply not a reference (and never fails the config load).</summary>
    internal static bool IsSecretRefText(string value)
    {
        try
        {
            return SecretRefText().IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The trimmed value, or null when it is absent, blank or holds a control character (in which
    /// case <paramref name="note"/> gains a sentence naming <paramref name="where"/>).
    /// </summary>
    private static string? Clean(string? raw, string where, bool skipReferences, ref string? note)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (skipReferences && IsSecretRefText(trimmed))
        {
            // DefenseClaw 1.0.5/1.0.6 could save an OpenClaw secret reference as the token (upstream issue #1404). Never echo the value.
            var sentence = $"{where} holds an OpenClaw secret reference that an earlier DefenseClaw saved as the gateway token, so it was ignored. Run defenseclaw doctor --fix.";
            note = note is null ? sentence : $"{note} {sentence}";
            return null;
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
            {
                var sentence = $"{where} contains a control character, so it was ignored.";
                note = note is null ? sentence : $"{note} {sentence}";
                return null;
            }
        }

        return trimmed;
    }
}
