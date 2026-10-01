using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One command inside a reviewed action. <see cref="MinimumTier"/> is a floor, not a replacement for
/// <see cref="CommandTiers.Classify"/>: the review uses whichever of the two is stricter, so a source id
/// that happens to spell a read-only verb (<c>registry sync list</c>) cannot downgrade the review.
/// <see cref="Executable"/> is what <see cref="Argv"/> is handed to: <c>defenseclaw</c> unless the step is a gateway action
/// (<c>defenseclaw-gateway restart</c>, from the Overview's Quick Actions).
/// <see cref="Verify"/> is for a command whose exit code is not its result (<c>init --json-summary</c> exits 0 with a failed report): it
/// reads what the run printed and returns null when it is satisfied, or the reason the step must count as failed, which stops the steps
/// after it. <see cref="RetainFullOutput"/> keeps the whole transcript for a step whose output <see cref="Verify"/> parses.
/// </summary>
public sealed record DiscoverStep(
    IReadOnlyList<string> Argv,
    string Purpose,
    CommandTier MinimumTier = CommandTier.StateChanging,
    TimeSpan? Timeout = null,
    string Executable = CommandReview.DefaultExecutable,
    Func<CliInvocation, string?>? Verify = null,
    bool RetainFullOutput = false);

/// <summary>What a reviewed action did, handed to the panel so it can re-read its state.</summary>
public sealed record DiscoverReviewResult(bool Succeeded, IReadOnlyList<CliInvocation> Invocations);

/// <summary>
/// Helpers shared by the Discover panels for talking to the CLI.
/// </summary>
internal static class DiscoverCli
{
    /// <summary>
    /// Runs <paramref name="argv"/> without a confirmation, but only if
    /// <see cref="CommandTiers.Classify"/> calls it read-only. Anything else must go through a
    /// <see cref="DiscoverActionReview"/>.
    /// </summary>
    public static Task<CliInvocation> RunReadOnlyAsync(
        AppServices services,
        IReadOnlyList<string> argv,
        CancellationToken cancellationToken)
    {
        if (CommandTiers.Classify(argv) != CommandTier.ReadOnly)
        {
            throw new InvalidOperationException(
                $"'{CommandLine(argv)}' is not a read-only command, so it has to be confirmed first.");
        }

        // Read-only calls here are all --json reads the panels parse, so keep their full output.
        return services.Cli.RunAsync(argv, cancellationToken: cancellationToken, options: CliRunOptions.JsonRead);
    }

    public static string Stdout(CliInvocation invocation) =>
        string.Join(
            Environment.NewLine,
            invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));

    public static string Stderr(CliInvocation invocation) =>
        string.Join(
            Environment.NewLine,
            invocation.OutputLines.Where(l => l.Stream == CliStream.StandardError).Select(l => l.Text));

    public static string CommandLine(IEnumerable<string> argv) =>
        CommandReview.CommandLine(CommandReview.DefaultExecutable, argv);

    /// <summary>Display quoting only (the runner passes an argument list, no shell is involved).</summary>
    public static string Quote(string argument) => CommandReview.Quote(argument);

    /// <summary>The first JSON value in <paramref name="text"/>, skipping any banner line the CLI printed before it.</summary>
    public static string TrimToJson(string text)
    {
        var start = text.IndexOfAny(new[] { '{', '[' });
        return start > 0 ? text[start..] : text;
    }
}

/// <summary>
/// The confirm-and-run overlay every mutating action on the Discover panels goes through. It builds a
/// <see cref="CommandReview"/> for the shared <c>CommandReviewControl</c> — the exact argv of each step
/// (selectable, copyable), the strictest tier of them from <see cref="CommandTiers"/> (a Destructive step gets a
/// red badge and a danger-styled confirm button), whether it restarts the gateway — and then
/// runs the steps in order: a step only runs if every step before it exited 0 (catalog section 4 item
/// 8f). Every run goes through <see cref="CliRunner"/>, so it lands in the Activity panel with its full
/// output; the result shown here is a short tail of that.
/// </summary>
public sealed partial class DiscoverActionReview : ObservableObject
{
    private const int MaxOutputLinesPerStep = 30;
    private const int MaxLineChars = 300;

    private readonly AppServices _services;
    private readonly List<DiscoverStep> _plan = new();
    private Func<DiscoverReviewResult, Task>? _onFinished;
    private Action? _onCancelled;

    public DiscoverActionReview(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    [NotifyPropertyChangedFor(nameof(Phase))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    [NotifyPropertyChangedFor(nameof(Phase))]
    private bool _isFinished;

    /// <summary>What the dialog shows: the title, tier, steps and warnings. Set before <see cref="IsOpen"/> flips.</summary>
    [ObservableProperty]
    private CommandReview? _commandReview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty]
    private string _resultKey = "Neutral";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultOutput))]
    private string? _resultOutput;

    /// <summary>The dialog is up and waiting for a decision.</summary>
    public bool IsConfirming => IsOpen && !IsRunning && !IsFinished;

    /// <summary>Which buttons the shared control offers: Cancel and confirm, none while running, then Close.</summary>
    public CommandReviewPhase Phase => IsRunning
        ? CommandReviewPhase.Running
        : IsFinished ? CommandReviewPhase.Finished : CommandReviewPhase.Review;

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    public bool HasResultOutput => !string.IsNullOrWhiteSpace(ResultOutput);

    /// <summary>The stricter of the classifier's verdict and the step's own floor; never ReadOnly (a review exists because something changes).</summary>
    public static CommandTier EffectiveTier(DiscoverStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return CommandReview.ResolveTier(step.Argv, CommandReview.Stricter(step.MinimumTier, CommandTier.StateChanging));
    }

    /// <summary>
    /// Opens the review. Ignored while a previous review is still running its commands.
    /// </summary>
    /// <param name="onFinished">Called after the last step ran (or a step failed and the rest were skipped), with what happened.</param>
    /// <param name="onCancelled">Called when the operator dismisses the dialog without running anything.</param>
    /// <param name="primaryText">The confirm button's text; null keeps "Run command" / "Run destructive command".</param>
    public void Open(
        string heading,
        string explanation,
        IReadOnlyList<DiscoverStep> steps,
        Func<DiscoverReviewResult, Task>? onFinished = null,
        bool restartsGateway = false,
        string? warning = null,
        string? primaryText = null,
        Action? onCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || IsRunning)
        {
            return;
        }

        _plan.Clear();
        _plan.AddRange(steps);

        var warnings = new List<CommandReviewWarning>();
        if (restartsGateway)
        {
            warnings.Add(CommandReviewWarning.GatewayRestart());
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            warnings.Add(new CommandReviewWarning("Before you continue", warning));
        }

        _onFinished = onFinished;
        _onCancelled = onCancelled;

        ResultText = null;
        ResultOutput = null;
        ResultKey = "Neutral";
        IsFinished = false;
        IsRunning = false;
        CommandReview = new CommandReview
        {
            Title = heading,
            Summary = explanation,
            Steps = steps
                .Select((s, i) => new CommandReviewStep(
                    s.Argv,
                    s.Purpose,
                    CommandReview.Stricter(s.MinimumTier, CommandTier.StateChanging),
                    s.Executable,
                    number: steps.Count > 1 ? i + 1 : 0))
                .ToArray(),
            Warnings = warnings,
            RestartsGateway = restartsGateway,
            ConfirmLabel = primaryText ?? string.Empty,
        };
        IsOpen = true;
    }

    /// <summary>Esc / Cancel / Close. Does nothing while commands are running (there is no way to stop them from here).</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (IsRunning)
        {
            return;
        }

        var wasFinished = IsFinished;
        IsOpen = false;
        if (!wasFinished)
        {
            var callback = _onCancelled;
            _onCancelled = null;
            callback?.Invoke();
        }
    }

    /// <summary>True when Esc was consumed (the dialog closed, or is busy and must stay).</summary>
    public bool HandleEscape()
    {
        if (!IsOpen)
        {
            return false;
        }

        Dismiss();
        return true;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (!IsConfirming || CommandReview is not { } review)
        {
            return;
        }

        IsRunning = true;
        ResultText = null;
        ResultOutput = null;

        var invocations = new List<CliInvocation>();
        var output = new StringBuilder();
        var succeeded = true;

        try
        {
            for (var i = 0; i < review.Steps.Count; i++)
            {
                var row = review.Steps[i];
                var step = _plan[i];
                if (!succeeded)
                {
                    row.SetStatus("Skipped: an earlier step did not succeed", "Neutral");
                    continue;
                }

                row.SetStatus("Running…", "Warn");
                try
                {
                    var options = step.Timeout is { } timeout
                        ? CliRunOptions.WithTimeout(timeout) with { RetainFullOutput = step.RetainFullOutput }
                        : step.RetainFullOutput ? CliRunOptions.JsonRead : null;
                    var invocation = await _services.Cli
                        .RunNamedAsync(step.Executable, step.Argv, cancellationToken: CancellationToken.None, options: options)
                        .ConfigureAwait(true);
                    invocations.Add(invocation);

                    if (invocation.FailureReason is { Length: > 0 } reason)
                    {
                        row.SetStatus($"Did not complete: {reason}", "Bad");
                        succeeded = false;
                    }
                    else if (invocation.ExitCode != 0)
                    {
                        row.SetStatus($"Failed (exit {invocation.ExitCode})", "Bad");
                        succeeded = false;
                    }
                    else if (step.Verify?.Invoke(invocation) is { Length: > 0 } problem)
                    {
                        // Exit 0 is not success for this step: its own report says it did not finish the job.
                        row.SetStatus("Reported a problem (exit 0)", "Bad");
                        output.AppendLine(problem);
                        succeeded = false;
                    }
                    else
                    {
                        row.SetStatus("Succeeded (exit 0)", "Ok");
                    }

                    AppendOutput(output, i + 1, invocation, label: review.Steps.Count > 1);
                }
                catch (CliNotFoundException ex)
                {
                    row.SetStatus("Could not start", "Bad");
                    output.AppendLine($"'{ex.ExecutableName}' was not found: {ex.Message}");
                    succeeded = false;
                }
                catch (SecretInArgumentException)
                {
                    row.SetStatus("Refused: a secret was in the arguments", "Bad");
                    succeeded = false;
                }
            }
        }
        finally
        {
            IsRunning = false;
            IsFinished = true;
        }

        ResultKey = succeeded ? "Ok" : "Bad";
        ResultText = succeeded
            ? "Done. The exact command and its full output are in the Activity panel."
            : "Did not finish. Steps after the failing one were not run. The exact command and its full output are in the Activity panel.";
        ResultOutput = output.Length == 0 ? null : output.ToString().TrimEnd();

        var callback = _onFinished;
        _onFinished = null;
        if (callback is not null)
        {
            try
            {
                await callback(new DiscoverReviewResult(succeeded, invocations)).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // A panel's re-read failing must not turn a finished run into an unhandled exception.
            catch (Exception ex)
            {
                Trace.TraceError($"Discover action follow-up failed: {ex}");
            }
#pragma warning restore CA1031
        }
    }

    private static void AppendOutput(StringBuilder builder, int stepNumber, CliInvocation invocation, bool label)
    {
        var lines = invocation.OutputLines
            .Select(l => l.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        if (lines.Count == 0)
        {
            return;
        }

        if (label)
        {
            builder.AppendLine($"Step {stepNumber}:");
        }

        var skipped = Math.Max(0, lines.Count - MaxOutputLinesPerStep);
        if (skipped > 0)
        {
            builder.AppendLine($"… {skipped} earlier line(s) omitted; see Activity.");
        }

        foreach (var line in lines.Skip(skipped))
        {
            builder.AppendLine(line.Length > MaxLineChars ? line[..MaxLineChars] + "…" : line);
        }
    }
}
