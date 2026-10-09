using System.Globalization;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Config;

namespace DefenseClaw.Core.Cli;

/// <summary>How a run in a pseudo-console ended; what a caller decides on (the terminal hand-off is the answer to the two that say the value was never typed).</summary>
public enum SecretPtyOutcome
{
    /// <summary>The command ran and ended on its own. Its exit code says whether it worked (<see cref="SecretPtyResult.Succeeded"/>), and <see cref="SecretPtyResult.ValuesTyped"/> says whether it asked for the value.</summary>
    Completed,

    /// <summary>There is no pseudo-console to run it in (Windows before 10 1809, a container runtime) or it could not be started in one. No value was typed anywhere.</summary>
    Unavailable,

    /// <summary>The command started but never showed the prompt the value answers within the limit, so it was stopped (its whole process tree) before anything was typed.</summary>
    PromptNotSeen,

    /// <summary>The operator, the caller or the app exiting stopped it.</summary>
    Cancelled,

    /// <summary>It ran past its limit (the overall one, or the time after the value was typed) and was stopped.</summary>
    TimedOut,

    /// <summary>The installation is read-only (managed or invalid): nothing was started, and the refusal is recorded in Activity.</summary>
    Refused,
}

/// <summary>What <see cref="SecretPtyRunner"/> did. Never carries a value.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Invocation">Its entry in Activity (the transcript, the exit code, why it stopped); null when nothing was recorded because nothing could start.</param>
/// <param name="ValuesTyped">How many values were typed at a prompt.</param>
/// <param name="ValuesExpected">How many were to be typed.</param>
/// <param name="Message">One sentence for the operator.</param>
public sealed record SecretPtyResult(SecretPtyOutcome Outcome, CliInvocation? Invocation, int ValuesTyped, int ValuesExpected, string Message)
{
    /// <summary>The command ended with exit 0 after every value was typed.</summary>
    public bool Succeeded => Outcome == SecretPtyOutcome.Completed && Invocation is { ExitCode: 0 } && ValuesTyped == ValuesExpected;

    /// <summary>
    /// The pseudo-console route itself did not work and no value reached anything, so the same command can still be done in a console window
    /// the operator types into. A run that typed the value and then failed is not this: the CLI said no, and a console would say the same.
    /// </summary>
    public bool RouteFailed => Outcome is SecretPtyOutcome.Unavailable or SecretPtyOutcome.PromptNotSeen && ValuesTyped == 0;
}

/// <summary>
/// Answers a command's hidden prompt for the operator, in a pseudo-console (CUST-221): the one way to give <c>keys set NAME</c> a value from this app.
/// <para>
/// <b>The blocker.</b> The CLI reads the secret with <c>click.prompt(hide_input=True)</c>, which on Windows is <c>getpass.win_getpass</c> and
/// <c>msvcrt.getwch</c>: it reads the console's input buffer, not stdin. A value piped to the process is never read (the command waits until
/// it is killed), and the value is not allowed on a command line (visible to every process; <see cref="CliRunner"/> refuses it). A console window
/// the operator types into works and is still the fallback (<c>CredentialTerminal</c>), but it takes the value out of the app. A pseudo-console
/// gives the child a real console without a window: the app starts the command in it, waits for the prompt, types the value and Enter, reads what
/// the command prints, and ends the whole tree if anything goes wrong.
/// </para>
/// <para>
/// <b>What it guarantees.</b> The value goes from the <see cref="SecretValue"/> to the child's input pipe and nowhere else: not to argv (refused
/// before anything starts), not to the environment, not into Activity's transcript (every line is scrubbed of it, and of the CLI's own four-and-four
/// preview of it), not into an exception message. It is typed only at a line that is the prompt for the variable it is for, and only once.
/// The run is a <see cref="CliRunner"/> run like any other: refused on a read-only installation, listed in Activity with its argv, ended by Cancel, by its
/// limits and by the app exiting, with the process tree killed (the child is in a kill-on-close job from its first instruction).
/// </para>
/// <para>
/// <b>What it cannot do.</b> It needs Windows 10 1809; a runtime in a container is not reached; and the prompt must look like the 0.8.10 CLI's
/// (<c>  NAME:</c>). Anything else ends as <see cref="SecretPtyOutcome.PromptNotSeen"/> or <see cref="SecretPtyOutcome.Unavailable"/> with nothing typed,
/// and the caller offers the console window. A managed string cannot be wiped: the value lives as long as its <see cref="SecretValue"/> does.
/// </para>
/// </summary>
public sealed class SecretPtyRunner
{
    /// <summary>How long the CLI has to show its prompt (Python and the CLI's imports start in about a second; 30 s allows a scanner to look at them first).</summary>
    public static readonly TimeSpan DefaultPromptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the CLI has, after the last value was typed, to finish writing it.</summary>
    public static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromSeconds(30);

    private readonly CliRunner _runner;
    private readonly Func<bool> _isSupported;

    /// <param name="runner">Where the run is recorded, gated and ended from.</param>
    /// <param name="isSupported">Whether a pseudo-console can be used; null asks Windows. A test replaces it.</param>
    public SecretPtyRunner(CliRunner runner, Func<bool>? isSupported = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _isSupported = isSupported ?? (() => PseudoConsole.IsSupported);
    }

    /// <summary>True on Windows 10 1809 and later: the Credentials card then types values itself; otherwise it opens a console window.</summary>
    public static bool IsSupportedHere => PseudoConsole.IsSupported;

    public bool IsSupported => _isSupported();

    /// <summary>How long the command has to show its prompt before it is stopped with nothing typed.</summary>
    public TimeSpan PromptTimeout { get; init; } = DefaultPromptTimeout;

    /// <summary>How long it has to finish once the last value is typed.</summary>
    public TimeSpan CompletionTimeout { get; init; } = DefaultCompletionTimeout;

    /// <summary>The argv that stores a value for <paramref name="envName"/>: <c>keys set NAME</c>. No value, not even a placeholder: the prompt takes it.</summary>
    public static IReadOnlyList<string> SetKeyArgv(string envName) => new[] { "keys", "set", envName };

    /// <summary>
    /// Runs <c>defenseclaw keys set <paramref name="envName"/></c> in a pseudo-console and types <paramref name="value"/> at its prompt.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="envName"/> is not an environment variable name, or <paramref name="value"/> is empty.</exception>
    /// <exception cref="SecretInArgumentException">The value is part of the name (it would be on the command line).</exception>
    /// <exception cref="CliNotFoundException"><c>defenseclaw</c> is not on this machine.</exception>
    public Task<SecretPtyResult> SetKeyAsync(string envName, SecretValue value, CancellationToken cancellationToken = default)
    {
        if (!CliRunOptions.IsValidEnvironmentName(envName))
        {
            throw new ArgumentException("A credential name is letters, digits and underscores, starting with a letter or an underscore.", nameof(envName));
        }

        ArgumentNullException.ThrowIfNull(value);
        if (value.IsEmpty)
        {
            throw new ArgumentException("There is no value to store.", nameof(value));
        }

        return RunAsync(
            "defenseclaw",
            byName: true,
            SetKeyArgv(envName),
            new[] { new PtyAnswer(PromptFor(envName), value) },
            cancellationToken);
    }

    /// <summary>
    /// The prompt <c>keys set NAME</c> shows (<c>click.prompt("  NAME", ...)</c> draws <c>  NAME: </c>), as a pattern for the cursor's row: the
    /// name and a colon at the end of the row, after nothing or white space. A sentence that merely ends in the name is not it.
    /// </summary>
    internal static Regex PromptFor(string envName) =>
        new(@"(?:^|\s)" + Regex.Escape(envName) + ":$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The general form, for the tests' stand-ins: any program, by name or by path, with its answers.</summary>
    internal Task<SecretPtyResult> RunAsync(
        string executable,
        bool byName,
        IReadOnlyList<string> argv,
        IReadOnlyList<PtyAnswer> answers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(answers);

        if (!IsSupported)
        {
            return Task.FromResult(new SecretPtyResult(
                SecretPtyOutcome.Unavailable,
                null,
                0,
                answers.Count,
                "This version of Windows has no pseudo-console (it needs Windows 10 version 1809 or later)."));
        }

        return _runner.RunInPseudoConsoleAsync(executable, byName, argv, answers, PromptTimeout, CompletionTimeout, cancellationToken);
    }

    internal static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
