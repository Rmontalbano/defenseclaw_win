namespace DefenseClaw.App.Services.Guardrail;

/// <summary>
/// The argv for each guardrail control, in one place so the view-model and its tests agree on it. Every flag was checked
/// against <c>defenseclaw guardrail --help</c>, each subcommand's <c>--help</c> and <c>cmd_guardrail.py</c> / <c>cmd_judge.py</c>:
/// <list type="bullet">
/// <item><c>hilt [on|off] [--min-severity low|medium|high|critical] [--connector X] [--restart|--no-restart] [--yes]</c></item>
/// <item><c>block-message [MESSAGE | --clear] [--connector X] [--restart|--no-restart] [--yes]</c></item>
/// <item><c>judge add CONNECTOR [--enable] [--timeout S] [--restart|--no-restart]</c> and <c>judge remove CONNECTOR [--restart|--no-restart]</c>:
/// no <c>--yes</c> and no <c>--connector</c>, because neither asks anything and the connector is the argument.</item>
/// </list>
/// The <c>--yes</c> of the first two is not optional: without it they ask "Proceed?" and the app cannot answer.
/// </summary>
public static class GuardrailControlArgv
{
    /// <summary>The severities <c>--min-severity</c> accepts, lowest first (the Mac offers the same four).</summary>
    public static readonly IReadOnlyList<string> Severities = new[] { "LOW", "MEDIUM", "HIGH", "CRITICAL" };

    /// <summary>The word the CLI takes for "every hook connector".</summary>
    public const string AllConnectors = "all";

    /// <summary><c>guardrail hilt on|off</c>; the severity is sent only when turning it on.</summary>
    public static string[] Hilt(bool on, string? minSeverity, string? connector, bool restart)
    {
        var argv = new List<string> { "guardrail", "hilt", on ? "on" : "off", "--yes" };
        if (on && !string.IsNullOrWhiteSpace(minSeverity))
        {
            argv.Add("--min-severity");
            argv.Add(minSeverity.Trim().ToUpperInvariant());
        }

        return Finish(argv, connector, restart);
    }

    /// <summary>
    /// <c>guardrail block-message [--clear] [-- MESSAGE]</c>. The message comes after a <c>--</c>, so text that starts
    /// with a dash, or reads like a verb ("list"), is still only the message. A null or blank message is <c>--clear</c>.
    /// </summary>
    public static string[] BlockMessage(string? message, string? connector, bool restart)
    {
        var argv = new List<string> { "guardrail", "block-message" };
        var clear = string.IsNullOrWhiteSpace(message);
        if (clear)
        {
            argv.Add("--clear");
        }

        argv.Add("--yes");
        Finish(argv, connector, restart);

        if (!clear)
        {
            argv.Add("--");
            argv.Add(message!.Trim());
        }

        return argv.ToArray();
    }

    /// <summary><c>guardrail judge add CONNECTOR [--enable] [--timeout S]</c>.</summary>
    public static string[] JudgeAdd(string connector, bool alsoEnableJudge, double? timeoutSeconds, bool restart)
    {
        var argv = new List<string> { "guardrail", "judge", "add", connector.Trim() };
        if (alsoEnableJudge)
        {
            argv.Add("--enable");
        }

        if (timeoutSeconds is { } seconds)
        {
            argv.Add("--timeout");
            argv.Add(seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        return Finish(argv, null, restart);
    }

    /// <summary><c>guardrail judge remove CONNECTOR</c>.</summary>
    public static string[] JudgeRemove(string connector, bool restart) =>
        Finish(new List<string> { "guardrail", "judge", "remove", connector.Trim() }, null, restart);

    /// <summary>The three read-only verbs, in the order the view-model reads them.</summary>
    public static string[] ReadHilt() => new[] { "guardrail", "hilt" };

    public static string[] ReadBlockMessage() => new[] { "guardrail", "block-message" };

    public static string[] ReadJudge() => new[] { "guardrail", "judge", "list" };

    private static string[] Finish(List<string> argv, string? connector, bool restart)
    {
        if (!string.IsNullOrWhiteSpace(connector))
        {
            argv.Add("--connector");
            argv.Add(connector.Trim());
        }

        if (!restart)
        {
            argv.Add("--no-restart");
        }

        return argv.ToArray();
    }
}
