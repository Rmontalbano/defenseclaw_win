using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Services;

/// <summary>What the shell can ask <c>defenseclaw-gateway</c> to do to the sidecar daemon.</summary>
public enum GatewayAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// The one description of the shell's gateway controls — argv, wording, and when each is
/// available — shared by the tray menu and the command palette so they can never disagree.
/// <para>
/// Verified against <c>defenseclaw-gateway --help</c> (0.8.10): <c>start</c> ("Start the gateway
/// sidecar as a background daemon"), <c>stop</c> ("Stop the running gateway sidecar daemon") and
/// <c>restart</c> ("Restart the gateway sidecar daemon", equivalent to stop then start) each take no
/// arguments beyond <c>-h</c>. All three classify as <c>StateChanging</c> in
/// <c>CommandTiers</c>, so every surface reviews the exact argv before it runs.
/// </para>
/// </summary>
internal static class GatewayControl
{
    /// <summary>The executable the argv is handed to (see <c>CliRunner.RunGatewayAsync</c>).</summary>
    public const string Executable = "defenseclaw-gateway";

    public static string Verb(GatewayAction action) => action switch
    {
        GatewayAction.Start => "start",
        GatewayAction.Stop => "stop",
        _ => "restart",
    };

    /// <summary>The argv (without the executable) for <paramref name="action"/>.</summary>
    public static IReadOnlyList<string> Argv(GatewayAction action) => new[] { Verb(action) };

    /// <summary>The whole command as a reviewer reads it, e.g. <c>defenseclaw-gateway restart</c>.</summary>
    public static string CommandText(GatewayAction action) => $"{Executable} {Verb(action)}";

    /// <summary>Menu / palette / dialog title.</summary>
    public static string Title(GatewayAction action) => action switch
    {
        GatewayAction.Start => "Start gateway",
        GatewayAction.Stop => "Stop gateway",
        _ => "Restart gateway",
    };

    /// <summary>What a tooltip or palette row says the action does, before any review.</summary>
    public static string Summary(GatewayAction action) => action switch
    {
        GatewayAction.Start => "Start the DefenseClaw gateway as a background daemon.",
        GatewayAction.Stop => "Stop the DefenseClaw gateway daemon.",
        _ => "Stop and start the DefenseClaw gateway daemon.",
    };

    /// <summary>
    /// The review text under the command. A restart says the required sentence verbatim; stop and
    /// restart both say what the hooks do meanwhile, because that is what an operator is really
    /// weighing (the hooks obey the configured fail mode whenever the gateway is unreachable).
    /// </summary>
    public static string ReviewNote(GatewayAction action) => action switch
    {
        GatewayAction.Start => "This starts the DefenseClaw gateway as a background daemon.",
        GatewayAction.Stop =>
            "This stops the DefenseClaw gateway. It stays stopped until it is started again, and hooks " +
            "that call it follow the configured fail mode while it is down.",
        _ =>
            "This restarts the DefenseClaw gateway. Hooks that call it follow the configured fail mode " +
            "for the moment it is down.",
    };

    /// <summary>The past-tense result line for a toast.</summary>
    public static string SucceededText(GatewayAction action) => $"{CommandText(action)} succeeded.";

    /// <summary>
    /// <see cref="Availability(GatewayAction, GatewaySnapshot)"/> on an installation that may not be changed: start, stop and restart are changes
    /// (<see cref="InstallationGate"/>), so a managed or invalid installation is told so before the gateway's own state is considered. The
    /// reason is the installation's sentence.
    /// </summary>
    public static (bool Allowed, string? Reason) Availability(GatewayAction action, GatewaySnapshot snapshot, InstallationGuard installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        return installation.ReasonFor(Executable, Argv(action)) is { } blocked
            ? (false, blocked)
            : Availability(action, snapshot);
    }

    /// <summary>
    /// Whether <paramref name="action"/> makes sense for <paramref name="snapshot"/>, and if not, why.
    /// The reason is written for a tooltip or a palette row.
    /// </summary>
    public static (bool Allowed, string? Reason) Availability(GatewayAction action, GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (InstallProblem(snapshot) is { } problem)
        {
            return (false, problem);
        }

        return action switch
        {
            GatewayAction.Start when snapshot.IsRunning => (false, "The gateway is already running."),
            GatewayAction.Stop when !snapshot.IsRunning => (false, "The gateway is not running."),
            _ => (true, null),
        };
    }

    /// <summary>
    /// Whether a command that talks to the running sidecar (<c>defenseclaw-gateway policy reload</c> posts to its API) makes sense for
    /// <paramref name="snapshot"/>, and if not, why. The same install-state reasons as <see cref="Availability"/>, then
    /// <paramref name="whenStopped"/> while the gateway is not running.
    /// </summary>
    public static (bool Allowed, string? Reason) AvailabilityWhileRunning(GatewaySnapshot snapshot, string whenStopped)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (InstallProblem(snapshot) is { } problem)
        {
            return (false, problem);
        }

        return snapshot.IsRunning ? (true, null) : (false, whenStopped);
    }

    /// <summary>Why nothing can be asked of the gateway yet or at all (not known, not installed, not initialized), or null.</summary>
    private static string? InstallProblem(GatewaySnapshot snapshot) => snapshot.Install switch
    {
        null => "Still checking the gateway; try again in a moment.",
        InstallState.NotInstalled => "DefenseClaw is not installed on this machine.",
        InstallState.InstalledNotInitialized => "DefenseClaw is not initialized yet. Run 'defenseclaw init' first.",
        _ => null,
    };
}
