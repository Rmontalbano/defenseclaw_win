using System.IO;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The buttons of the durability rows in "What needs attention" (CUST-352). Every one opens the shared review first and runs nothing until it is
/// confirmed; the installation guard's sentence comes before any other reason a button is off. Two of them are commands of the DefenseClaw CLI
/// (<c>setup codex --yes --mode &lt;current&gt;</c>, <c>guardrail fail-mode open --yes</c>); the other two are <b>file operations done by this app</b>,
/// and their review says so: moving a stuck pending write aside, and putting it back. DefenseClaw 1.0 has no command for that recovery.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The "executable" a file operation shows in its review and in Activity: this app, not a DefenseClaw program.</summary>
    internal const string AppActor = "DefenseClaw for Windows";

    /// <summary>
    /// Why the button <paramref name="id"/> is off, or null while it may be pressed: the installation's sentence first, then, for the two file
    /// operations, a running gateway (it may be writing the very files that would be moved).
    /// </summary>
    internal string? AttentionActionBlockedReason(string id, GatewaySnapshot snapshot)
    {
        if (InstallationBlockedReason is { } installation)
        {
            return installation;
        }

        return id is AttentionActionIds.ClearStuckWrite or AttentionActionIds.PutBack && snapshot.IsRunning
            ? "Stop the gateway first: it may be writing these files right now."
            : null;
    }

    /// <summary>A button on an attention row: opens its review. Does nothing for an id it does not know or one that is off now.</summary>
    [RelayCommand]
    private async Task RunAttentionAction(string? id)
    {
        if (string.IsNullOrEmpty(id) || AttentionActionBlockedReason(id, _snapshot) is not null)
        {
            return;
        }

        switch (id)
        {
            case AttentionActionIds.ReselectCodex:
                await ReselectCodexAsync().ConfigureAwait(true);
                break;
            case AttentionActionIds.ClearStuckWrite:
                ClearStuckWrite();
                break;
            case AttentionActionIds.PutBack:
                PutBackQuarantine();
                break;
            case AttentionActionIds.SwitchToFailOpen:
                SwitchToFailOpen();
                break;
            case OpenInstallerActionId:
                OpenInstallerForLock();
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// "Re-select Codex": the review of <c>defenseclaw setup codex --yes --mode &lt;current&gt;</c>. The mode is read now (what the gateway last logged
    /// for Codex, then config.yaml, then observe) and always on the command line, because <c>setup codex --yes</c> alone switched a Codex that was
    /// observing to action. The "Codex is running" bar comes with the review (<see cref="DiscoverActionReview"/>).
    /// </summary>
    internal async Task ReselectCodexAsync()
    {
        var logPath = Services.Paths.GatewayLogPath;
        var lines = await Task.Run(() => GatewayLogClassifier.ReadTail(logPath)).ConfigureAwait(true);
        _ = Services.Config.Config.Guardrail.Connectors.TryGetValue("codex", out var codex);
        var (mode, source) = DurabilityAttention.ResolveCodexMode(lines, codex?.Mode, DefenseClaw.Core.Runtime.ObserveModeCopy.BlocksSelfChange(Services.Runtime.Capabilities));
        var argv = DurabilityAttention.ReselectArgv(mode);

        Review.Open(
            "Re-select Codex?",
            $"Runs setup for Codex again so DefenseClaw finds the Codex that is installed now and registers its hooks with it. " +
            $"The mode is passed explicitly - {mode}, from {source} - because setup without it has switched Codex from observe to action. " +
            "Quit Codex first.",
            new[]
            {
                new DiscoverStep(argv, "Find Codex's current executable and re-register its hooks.", CommandTier.StateChanging, CliRunner.LongRunningTimeout),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            restartsGateway: CommandReview.RestartsGatewayFor(argv),
            primaryText: "Re-select Codex");
    }

    /// <summary>
    /// "Clear the stuck pending write": the review of a <b>file operation</b> that moves the pending receipt files and their staging files aside.
    /// The review lists every path; the folder is dated and holds a manifest.json. Refused again at the moment of running if the gateway is up.
    /// </summary>
    private void ClearStuckWrite()
    {
        var pending = _snapshot.Durability?.PendingWrites;
        if (pending is not { Count: > 0 })
        {
            return;
        }

        var ids = pending.Select(static p => p.Id).ToArray();
        var files = pending.SelectMany(static p => p.AllFiles).ToArray();
        var root = Services.CasQuarantineRoot;
        var argv = new List<string> { "move-aside" };
        argv.AddRange(files);
        argv.Add("to");
        argv.Add(Path.Combine(root, "<time>"));

        Review.Open(
            "Clear the stuck pending write?",
            "Moves the files below out of the way so DefenseClaw can write the config again. They are moved, never deleted: the folder gets a manifest.json " +
            "with every original path, size and SHA-256, and Put back restores them. Only the pending receipt files of the writes listed, and the staging files " +
            "beside their target, are moved; finished writes and the config file itself are not touched. Afterwards Re-run Codex setup is offered on the row." +
            (DurabilityAttention.StateSentence(pending) is { Length: > 0 } states ? " " + states : string.Empty),
            new[]
            {
                new DiscoverStep(
                    argv,
                    "Move the pending receipt and staging files into a dated quarantine folder.",
                    CommandTier.StateChanging,
                    Executable: AppActor,
                    Run: () => RunMoveAsideAsync(ids, argv, root)),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            extraWarnings: ClearWarnings(pending),
            primaryText: "Move the files aside");
    }

    /// <summary>
    /// The review's warning bars: always that this is a file operation; and, when the hash check could not tell what a write amounts to (CUST-360), a
    /// stronger one that says why and asks the operator to look first. The move itself does not change: it stays available and reversible.
    /// </summary>
    internal static IReadOnlyList<CommandReviewWarning> ClearWarnings(IReadOnlyList<CasPendingWrite> pending)
    {
        var warnings = new List<CommandReviewWarning>
        {
            new(
                "A file operation, not a DefenseClaw command",
                "DefenseClaw has no command that recovers from a stuck pending write, so this app moves the files itself - the same move-aside that was done by hand " +
                "on 2026-10-15. Nothing here is run by the DefenseClaw CLI. The gateway must be stopped; the move is refused if it is running."),
        };

        if (CasWriteText.AnyUnverified(pending))
        {
            warnings.Add(new CommandReviewWarning(
                "Not every pending write could be verified",
                "The hash check could not tell whether this write finished, never applied or was rewritten since (" + CasWriteText.UnverifiedReasons(pending) + "). " +
                "Open the target file and the staging files listed below and make sure nothing you need is only in them before you move them aside. " +
                "The move is still reversible with Put back."));
        }

        return warnings;
    }

    private async Task<CliInvocation> RunMoveAsideAsync(IReadOnlyCollection<string> ids, IReadOnlyList<string> argv, string root)
    {
        // The state is asked again now, not taken from the poll before the review opened: the gateway may have been started while it was read.
        var running = (await Services.Monitor.RefreshAsync().ConfigureAwait(true)).IsRunning;
        var dataDirectory = Services.Paths.DataDirectory;
        var result = await Task.Run(() => CasQuarantine.MoveAside(dataDirectory, ids, root, DateTimeOffset.UtcNow, running)).ConfigureAwait(true);
        return RecordFileOperation(argv, result);
    }

    /// <summary>"Put back": the review of a file operation that restores what <see cref="ClearStuckWrite"/> moved, from its manifest.</summary>
    private void PutBackQuarantine()
    {
        if (_snapshot.Durability?.Quarantine is not { } quarantine)
        {
            return;
        }

        var argv = new[] { "put-back", "from", quarantine.Directory };
        Review.Open(
            "Put the moved files back?",
            $"Moves the {quarantine.Files.ToString(System.Globalization.CultureInfo.CurrentCulture)} file(s) listed in {Path.Combine(quarantine.Directory, CasQuarantine.ManifestName)} " +
            "back to where they came from, if they are unchanged (SHA-256) and the place is free. That puts the stuck pending write back too.",
            new[]
            {
                new DiscoverStep(
                    argv,
                    "Restore the quarantined files to their original paths.",
                    CommandTier.StateChanging,
                    Executable: AppActor,
                    Run: () => RunPutBackAsync(quarantine.Directory, argv)),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            extraWarnings: new[]
            {
                new CommandReviewWarning("A file operation, not a DefenseClaw command", "This app moves the files itself. The gateway must be stopped; it is refused if it is running."),
            },
            primaryText: "Put back");
    }

    private async Task<CliInvocation> RunPutBackAsync(string directory, IReadOnlyList<string> argv)
    {
        var running = (await Services.Monitor.RefreshAsync().ConfigureAwait(true)).IsRunning;
        var result = await Task.Run(() => CasQuarantine.PutBack(directory, running)).ConfigureAwait(true);
        return RecordFileOperation(argv, result);
    }

    /// <summary>A file operation in Activity: an entry born and finished here, with the result as its output and its failure when it did not work.</summary>
    private CliInvocation RecordFileOperation(IReadOnlyList<string> argv, CasQuarantineResult result)
    {
        var entry = Services.Cli.BeginExternalRun(AppActor, argv, result.Message);
        Services.Cli.CompleteExternalRun(entry, result.Succeeded ? 0 : null, result.Succeeded ? null : result.Message, null);
        return entry;
    }

    /// <summary>
    /// "Switch to fail open": the review of <c>defenseclaw guardrail fail-mode open --yes</c>. Said in the review: <c>config set guardrail.hook_fail_mode</c>
    /// would save the value but not change what the hooks do - only <c>guardrail fail-mode</c> regenerates them.
    /// </summary>
    private void SwitchToFailOpen()
    {
        var argv = DurabilityAttention.FailOpenArgv;
        var running = _snapshot.IsRunning;
        Review.Open(
            "Switch to fail open?",
            running
                ? "Hooks will let the agent's actions through when they cannot reach the gateway, instead of blocking them."
                : "Hooks will let the agent's actions through while the gateway is down, instead of blocking them. The gateway is not running: the command writes the " +
                  "new hook files and does not start it (the CLI never starts a gateway that is stopped).",
            new[]
            {
                new DiscoverStep(argv, "Set the hook fail mode to open and regenerate the hooks.", CommandTier.StateChanging, CliRunner.LongRunningTimeout),
            },
            onFinished: _ => RefreshAfterActionAsync(),
            restartsGateway: running,
            extraWarnings: new[]
            {
                new CommandReviewWarning(
                    "Use guardrail fail-mode",
                    "defenseclaw config set guardrail.hook_fail_mode does not take effect: it saves the value, but only guardrail fail-mode regenerates the hooks that read it."),
                new CommandReviewWarning(
                    "The agent is not blocked, and not inspected",
                    "While the gateway is down an open hook allows every action without inspecting it. Start the gateway and switch back with guardrail fail-mode closed when you want it enforced."),
            },
            primaryText: "Switch to fail open");
    }
}
