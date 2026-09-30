using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.Core.Security;

/// <summary>
/// Finds the secrets DefenseClaw itself is configured with, so the CLI runner can be told about them
/// (<c>CliRunner.RegisterSecret</c>) and then refuse them on a command line and mask them in captured output.
/// Only the gateway token was registered before; every other secret on the machine that the operator can paste
/// into a form field — an LLM key, a Splunk token, a webhook secret — was invisible to the runner.
/// <para>
/// Two sources, both read-only:
/// <list type="number">
/// <item><description><b>Every secret-shaped value in <c>~/.defenseclaw/.env</c></b> (see
/// <see cref="SecretHeuristics.IsRegistrableEntry"/>): the file the CLI writes its secrets to.</description></item>
/// <item><description><b>Every variable a config <c>*_env</c> key names</b> (<c>gateway.token_env</c>,
/// <c>llm.api_key_env</c>, a webhook's <c>secret_env</c> …), looked up in the process environment first and then in
/// <c>.env</c>, the order the CLI itself resolves them in. config.yaml stores the <i>name</i>, so the key
/// tells us that the value is a secret whatever it looks like.</description></item>
/// </list>
/// </para>
/// <para>Values are returned wrapped in <see cref="SecretValue"/>; nothing is printed or logged.</para>
/// </summary>
public static partial class ConfiguredSecrets
{
    /// <summary>
    /// Every secret to register, without duplicates. Never throws for a missing or empty source.
    /// </summary>
    /// <param name="configText">The raw text of config.yaml, or null when there is none.</param>
    /// <param name="dotEnv">The parsed <c>.env</c> entries (empty when the file is missing).</param>
    /// <param name="environment">The process environment, or a fake for tests.</param>
    public static IReadOnlyList<SecretValue> Collect(
        string? configText,
        IReadOnlyDictionary<string, string> dotEnv,
        IEnvironmentReader environment)
    {
        ArgumentNullException.ThrowIfNull(dotEnv);
        ArgumentNullException.ThrowIfNull(environment);

        var found = new List<SecretValue>();

        void Add(string? value)
        {
            if (value is null)
            {
                return;
            }

            var secret = new SecretValue(value.Trim());
            if (!found.Contains(secret))
            {
                found.Add(secret);
            }
        }

        foreach (var (name, value) in dotEnv)
        {
            if (SecretHeuristics.IsRegistrableEntry(name, value))
            {
                Add(value);
            }
        }

        foreach (var name in EnvironmentVariableNamesIn(configText))
        {
            var value = environment.GetVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                dotEnv.TryGetValue(name, out value);
            }

            if (SecretHeuristics.IsRegistrableValue(value))
            {
                Add(value);
            }
        }

        return found;
    }

    /// <summary>
    /// The variable names that config text points at with a <c>*_env</c> key: <c>api_key_env: MY_KEY</c> gives
    /// <c>MY_KEY</c>. A textual scan, on purpose: it must keep working on a config the typed parser could not read,
    /// and it finds keys in sections this app has no class for. Only well-formed names are returned.
    /// </summary>
    public static IReadOnlyList<string> EnvironmentVariableNamesIn(string? configText)
    {
        if (string.IsNullOrEmpty(configText))
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        foreach (Match match in EnvKeyLinePattern().Matches(configText))
        {
            var name = match.Groups["value"].Value.Trim();
            if (CliRunOptions.IsValidEnvironmentName(name) && !names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // "  api_key_env: MY_KEY", "- secret_env: 'MY_KEY'  # comment": a key ending in _env, a scalar value, nothing else.
    [GeneratedRegex(
        @"^[ \t]*(?:-[ \t]+)?[A-Za-z0-9_.-]*_env[ \t]*:[ \t]*(?:""(?<value>[^""\r\n]*)""|'(?<value>[^'\r\n]*)'|(?<value>[^\s#""']+))[ \t]*(?:#[^\r\n]*)?\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex EnvKeyLinePattern();
}
