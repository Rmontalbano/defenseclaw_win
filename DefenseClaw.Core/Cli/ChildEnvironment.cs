using System.Diagnostics;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// What a child process that is not DefenseClaw's own CLI or gateway may inherit. A started process gets this app's whole environment, which
/// can hold <c>DEFENSECLAW_GATEWAY_TOKEN</c> and provider keys (<c>OPENAI_API_KEY</c>, <c>*_TOKEN</c>, <c>*_SECRET</c>); docker, cmd, explorer, a
/// release installer or a resolver script have no use for them, and a program that logs its environment or starts a helper would pass
/// them on. Every start of such a process goes through <see cref="StripSecrets(ProcessStartInfo)"/> (or the dictionary overload when the
/// environment is handed to something other than <see cref="Process"/>). The DefenseClaw CLI and gateway are deliberately left alone: they
/// read these variables by design.
/// </summary>
public static class ChildEnvironment
{
    private static readonly string[] SecretSuffixes =
    [
        "_API_KEY",
        "_APIKEY",
        "_TOKEN",
        "_SECRET",
        "_SECRET_KEY",
        "_ACCESS_KEY",
        "_PASSWORD",
        "_PASSWD",
        "_CREDENTIALS",
    ];

    private static readonly string[] SecretNames =
    [
        "API_KEY",
        "APIKEY",
        "TOKEN",
        "SECRET",
        "PASSWORD",
        "DEFENSECLAW_GATEWAY_TOKEN",
    ];

    /// <summary>True for a variable name that conventionally carries a secret.</summary>
    public static bool IsSecretName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var exact in SecretNames)
        {
            if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var suffix in SecretSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes secret-looking variables from <paramref name="environment"/>; returns the names removed.</summary>
    public static IReadOnlyList<string> StripSecrets(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var doomed = environment.Keys.Where(IsSecretName).ToList();
        foreach (var name in doomed)
        {
            _ = environment.Remove(name);
        }

        return doomed;
    }

    /// <summary>
    /// Removes secret-looking variables from the environment <paramref name="startInfo"/> will give its child. Only meaningful with
    /// <c>UseShellExecute = false</c> (the shell hands a ShellExecute child this process's own environment, whatever is set here).
    /// </summary>
    public static IReadOnlyList<string> StripSecrets(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        return StripSecrets(startInfo.Environment);
    }
}
