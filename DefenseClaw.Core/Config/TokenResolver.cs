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

    /// <summary>Safe for logs and banners: names the rung, never the value.</summary>
    public override string ToString() =>
        Found
            ? $"token from {Source} ({VariableName}), length {Token!.Length}"
            : $"no token found for {VariableName}";
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

        var fromEnvironment = _environment.GetVariable(variableName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new TokenResolution(new SecretValue(fromEnvironment), TokenSource.Environment, variableName);
        }

        var dotEnv = _dotEnvProvider();
        if (dotEnv.TryGetValue(variableName, out var fromDotEnv) && !string.IsNullOrWhiteSpace(fromDotEnv))
        {
            return new TokenResolution(new SecretValue(fromDotEnv), TokenSource.DotEnvFile, variableName);
        }

        if (!string.IsNullOrWhiteSpace(config.Gateway.Token))
        {
            return new TokenResolution(new SecretValue(config.Gateway.Token), TokenSource.ConfigLiteral, variableName);
        }

        return new TokenResolution(null, TokenSource.None, variableName);
    }
}
