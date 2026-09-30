using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>Whether a credential variable currently has a value, and where. Never carries the value.</summary>
public enum CredentialPresence
{
    /// <summary>The variable name is not known, so there is nothing to look for.</summary>
    Unknown = 0,

    /// <summary>Not in <c>~/.defenseclaw/.env</c> and not in this app's environment.</summary>
    NotSet,

    /// <summary>Has a non-empty value in <c>~/.defenseclaw/.env</c>.</summary>
    InDotEnv,

    /// <summary>Has a non-empty value in this app's own environment (which every CLI it starts inherits).</summary>
    InEnvironment,
}

/// <summary>
/// The credential side of the wizards: "is the variable the command will read set?" and "let me type
/// it into a real console".
/// <para>
/// <b>This class never sees a secret.</b> The presence check reads <c>.env</c> only to learn whether a key
/// has a non-empty value and immediately discards the text; nothing is cached, logged or returned.
/// Storing a value from here is done by <c>defenseclaw keys set NAME</c> in a console the operator types into —
/// see <see cref="SecretRoute"/> for why no piped route exists on Windows. (A value typed into the wizard's
/// password box, for the flags whose CLI reads an environment variable, never passes through this class either:
/// it goes from the box to the run's environment overlay.)
/// </para>
/// </summary>
public sealed class WizardCredentials
{
    /// <summary>The shape of an environment variable name the CLI accepts and the shell cannot misread.</summary>
    private static readonly Regex NamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// How long the set of variable NAMES found in <c>.env</c> is reused. Typing in a wizard field re-derives
    /// the credential status on every keystroke; the file is tiny but there is no reason to re-read it
    /// forty times a second. A "Re-check" press, and arriving on the review page, always read afresh.
    /// </summary>
    private static readonly TimeSpan NameCacheLifetime = TimeSpan.FromSeconds(2);

    private readonly DefenseClawPaths _paths;
    private HashSet<string>? _dotEnvNames;
    private DateTimeOffset _dotEnvReadAt;

    public WizardCredentials(DefenseClawPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>True for a name that is safe to hand to <c>keys set</c> and to a console command line.</summary>
    public static bool IsValidName([NotNullWhen(true)] string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 128 && NamePattern.IsMatch(name);

    /// <summary>The exact command the operator runs; also what the review page and the clipboard show.</summary>
    public static string KeysSetCommand(string envName) => "defenseclaw keys set " + envName;

    /// <param name="envName">The variable NAME to look for.</param>
    /// <param name="fresh">True to re-read <c>.env</c> even if it was read a moment ago.</param>
    public CredentialPresence Check(string? envName, bool fresh = false)
    {
        if (!IsValidName(envName))
        {
            return CredentialPresence.Unknown;
        }

        // The CLI never overwrites a variable that is already in its environment with the .env value,
        // and its environment is this app's, so the process environment is consulted first.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envName)))
        {
            return CredentialPresence.InEnvironment;
        }

        var names = DotEnvNames(fresh);
        if (names is null)
        {
            return CredentialPresence.Unknown;
        }

        return names.Contains(envName!) ? CredentialPresence.InDotEnv : CredentialPresence.NotSet;
    }

    /// <summary>
    /// The names in <c>.env</c> that have a non-empty value — names only: the values are dropped as
    /// the file is read, so nothing secret is ever held by this type. Null when the file cannot be read.
    /// </summary>
    private HashSet<string>? DotEnvNames(bool fresh)
    {
        if (!fresh && _dotEnvNames is not null && DateTimeOffset.UtcNow - _dotEnvReadAt < NameCacheLifetime)
        {
            return _dotEnvNames;
        }

        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, value) in DotEnvFile.Load(_paths.EnvFilePath))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _ = names.Add(key);
                }
            }

            _dotEnvNames = names;
            _dotEnvReadAt = DateTimeOffset.UtcNow;
            return names;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a new console window running <c>defenseclaw keys set NAME</c> and pausing at the end so the
    /// result stays readable. The operator types the value at the CLI's own hidden prompt; it never
    /// passes through this process. Returns null on success, otherwise a sentence for the wizard's
    /// message line.
    /// <para>
    /// <c>cmd /d /s /c "…"</c> is used on purpose: <c>/s</c> makes the outer quotes unambiguous, so an
    /// install path with spaces survives, and <c>/d</c> skips AutoRun. The variable name is validated
    /// against <see cref="IsValidName"/> first, so nothing here can be steered into another command.
    /// </para>
    /// </summary>
    public string? OpenKeysSetTerminal(string? envName)
    {
        if (!IsValidName(envName))
        {
            return "The variable name is missing or has characters an environment variable cannot have.";
        }

        // A click handler on the UI thread: use the remembered answer when there is one (the wizard has usually primed it)
        // and only fall back to a PATH scan when nothing is known yet.
        var executable = _paths.TryGetKnownExecutable("defenseclaw", out var known) ? known : _paths.FindExecutable("defenseclaw");
        if (executable is null)
        {
            return "defenseclaw is not on PATH or in the installer's bin directory, so there is nothing to run.";
        }

        var shell = Environment.GetEnvironmentVariable("ComSpec");
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(shell) ? "cmd.exe" : shell,
            Arguments = $"/d /s /c \"\"{executable}\" keys set {envName} & echo. & pause\"",
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = _paths.DataDirectoryExists ? _paths.DataDirectory : Environment.CurrentDirectory,
        };

        try
        {
            using var process = Process.Start(startInfo);
            return process is null ? "The console could not be started." : null;
        }
        catch (Win32Exception ex)
        {
            return "The console could not be started: " + ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            return "The console could not be started: " + ex.Message;
        }
    }
}
