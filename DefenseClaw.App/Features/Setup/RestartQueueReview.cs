using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "Restart now" for the restart queue (CUST-267): the reviewed <c>defenseclaw-gateway restart</c> the tray, the palette and the Overview's gateway
/// button already run, opened from the Setup hub's banner and the Overview's attention row. One place, so the two cannot differ: the same exact
/// argv (<see cref="GatewayControl.Argv"/>: <c>restart</c> on <c>defenseclaw-gateway</c>, never a bare <c>defenseclaw-gateway</c>, which would
/// start a second daemon), the same availability (<see cref="GatewayControl.Availability(GatewayAction, GatewaySnapshot, InstallationGuard)"/>:
/// the installation's sentence first, then whether DefenseClaw is installed and initialized), and the same review (<see cref="DiscoverActionReview"/>:
/// Confirm is off for a read-only installation, asks the live answer again when it is pressed, and records a refusal in Activity if the
/// installation turned read-only while the review was open).
/// <para>
/// Nothing here clears the queue. The run goes through <see cref="CliRunner"/>, which tells <see cref="RestartQueue.Note"/> that the gateway was
/// restarted, and the monitor poll that follows reports the new start time.
/// </para>
/// </summary>
internal static class RestartQueueReview
{
    /// <summary>The review's question.</summary>
    public const string Heading = "Restart the gateway now?";

    /// <summary>How much of the queued reasons the review's explanation quotes.</summary>
    private const int MaxReasonCharacters = 400;

    /// <summary>
    /// Why "Restart now" is off right now - the installation is managed or invalid, or the gateway cannot be asked yet or at all - as one sentence for a
    /// tooltip; null while it may be pressed. The installation's sentence comes before any other.
    /// </summary>
    /// <param name="services">The composition.</param>
    /// <param name="snapshot">The gateway state the asking panel is drawn from; the monitor's current one when null.</param>
    public static string? BlockedReason(AppServices services, GatewaySnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var (allowed, reason) = GatewayControl.Availability(GatewayAction.Restart, snapshot ?? services.Monitor.Current, services.Installation);
        return allowed ? null : reason ?? "Not available right now.";
    }

    /// <summary>
    /// Opens the review of the restart in <paramref name="review"/> (the panel's own overlay) and returns true; nothing is opened, and false is
    /// returned, while <see cref="BlockedReason"/> says it is off. Nothing runs until the operator confirms in the review.
    /// </summary>
    /// <param name="review">The panel's confirm-and-run overlay.</param>
    /// <param name="services">The composition.</param>
    /// <param name="afterRun">What the panel refreshes once the run has finished, after the monitor has been polled.</param>
    /// <param name="snapshot">The gateway state the asking panel is drawn from; the monitor's current one when null.</param>
    public static bool Open(DiscoverActionReview review, AppServices services, Func<Task>? afterRun = null, GatewaySnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(services);

        if (BlockedReason(services, snapshot) is not null)
        {
            return false;
        }

        var reasons = services.RestartQueue.Reason;
        if (reasons.Length > MaxReasonCharacters)
        {
            reasons = reasons[..(MaxReasonCharacters - 1)].TrimEnd() + "…";
        }

        review.Open(
            Heading,
            (reasons.Length == 0 ? string.Empty : $"Applies what was saved without a restart: {reasons.TrimEnd('.', ' ')}. ") + GatewayControl.ReviewNote(GatewayAction.Restart),
            new[]
            {
                new DiscoverStep(
                    GatewayControl.Argv(GatewayAction.Restart),
                    GatewayControl.Summary(GatewayAction.Restart),
                    CommandTier.StateChanging,
                    Executable: GatewayControl.Executable),
            },
            onFinished: async finished =>
            {
                // The poll is what reports the start time that applies the queue; the run has told the queue already, and this makes the shell follow.
                _ = await services.Monitor.RefreshAsync().ConfigureAwait(true);
                if (afterRun is not null)
                {
                    await afterRun().ConfigureAwait(true);
                }
            },
            restartsGateway: true,
            primaryText: GatewayControl.Title(GatewayAction.Restart));
        return true;
    }
}
