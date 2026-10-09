using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The runtime's master notification switch, <c>defenseclaw setup notifications on|off</c>, as a reviewed action - the one the Overview's
/// "Turn notifications off / on" button opens (CUST-274), and the one the notification routing dialog (CUST-271) opens from its first row. The
/// question, the consequence text and the step are written once, here, so the two cannot drift apart and there is no second master switch.
/// <para>
/// <b>Whose notifications these are.</b> The switch writes <c>notifications.enabled</c> in config.yaml and restarts the gateway, whose
/// notification dispatcher reads it once when it starts. They are DefenseClaw's own desktop notifications, raised by the gateway for blocked
/// tool calls and pending approvals. The alerts this app shows from the tray (Settings, Notifications: CRITICAL, HIGH, gateway offline) are a
/// separate setting that this does not change - so one block can raise both, and turning one off leaves the other as it is. The consequence text
/// says so, because the two are easy to take for one.
/// </para>
/// </summary>
internal static class NotificationSwitch
{
    /// <summary>What the button runs while the notifications are off. <c>setup notifications</c> takes <c>on | off | status</c>; with one of them it asks nothing.</summary>
    internal static readonly string[] OnArgv = { "setup", "notifications", "on" };

    /// <summary>What the button runs while they are on.</summary>
    internal static readonly string[] OffArgv = { "setup", "notifications", "off" };

    /// <summary>The command that turns the switch on or off.</summary>
    internal static string[] Argv(bool turnOn) => turnOn ? OnArgv : OffArgv;

    /// <summary>The review's question.</summary>
    internal static string Heading(bool turnOn) => turnOn ? "Turn desktop notifications on?" : "Turn desktop notifications off?";

    /// <summary>The consequence text: what is written, the restart, whose notifications these are, and that the tray alerts are separate.</summary>
    internal static string Explanation(bool turnOn) =>
        $"Sets notifications.enabled to {(turnOn ? "true" : "false")} in config.yaml and restarts the gateway, whose notification dispatcher reads it once when it starts. " +
        "These are DefenseClaw's own desktop notifications for blocked tool calls and pending approvals. " +
        "The alerts this app shows from the tray are a separate setting (Settings, Notifications) and do not change.";

    /// <summary>The step's sentence.</summary>
    internal static string Purpose(bool turnOn) =>
        turnOn ? "Turn the runtime's desktop notifications on." : "Turn the runtime's desktop notifications off.";

    /// <summary>The confirm button's text.</summary>
    internal static string PrimaryText(bool turnOn) => turnOn ? "Turn on" : "Turn off";

    /// <summary>Opens the review of turning the switch <paramref name="turnOn"/>; nothing runs until it is confirmed.</summary>
    internal static void Open(DiscoverActionReview review, bool turnOn, Func<DiscoverReviewResult, Task>? onFinished = null)
    {
        ArgumentNullException.ThrowIfNull(review);

        review.Open(
            Heading(turnOn),
            Explanation(turnOn),
            new[] { new DiscoverStep(Argv(turnOn), Purpose(turnOn), CommandTier.StateChanging) },
            onFinished: onFinished,
            restartsGateway: true,
            primaryText: PrimaryText(turnOn));
    }
}
