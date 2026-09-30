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
/// Replicates the CLI's gateway-token priority ladder:
/// <list type="number">
///   <item>process environment variable named by <c>gateway.token_env</c>,</item>
///   <item>that same variable in <c>~/.defenseclaw/.env</c>,</item>
///   <item>a literal <c>gateway.token</c> in config.yaml.</item>
/// </list>
/// Empty or whitespace-only values at a rung are treated as absent and the walk continues.
/// <para>
/// <b>Every rung is trimmed</b> — a token pasted into an environment variable or a config line
/// routinely carries a trailing newline or space, and a header value with one is either a
/// <see cref="FormatException"/> or a 401. A value with a control character <i>inside</i> it
/// (a NUL, an embedded newline, an escape) is not a token anyone meant: it is treated as absent,
/// the walk continues, and <see cref="TokenResolution.Note"/> says which rung was skipped.
/// </para>
/// </summary>
public sealed class TokenResolver
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

    public TokenResolution Resolve(DefenseClawConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var variableName = config.Gateway.TokenEnv;
        string? note = null;

        if (Clean(_environment.GetVariable(variableName), $"{variableName} in the process environment", ref note) is { } fromEnvironment)
        {
            return new TokenResolution(new SecretValue(fromEnvironment), TokenSource.Environment, variableName) { Note = note };
        }

        var dotEnv = _dotEnvProvider();
        dotEnv.TryGetValue(variableName, out var rawDotEnv);
        if (Clean(rawDotEnv, $"{variableName} in .env", ref note) is { } fromDotEnv)
        {
            return new TokenResolution(new SecretValue(fromDotEnv), TokenSource.DotEnvFile, variableName) { Note = note };
        }

        if (Clean(config.Gateway.Token, "gateway.token in config.yaml", ref note) is { } fromConfig)
        {
            return new TokenResolution(new SecretValue(fromConfig), TokenSource.ConfigLiteral, variableName) { Note = note };
        }

        return new TokenResolution(null, TokenSource.None, variableName) { Note = note };
    }

    /// <summary>
    /// The trimmed value, or null when it is absent, blank or holds a control character (in which
    /// case <paramref name="note"/> gains a sentence naming <paramref name="where"/>).
    /// </summary>
    private static string? Clean(string? raw, string where, ref string? note)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
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
