namespace DefenseClaw.Core.Cli;

/// <summary>
/// The one-line "what to do next" an Activity entry ends with once its command has finished (<c>next: rerun readiness</c>). A port of
/// DefenseClaw 0.8.10's <c>suggested_next_action</c> (<c>defenseclaw/tui/command_line.py</c>, itself a mirror of the Go TUI's
/// <c>suggestedNextAction</c>), so an operator sees the same nudge after the same command in the TUI and here.
/// <para>
/// <b>The table.</b> A failure (non-zero exit) says <c>open Credentials or run keys check</c> for a command about keys, <c>open readiness or
/// rerun doctor</c> for the doctor, and <c>review output and rerun when fixed</c> for anything else. A success says <c>rerun readiness</c>
/// after keys or setup, <c>review readiness</c> after the doctor, <c>refresh gateway health</c> after a restart, and nothing after any other
/// command (an empty string: the footer shows no hint rather than "none"). The first rule that matches wins, in the order keys, doctor, setup,
/// restart, as in the TUI, and the match is on the lower-cased command, a word contained anywhere in it.
/// </para>
/// <para>
/// <b>What the TUI matches.</b> Its label is the executable's name and the first two arguments that are not options (<see cref="LabelFor"/>),
/// not the whole command line, so a long argv cannot match on something it merely mentions. One difference from the TUI, on purpose: the label
/// stops at a <c>--</c>, because what follows is a name that came from outside (a skill called <c>doctor</c>) and not a word of the command.
/// </para>
/// </summary>
public static class CommandNextAction
{
    /// <summary>The hint after a failed command that is neither about keys nor the doctor.</summary>
    public const string ReviewOutputAndRerun = "review output and rerun when fixed";

    /// <summary>The hint after a failed <c>keys</c> command.</summary>
    public const string OpenCredentials = "open Credentials or run keys check";

    /// <summary>The hint after a failed <c>doctor</c>.</summary>
    public const string OpenReadiness = "open readiness or rerun doctor";

    /// <summary>The hint after a successful <c>keys</c> or <c>setup</c> command.</summary>
    public const string RerunReadiness = "rerun readiness";

    /// <summary>The hint after a successful <c>doctor</c>.</summary>
    public const string ReviewReadiness = "review readiness";

    /// <summary>The hint after a successful <c>restart</c>.</summary>
    public const string RefreshGatewayHealth = "refresh gateway health";

    /// <summary>
    /// The hint for a command that finished with <paramref name="exitCode"/>, or an empty string when there is nothing useful to say.
    /// </summary>
    /// <param name="command">What the TUI calls the command's label: see <see cref="LabelFor"/>. Matched without regard to case.</param>
    public static string Suggest(string command, int exitCode)
    {
        var text = (command ?? string.Empty).Trim().ToLowerInvariant();

        if (exitCode != 0)
        {
            if (text.Contains("keys", StringComparison.Ordinal))
            {
                return OpenCredentials;
            }

            return text.Contains("doctor", StringComparison.Ordinal) ? OpenReadiness : ReviewOutputAndRerun;
        }

        if (text.Contains("keys", StringComparison.Ordinal))
        {
            return RerunReadiness;
        }

        if (text.Contains("doctor", StringComparison.Ordinal))
        {
            return ReviewReadiness;
        }

        if (text.Contains("setup", StringComparison.Ordinal))
        {
            return RerunReadiness;
        }

        return text.Contains("restart", StringComparison.Ordinal) ? RefreshGatewayHealth : string.Empty;
    }

    /// <summary>
    /// The hint for the command <paramref name="executable"/> <paramref name="argv"/>: <see cref="Suggest"/> over <see cref="LabelFor"/>.
    /// </summary>
    public static string For(string executable, IReadOnlyList<string> argv, int exitCode) =>
        Suggest(LabelFor(executable, argv), exitCode);

    /// <summary>
    /// The label the TUI derives for a command when it has no display name (<c>_derive_command_label</c>): the executable's name without its
    /// folder or extension, then the first two arguments that do not start with <c>-</c> (<c>defenseclaw agent discovery</c> for
    /// <c>agent discovery scan --json</c>). Stops at a <c>--</c> terminator, after which come names, not words of the command.
    /// </summary>
    public static string LabelFor(string executable, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var head = Path.GetFileNameWithoutExtension(executable ?? string.Empty);
        var words = new List<string> { head.Length == 0 ? "command" : head };

        foreach (var argument in argv)
        {
            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                break;
            }

            if (argument.StartsWith('-'))
            {
                continue;
            }

            words.Add(argument);
            if (words.Count >= 3)
            {
                break;
            }
        }

        return string.Join(' ', words);
    }
}
