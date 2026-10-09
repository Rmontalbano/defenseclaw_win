using System.Globalization;
using System.IO;
using System.Windows;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// Whether an Activity entry can be run again, as the entry's Rerun button and the palette's "Re-run last command" ask it.
/// </summary>
/// <param name="Offered">False when the entry has no Rerun at all (the button is not drawn): see <see cref="CommandRerun.StateOf"/> for which.</param>
/// <param name="Enabled">
/// False while the entry is still running, or while it would change an installation that is read-only: the button is drawn, off, with
/// <paramref name="Hint"/> for its tooltip.
/// </param>
/// <param name="Hint">One sentence: what Rerun does, or the one reason it is off or missing.</param>
internal readonly record struct RerunAvailability(bool Offered, bool Enabled, string Hint)
{
    public static RerunAvailability No(string reason) => new(false, false, reason);
}

/// <summary>
/// Runs an Activity entry again (CUST-264): the Rerun button of a row, and the command palette's "Re-run last command". It never runs
/// anything on its own say. The entry's exact argv goes into the shared review (<see cref="CommandReview"/>, the same dialog the tray and the
/// palette use) with the tier the entry had - a destructive entry opens with focus on Cancel and a danger-styled confirm - and only a confirmed
/// review reaches <see cref="CliRunner"/>, which records the new run in Activity like any other.
/// <para>
/// <b>What can be run again.</b> A <c>defenseclaw</c> or <c>defenseclaw-gateway</c> command with an argv, whose run left nothing behind that the
/// argv does not hold. That leaves out: an entry that carried a secret on stdin or in its environment (the app never stores the value, so the
/// run would not be the same one), the app's own signature checks and the upgrade installer (not DefenseClaw commands), a command that was handed
/// to a console (<c>keys set</c>: nothing ran here), a refusal (<see cref="CliRunner.RecordRefusal"/>: the list it was reviewed on was out of
/// date, and the panel it came from is the place to ask again), and any run of <c>defenseclaw-gateway</c> with no argument, which would start a
/// second sidecar daemon. A command that is still running has Rerun, off.
/// </para>
/// <para>
/// <b>A read-only installation (CUST-308)</b> - a managed one, or one that is not valid - is changed by nothing here. The entry's Rerun is
/// off whenever running it again would be a change (<see cref="InstallationGuard.ReasonFor(string, IReadOnlyList{string})"/>: a state-changing
/// or destructive command; a read still runs), with the installation's sentence as the only reason it gives, ahead of "still running". The
/// review <see cref="ReviewFor"/> builds carries that sentence too (<see cref="CommandReview.GuardedBy"/>: a bar, and Confirm off), the answer
/// is asked again once the operator confirms (the file may have changed while the review was open), and the runner refuses the run whatever
/// this class did.
/// </para>
/// <para>
/// <b>The gateway's own start, stop and restart</b> are run the way the tray runs them (the same review text; a confirmed stop is the operator's
/// word, so the automatic start does not undo it; the status strip is refreshed afterwards).
/// </para>
/// </summary>
internal sealed class CommandRerun
{
    private readonly AppServices _services;
    private readonly Func<Window?> _owner;
    private int _busy;

    /// <param name="services">The runner, the gateway state and the shared Docker look.</param>
    /// <param name="owner">The window to centre the review over; null uses whichever window is active.</param>
    public CommandRerun(AppServices services, Func<Window?>? owner = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _owner = owner ?? ActiveWindow;
    }

    /// <summary>Test seam: shows a review and says whether the operator confirmed it. Null shows the real dialog.</summary>
    internal Func<CommandReview, bool>? Confirmer { get; set; }

    /// <summary>Test seam: runs the confirmed command instead of the runner, so a test sees the exact tool and argv and starts nothing.</summary>
    internal Func<string, IReadOnlyList<string>, CliRunOptions?, Task<CliInvocation>>? Runner { get; set; }

    /// <summary>Test seam: what refreshes the gateway status after the gateway's start, stop or restart. Null asks the monitor for a poll.</summary>
    internal Func<Task>? RefreshGateway { get; set; }

    private static Window? ActiveWindow() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    // ------------------------------------------------------------------ what can be run again

    /// <summary>The tool an entry ran - <c>defenseclaw</c> or <c>defenseclaw-gateway</c> - or null when it is neither (cosign, the installer, docker).</summary>
    internal static string? ToolOf(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var name = Path.GetFileNameWithoutExtension(invocation.Executable);
        if (string.Equals(name, CommandReview.DefaultExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return CommandReview.DefaultExecutable;
        }

        return string.Equals(name, GatewayControl.Executable, StringComparison.OrdinalIgnoreCase) ? GatewayControl.Executable : null;
    }

    /// <summary>
    /// The installation's sentence when running <paramref name="invocation"/> again would change a read-only installation - the entry is a
    /// state-changing or destructive command and the installation is managed or not valid - and null otherwise: for a read, whatever the
    /// installation, for any command while it is writable, and for an entry that has no Rerun to be blocked. It is what the row hands
    /// <see cref="StateOf"/>; the rule is the runner's (<see cref="InstallationGate"/>), not a second one.
    /// </summary>
    internal static string? InstallationBlockOf(CliInvocation invocation, InstallationGuard installation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(installation);

        return ToolOf(invocation) is { } tool && invocation.Argv.Count > 0 ? installation.ReasonFor(tool, invocation.Argv) : null;
    }

    /// <summary>Whether, and with which sentence, <paramref name="invocation"/> can be run again.</summary>
    /// <param name="invocation">The entry.</param>
    /// <param name="installationBlock">
    /// What <see cref="InstallationBlockOf"/> says of it: the sentence of a read-only installation that running the entry again would change,
    /// or null. An entry that has a Rerun at all then has it off with this as the only reason, which comes before "still running" (one reason
    /// per control, the installation's first). An entry that has none keeps the sentence that says why.
    /// </param>
    public static RerunAvailability StateOf(CliInvocation invocation, string? installationBlock = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (ToolOf(invocation) is not { } tool || invocation.SurvivesShutdown)
        {
            return RerunAvailability.No("Only DefenseClaw commands can be run again from here.");
        }

        if (invocation.UsedStdinSecret || invocation.EnvironmentNames.Count > 0)
        {
            return RerunAvailability.No("A secret was supplied to this run and the app never keeps it, so it cannot be run again from here.");
        }

        if (invocation.Argv.Count == 0)
        {
            return RerunAvailability.No(string.Equals(tool, GatewayControl.Executable, StringComparison.Ordinal)
                ? "A bare defenseclaw-gateway starts a second gateway daemon, so it is never run from here."
                : "There is no command to run again.");
        }

        if (invocation.FailureReason is { } reason && reason.StartsWith(CliRunner.RefusedPrefix, StringComparison.Ordinal))
        {
            return RerunAvailability.No("This was refused before it started. Ask again from the panel it came from, once its list is current.");
        }

        if (invocation is { IsRunning: false, ExitCode: null, FailureReason: null })
        {
            return RerunAvailability.No("This was handed to a terminal and never ran here.");
        }

        // A read-only installation: nothing about the entry (running or not) can make this runnable, so it is the one thing said.
        if (installationBlock is { Length: > 0 })
        {
            return new RerunAvailability(true, false, installationBlock);
        }

        return invocation.IsRunning
            ? new RerunAvailability(true, false, "Wait for this command to finish, or cancel it, before running it again.")
            : new RerunAvailability(true, true, "Review this command and run it again. Nothing runs until you confirm.");
    }

    /// <summary>The tier the entry shows and its rerun is reviewed at: what the classifier says of the argv.</summary>
    internal static CommandTier TierOf(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return CommandReview.ResolveTier(invocation.Argv);
    }

    /// <summary>The newest entry that can be run again (running or not), or null. Newest by start time, as "last command output" is.</summary>
    public static CliInvocation? LastOffered(IReadOnlyList<CliInvocation> activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return activity.Where(i => StateOf(i).Offered).MaxBy(i => i.StartedAt);
    }

    private static GatewayAction? LifecycleOf(string tool, IReadOnlyList<string> argv) =>
        string.Equals(tool, GatewayControl.Executable, StringComparison.Ordinal) && argv.Count == 1
            ? argv[0] switch
            {
                "start" => GatewayAction.Start,
                "stop" => GatewayAction.Stop,
                "restart" => GatewayAction.Restart,
                _ => null,
            }
            : null;

    // ------------------------------------------------------------------ the review

    /// <summary>
    /// The review of running <paramref name="invocation"/> again: its exact argv on its own tool, at <paramref name="tier"/> (the entry's) or
    /// the classifier's if that is stricter, with the warnings the same command carries anywhere else (a gateway restart, Docker's cautions).
    /// Like every review, it is guarded by the installation (<see cref="CommandReview.GuardedBy"/>): on a read-only one, a state-changing or
    /// destructive entry's review carries the installation's reason - the shared control draws it as a bar and keeps Confirm off - while a
    /// read's review is the same as ever.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry is not one <see cref="StateOf"/> offers.</exception>
    public CommandReview ReviewFor(CliInvocation invocation, CommandTier? tier = null)
    {
        if (!StateOf(invocation).Offered || ToolOf(invocation) is not { } tool)
        {
            throw new InvalidOperationException("This entry cannot be run again from here.");
        }

        var argv = invocation.Argv.ToArray();
        var step = new CommandReviewStep(argv, floor: tier ?? CommandTier.ReadOnly, executable: tool);

        var lifecycle = LifecycleOf(tool, argv);
        var restarts = lifecycle == GatewayAction.Restart ||
                       (string.Equals(tool, CommandReview.DefaultExecutable, StringComparison.Ordinal) && CommandReview.RestartsGatewayFor(argv));

        var warnings = new List<CommandReviewWarning>();
        if (restarts)
        {
            warnings.Add(CommandReviewWarning.GatewayRestart());
        }

        if (string.Equals(tool, CommandReview.DefaultExecutable, StringComparison.Ordinal))
        {
            warnings.AddRange(LocalStackReview.Warnings(argv, _services.LocalStack.Status));
        }

        return new CommandReview
        {
            Title = $"Run {CommandReview.CommandLine(tool, argv)} again?",
            Summary = SummaryFor(tool, argv, lifecycle, invocation),
            Steps = new[] { step },
            RestartsGateway = restarts,
            Warnings = warnings,
        }.GuardedBy(_services.Installation);
    }

    private static string SummaryFor(string tool, IReadOnlyList<string> argv, GatewayAction? lifecycle, CliInvocation invocation)
    {
        // What the command does, where the app knows it in words; then the one sentence every rerun carries.
        var what = lifecycle is { } action
            ? GatewayControl.ReviewNote(action)
            : string.Equals(tool, GatewayControl.Executable, StringComparison.Ordinal)
                ? GatewayVerbs.SummaryOf(argv)
                : LocalStackReview.Summary(argv);

        var again = $"This runs the same command again, exactly as it ran at {invocation.StartedAt.ToLocalTime().ToString("MMM d HH:mm:ss", CultureInfo.CurrentCulture)} ({Outcome(invocation)}).";
        return what.Length == 0 ? again : what + " " + again;
    }

    /// <summary>How the entry ended, in a few words: <c>exit 0</c>, <c>exit 1</c>, <c>timed out after 120 s</c>, <c>cancelled</c>.</summary>
    internal static string Outcome(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (invocation.IsRunning)
        {
            return "still running";
        }

        if (invocation.ExitCode is { } code)
        {
            return "exit " + code.ToString(CultureInfo.InvariantCulture);
        }

        return invocation.FailureReason is { Length: > 0 } reason
            ? (reason.IndexOf(" — ", StringComparison.Ordinal) is var cut and > 0 ? reason[..cut] : reason)
            : "no exit code";
    }

    // ------------------------------------------------------------------ the run

    /// <summary>
    /// Reviews <paramref name="invocation"/> and, if the operator confirms, runs it again. Returns the sentence to show: what happened, why
    /// nothing did, or an empty string when the operator declined (declining says nothing). Never throws for a command that cannot start.
    /// </summary>
    /// <param name="tier">The tier the entry showed; null is what the classifier says.</param>
    public async Task<string> RunAsync(CliInvocation invocation, CommandTier? tier = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        // A read-only installation is said first and once, as the button does: a Rerun that arrived by a row drawn before the installation
        // turned read-only (or the palette's, opened before) gets the sentence and no review to look at.
        var state = StateOf(invocation, InstallationBlockOf(invocation, _services.Installation));
        if (!state.Offered || !state.Enabled || ToolOf(invocation) is not { } tool)
        {
            return state.Hint;
        }

        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return "Another command is waiting for your answer or still being started. Try again in a moment.";
        }

        try
        {
            var review = ReviewFor(invocation, tier);
            var confirmed = Confirmer is { } confirm ? confirm(review) : GatewayActionDialog.Confirm(_owner(), review);
            if (!confirmed)
            {
                return string.Empty;
            }

            var argv = invocation.Argv.ToArray();
            var lifecycle = LifecycleOf(tool, argv);

            // The review may have stood open while config.yaml turned the installation read-only. The runner would refuse the run as well, but
            // not before a confirmed stop was taken for the operator's word that the gateway stays down, which nothing here stopped.
            if (_services.Installation.ReasonFor(tool, argv) is not null)
            {
                return Refused(tool, argv);
            }

            // A confirmed stop is the operator's word for the rest of the session: no automatic start follows it.
            if (lifecycle == GatewayAction.Stop)
            {
                _services.GatewayAutoStart.MarkUserStopped();
            }

            // The target of a name the operator reviewed must be the one that runs: the runner refuses a name the CLI would rewrite.
            var options = (invocation.RetainsFullOutput ? CliRunOptions.JsonRead : CliRunOptions.Default) with { RefuseExpandingTargets = true };
            var again = await (Runner is { } run
                ? run(tool, argv, options)
                : _services.Cli.RunNamedAsync(tool, argv, options: options)).ConfigureAwait(true);

            if (lifecycle is not null)
            {
                await RefreshGatewayStatusAsync().ConfigureAwait(true);
            }

            return Describe(tool, argv, again);
        }
#pragma warning disable CA1031 // A command that cannot start (no CLI, a name the CLI would rewrite, a secret in the argv) is reported, not thrown into a button.
        catch (Exception ex)
        {
            return ex is CliNotFoundException or ArgumentExpansionException or SecretInArgumentException
                ? ex.Message
                : $"Could not run it: {ex.Message}";
        }
#pragma warning restore CA1031
        finally
        {
            _ = Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Ends a confirmed review that must not run: the installation turned read-only while it was open. The command is recorded in Activity as
    /// refused, the entry the runner makes for a run it declines (<see cref="CliRunner.RecordRefusal"/>), and the sentence says so.
    /// </summary>
    private string Refused(string tool, IReadOnlyList<string> argv)
    {
        var installation = _services.Installation.Context;
        try
        {
            _ = _services.Cli.RecordRefusal(tool, argv, InstallationGate.RefusalReason(installation));
        }
        catch (SecretInArgumentException)
        {
            // A command line that holds a known secret is never recorded; the sentence below is still said.
        }

        return NotRun(installation.BlockedReason);
    }

    /// <summary>The sentence for a re-run that did not start because the installation is read-only: nothing ran, nothing changed.</summary>
    private static string NotRun(string? installationReason) =>
        $"Not run. {installationReason ?? "This installation is read only."} Nothing was changed; the refusal is in the Activity list.";

    private async Task RefreshGatewayStatusAsync()
    {
        try
        {
            await (RefreshGateway is { } refresh ? refresh() : _services.Monitor.RefreshAsync()).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed poll is reported by the status strip itself.
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Gateway refresh after a re-run failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    /// <summary>The sentence for a re-run that started: which command, how it ended, and where to look.</summary>
    internal static string Describe(string tool, IReadOnlyList<string> argv, CliInvocation again)
    {
        // The runner declined it (a read-only installation, asked a moment after this class last looked): it never started, and it says why.
        if (again.FailureReason is { } failure && failure.StartsWith(CliRunner.RefusedPrefix, StringComparison.Ordinal))
        {
            var cut = failure.IndexOf(" — ", StringComparison.Ordinal);
            return NotRun(cut >= 0 ? failure[(cut + 3)..] : failure);
        }

        var command = CommandReview.CommandLine(tool, argv);
        var how = again.ExitCode == 0 ? "finished (exit 0)" : again.ExitCode is { } code
            ? $"exited with code {code.ToString(CultureInfo.InvariantCulture)}"
            : (again.FailureReason ?? "did not finish");
        return $"Ran {command} again: {how}. The new entry is at the top of the Activity list.";
    }
}
