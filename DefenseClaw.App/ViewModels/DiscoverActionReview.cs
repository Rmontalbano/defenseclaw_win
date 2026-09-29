using System.Collections.ObjectModel;
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
/// </summary>
public sealed record DiscoverStep(
    IReadOnlyList<string> Argv,
    string Purpose,
    CommandTier MinimumTier = CommandTier.StateChanging,
    TimeSpan? Timeout = null);

/// <summary>What a reviewed action did, handed to the panel so it can re-read its state.</summary>
public sealed record DiscoverReviewResult(bool Succeeded, IReadOnlyList<CliInvocation> Invocations);

/// <summary>Display and run-state of one step in the review dialog.</summary>
public sealed partial class DiscoverStepRow : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Not run yet";

    [ObservableProperty]
    private string _statusKey = "Neutral";

    public DiscoverStepRow(int number, DiscoverStep step, CommandTier tier)
    {
        Number = number;
        Step = step;
        Tier = tier;
        CommandText = DiscoverCli.CommandLine(step.Argv);
    }

    public int Number { get; }

    public DiscoverStep Step { get; }

    public CommandTier Tier { get; }

    /// <summary>The exact command, as it will run.</summary>
    public string CommandText { get; }

    public string Purpose => Step.Purpose;

    public string Heading => $"Step {Number}";

    public void SetStatus(string text, string key)
    {
        StatusText = text;
        StatusKey = key;
    }

    public override string ToString() => $"Step {Number}: {CommandText}. {Purpose} {StatusText}.";
}

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
        "defenseclaw " + string.Join(' ', argv.Select(Quote));

    /// <summary>Display quoting only (the runner passes an argument list, no shell is involved).</summary>
    public static string Quote(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;
    }

    /// <summary>The first JSON value in <paramref name="text"/>, skipping any banner line the CLI printed before it.</summary>
    public static string TrimToJson(string text)
    {
        var start = text.IndexOfAny(new[] { '{', '[' });
        return start > 0 ? text[start..] : text;
    }
}

/// <summary>
/// The confirm-and-run overlay every mutating action on the Discover panels goes through: it shows the
/// exact argv (selectable, copyable), the command's tier from <see cref="CommandTiers"/> (a Destructive
/// step gets a red badge and a danger-styled primary button), whether it restarts the gateway, and then
/// runs the steps in order — a step only runs if every step before it exited 0 (catalog section 4 item
/// 8f). Every run goes through <see cref="CliRunner"/>, so it lands in the Activity panel with its full
/// output; the result shown here is a short tail of that.
/// </summary>
public sealed partial class DiscoverActionReview : ObservableObject
{
    public const string RestartWarning =
        "This restarts the DefenseClaw gateway. Connector hooks and telemetry may report it as unavailable for a few seconds.";

    private const int MaxOutputLinesPerStep = 30;
    private const int MaxLineChars = 300;

    private readonly AppServices _services;
    private Func<DiscoverReviewResult, Task>? _onFinished;
    private Action? _onCancelled;

    public DiscoverActionReview(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public ObservableCollection<DiscoverStepRow> Steps { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    [NotifyPropertyChangedFor(nameof(ShowRunButton))]
    [NotifyPropertyChangedFor(nameof(ShowDangerButton))]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    [NotifyPropertyChangedFor(nameof(ShowRunButton))]
    [NotifyPropertyChangedFor(nameof(ShowDangerButton))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming))]
    [NotifyPropertyChangedFor(nameof(ShowRunButton))]
    [NotifyPropertyChangedFor(nameof(ShowDangerButton))]
    private bool _isFinished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRunButton))]
    [NotifyPropertyChangedFor(nameof(ShowDangerButton))]
    [NotifyPropertyChangedFor(nameof(TierKey))]
    private bool _isDestructive;

    [ObservableProperty]
    private string _heading = string.Empty;

    [ObservableProperty]
    private string _explanation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string? _warningText;

    [ObservableProperty]
    private string _primaryText = "Run";

    [ObservableProperty]
    private string _tierLabel = "Changes state";

    [ObservableProperty]
    private bool _restartsGateway;

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

    public bool ShowRunButton => IsConfirming && !IsDestructive;

    public bool ShowDangerButton => IsConfirming && IsDestructive;

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    public bool HasResultOutput => !string.IsNullOrWhiteSpace(ResultOutput);

    public bool MultipleSteps => Steps.Count > 1;

    /// <summary>Tone key for the tier badge: Destructive is red, anything else that changes state is amber.</summary>
    public string TierKey => IsDestructive ? "Bad" : "Warn";

    public string RestartNotice => RestartWarning;

    /// <summary>Every command in the review, one per line, for the Copy button.</summary>
    public string CommandText => string.Join(Environment.NewLine, Steps.Select(s => s.CommandText));

    /// <summary>The stricter of the classifier's verdict and the step's own floor; never ReadOnly (a review exists because something changes).</summary>
    public static CommandTier EffectiveTier(DiscoverStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var classified = CommandTiers.Classify(step.Argv);
        var tier = classified > step.MinimumTier ? classified : step.MinimumTier;
        return tier == CommandTier.ReadOnly ? CommandTier.StateChanging : tier;
    }

    /// <summary>
    /// Opens the review. Ignored while a previous review is still running its commands.
    /// </summary>
    /// <param name="onFinished">Called after the last step ran (or a step failed and the rest were skipped), with what happened.</param>
    /// <param name="onCancelled">Called when the operator dismisses the dialog without running anything.</param>
    public void Open(
        string heading,
        string explanation,
        IReadOnlyList<DiscoverStep> steps,
        Func<DiscoverReviewResult, Task>? onFinished = null,
        bool restartsGateway = false,
        string? warning = null,
        string primaryText = "Run",
        Action? onCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || IsRunning)
        {
            return;
        }

        Steps.Clear();
        var strictest = CommandTier.StateChanging;
        for (var i = 0; i < steps.Count; i++)
        {
            var tier = EffectiveTier(steps[i]);
            if (tier > strictest)
            {
                strictest = tier;
            }

            Steps.Add(new DiscoverStepRow(i + 1, steps[i], tier));
        }

        _onFinished = onFinished;
        _onCancelled = onCancelled;

        Heading = heading;
        Explanation = explanation;
        WarningText = warning;
        PrimaryText = primaryText;
        RestartsGateway = restartsGateway;
        IsDestructive = strictest == CommandTier.Destructive;
        TierLabel = IsDestructive ? "Destructive" : "Changes state";
        ResultText = null;
        ResultOutput = null;
        ResultKey = "Neutral";
        IsFinished = false;
        IsRunning = false;
        OnPropertyChanged(nameof(MultipleSteps));
        OnPropertyChanged(nameof(CommandText));
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
    private void Copy()
    {
        try
        {
            System.Windows.Clipboard.SetText(CommandText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is locked by another process; the command text is selectable in the dialog too.
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (!IsConfirming)
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
            foreach (var row in Steps)
            {
                if (!succeeded)
                {
                    row.SetStatus("Skipped: an earlier step did not succeed", "Neutral");
                    continue;
                }

                row.SetStatus("Running…", "Warn");
                try
                {
                    var options = row.Step.Timeout is { } timeout ? CliRunOptions.WithTimeout(timeout) : null;
                    var invocation = await _services.Cli
                        .RunAsync(row.Step.Argv, cancellationToken: CancellationToken.None, options: options)
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
                    else
                    {
                        row.SetStatus("Succeeded (exit 0)", "Ok");
                    }

                    AppendOutput(output, row, invocation, label: Steps.Count > 1);
                }
                catch (CliNotFoundException ex)
                {
                    row.SetStatus("Could not start", "Bad");
                    output.AppendLine($"'defenseclaw' was not found: {ex.Message}");
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

    private static void AppendOutput(StringBuilder builder, DiscoverStepRow row, CliInvocation invocation, bool label)
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
            builder.AppendLine($"Step {row.Number}:");
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
