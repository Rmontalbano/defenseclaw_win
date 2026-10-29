using System.ComponentModel;
using System.Diagnostics;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.ViewModels;

/// <summary>What opening the console came to; the message is for the operator and never carries a value.</summary>
/// <param name="Started">True when a console window was started.</param>
/// <param name="Message">One sentence: what happened, or why nothing did.</param>
/// <param name="ExecutablePath">The <c>defenseclaw</c> that was handed the command; null when none was found.</param>
public sealed record CredentialTerminalResult(bool Started, string Message, string? ExecutablePath);

/// <summary>
/// The console route for <c>keys set NAME</c> and <c>keys fill-missing</c> (CUST-266): the fallback of the Credentials card's in-app route (CUST-221's
/// pseudo-console, <see cref="DefenseClaw.Core.Cli.SecretPtyRunner"/>), used where the app cannot type the value itself and whenever the operator presses the console button.
/// <para>
/// Both read the secret with <c>click.prompt(hide_input=True)</c>, which on Windows is <c>getpass</c> and reads the console input
/// buffer, not stdin: a piped value hangs. So the app hands the exact command to a console window the operator types into. The value never
/// passes through this process, and it is not on any command line - only the command and the variable's NAME are.
/// </para>
/// <para>
/// <b>Which console.</b> The system's own <c>cmd.exe</c> (absolute path, never <c>%ComSpec%</c> or a PATH lookup), started with
/// <c>UseShellExecute=false, CreateNoWindow=false</c>: this is a GUI process with no console, so Windows gives the console-subsystem child
/// a new, visible console window - on every Windows 10/11 install, with no dependency on Windows Terminal being present or being the
/// default host (<c>wt</c> would need its own quoting and is absent on LTSC/Server). <c>cmd /d /s /c "..."</c> keeps an install path with
/// spaces intact (<c>/s</c>) and skips AutoRun (<c>/d</c>); the trailing <c>pause</c> keeps the result readable. It is the same route the
/// wizards use for <c>keys set</c> (<see cref="WizardCredentials"/>), so there is one behaviour to explain.
/// </para>
/// <para>
/// Every token after the executable must be a plain word or an environment variable name (<see cref="IsSafeToken"/>); anything else is
/// refused, so nothing can be steered into another command by the <c>cmd</c> line.
/// </para>
/// </summary>
public sealed class CredentialTerminal
{
    private readonly DefenseClawPaths _paths;

    public CredentialTerminal(DefenseClawPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>Finds <c>defenseclaw</c>; a test replaces it. Default: the runner's own resolution (known answer, then PATH, then the bin dir).</summary>
    internal Func<Task<string?>>? ResolveExecutable { get; set; }

    /// <summary>Starts the console; returns null on success or a sentence on failure. A test replaces it so no window ever opens.</summary>
    internal Func<ProcessStartInfo, string?> Launch { get; set; } = StartProcess;

    /// <summary>What <see cref="OpenAsync"/> starts, separate from the start itself so a test can read it.</summary>
    internal static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> argv, string workingDirectory) => new()
    {
        FileName = WizardCredentials.CommandInterpreterPath(),
        Arguments = $"/d /s /c \"\"{executable}\" {string.Join(' ', argv)} & echo. & pause\"",
        UseShellExecute = false,
        CreateNoWindow = false,
        WorkingDirectory = workingDirectory,
    };

    /// <summary>True for a word that cannot change what the <c>cmd</c> line does: a verb, a <c>--flag</c> or an environment variable name.</summary>
    internal static bool IsSafeToken(string token) =>
        !string.IsNullOrEmpty(token) && token.Length <= 128 &&
        token.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>Opens a console running <c>defenseclaw <paramref name="argv"/></c>.</summary>
    /// <param name="argv">The arguments without the executable: <c>keys set NAME</c> or <c>keys fill-missing --yes</c>.</param>
    public async Task<CredentialTerminalResult> OpenAsync(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        if (argv.Count == 0 || !argv.All(IsSafeToken))
        {
            return new CredentialTerminalResult(false, "The command has characters a console command line cannot carry safely, so it was not started.", null);
        }

        var executable = ResolveExecutable is { } resolve
            ? await resolve().ConfigureAwait(true)
            : await _paths.FindExecutableAsync("defenseclaw").ConfigureAwait(true);
        if (executable is null)
        {
            return new CredentialTerminalResult(false, "defenseclaw is not on PATH or in the installer's bin directory, so there is nothing to run.", null);
        }

        // `"` would end the quoted program name and `%` expands inside a cmd line: neither is a character a real install path has.
        if (executable.Contains('"', StringComparison.Ordinal) || executable.Contains('%', StringComparison.Ordinal))
        {
            return new CredentialTerminalResult(false, "The defenseclaw path has a character a console command line cannot carry safely, so it was not started.", null);
        }

        var startInfo = StartInfo(executable, argv, _paths.DataDirectoryExists ? _paths.DataDirectory : Environment.CurrentDirectory);
        var failure = Launch(startInfo);
        return failure is null
            ? new CredentialTerminalResult(true, "Opened a console window. Type the value at its hidden prompt, then come back here.", executable)
            : new CredentialTerminalResult(false, failure, executable);
    }

    private static string? StartProcess(ProcessStartInfo startInfo)
    {
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
