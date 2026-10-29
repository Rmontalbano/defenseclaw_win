using System.Globalization;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>How one step of a reviewed run ended.</summary>
public enum DiscoverStepState
{
    /// <summary>It ran and exited 0 (and, where it has a check of its own, the check accepted what it printed).</summary>
    Succeeded,

    /// <summary>It started and did not succeed, or could not start. The steps after it did not run.</summary>
    Failed,

    /// <summary>It did not run: an earlier step failed, or the whole review was refused before the first started.</summary>
    NotRun,
}

/// <summary>What became of one step of a reviewed run, for the report that says which steps ran and which did not.</summary>
/// <param name="Number">The step's position, 1-based.</param>
/// <param name="State">How it ended.</param>
/// <param name="Command">The step as the review shows it (<c>defenseclaw setup notifications-set sources.hook off --no-restart</c>).</param>
/// <param name="Detail">Why, in a few words: <c>exit 1</c>, <c>timed out after 120 s</c>, <c>an earlier step did not succeed</c>. Empty for a step that succeeded.</param>
public sealed record DiscoverStepOutcome(int Number, DiscoverStepState State, string Command, string Detail);

/// <summary>
/// A plan of N invocations that is reviewed together and run in order (the TUI's <c>SetupCommandIntent.follow_up</c>, the Mac's multi-command
/// wizard): the notification routing's one <c>setup notifications-set</c> per changed slot is the first. It is data - the steps, the sentence
/// that says what running them does, the bars that say what to know first - and the shared <see cref="DiscoverActionReview"/> is what runs it:
/// every step in the order given, the next only if the one before exited 0 (a failure stops the plan; nothing after it starts, and nothing
/// before it is undone), every step through <c>CliRunner</c> so each is its own entry in Activity, and a report of which steps ran
/// (<see cref="PlanReport"/>).
/// <para>
/// The first-run window builds the same shape (<c>InitPlan</c>); this one is for the Setup hub and the panels' dialogs, which build theirs from a form.
/// </para>
/// </summary>
public sealed record SetupPlan
{
    /// <summary>The review's question ("Update notification routing?").</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences on what running the plan does to the installation.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The commands, in run order. Empty is "nothing to apply": such a plan is not opened.</summary>
    public required IReadOnlyList<DiscoverStep> Steps { get; init; }

    /// <summary>True when running the plan restarts the live gateway (at least once); the review says so.</summary>
    public bool RestartsGateway { get; init; }

    /// <summary>The bars to show after the ones the review builds itself.</summary>
    public IReadOnlyList<CommandReviewWarning> Warnings { get; init; } = Array.Empty<CommandReviewWarning>();

    /// <summary>The confirm button's text; null keeps "Run command".</summary>
    public string? PrimaryText { get; init; }

    /// <summary>True when there is nothing to run.</summary>
    public bool IsEmpty => Steps.Count == 0;
}

/// <summary>Opening a <see cref="SetupPlan"/> in the shared review.</summary>
public static class SetupPlanReview
{
    /// <summary>
    /// Opens <paramref name="plan"/> in <paramref name="review"/>. Does nothing for an empty plan, or while the review is running a plan
    /// already (<see cref="DiscoverActionReview.Open"/> ignores that call).
    /// </summary>
    /// <param name="onFinished">Called after the last step ran, or after the one that failed (the rest were not run), with what happened.</param>
    /// <param name="onCancelled">Called when the operator closes the review without running anything.</param>
    public static void OpenPlan(
        this DiscoverActionReview review,
        SetupPlan plan,
        Func<DiscoverReviewResult, Task>? onFinished = null,
        Action? onCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.IsEmpty)
        {
            return;
        }

        review.Open(
            plan.Title,
            plan.Summary,
            plan.Steps,
            onFinished,
            restartsGateway: plan.RestartsGateway,
            primaryText: plan.PrimaryText,
            onCancelled: onCancelled,
            extraWarnings: plan.Warnings);
    }
}

/// <summary>
/// The sentences that say which steps of a plan ran and which did not. A step that failed is named with why; the steps after it are named as
/// not run; and, when something before the failure succeeded, that it stays done (a plan is a sequence, not a transaction: nothing is rolled back).
/// </summary>
public static class PlanReport
{
    /// <summary>
    /// The report for <paramref name="steps"/>, or empty for a plan of one step (its own status line is the whole story).
    /// <list type="bullet">
    ///   <item>All ran: <c>All 3 steps ran.</c></item>
    ///   <item>One failed: <c>1 of 3 steps succeeded. Step 2 failed (exit 1). Step 3 was not run. The steps that succeeded are not undone.</c></item>
    ///   <item>None started (the review was refused): <c>No step ran.</c></item>
    /// </list>
    /// </summary>
    public static string Describe(IReadOnlyList<DiscoverStepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count <= 1)
        {
            return string.Empty;
        }

        var succeeded = steps.Count(static s => s.State == DiscoverStepState.Succeeded);
        var failed = steps.FirstOrDefault(static s => s.State == DiscoverStepState.Failed);
        var notRun = steps.Where(static s => s.State == DiscoverStepState.NotRun).ToArray();

        if (failed is null && notRun.Length == 0)
        {
            return string.Create(CultureInfo.CurrentCulture, $"All {steps.Count} steps ran.");
        }

        if (succeeded == 0 && failed is null)
        {
            return "No step ran.";
        }

        var text = new List<string> { string.Create(CultureInfo.CurrentCulture, $"{succeeded} of {steps.Count} steps succeeded.") };
        if (failed is not null)
        {
            text.Add(
                "Step " + failed.Number.ToString(CultureInfo.CurrentCulture) + " failed" +
                (failed.Detail.Length > 0 ? " (" + failed.Detail + ")." : "."));
        }

        if (notRun.Length > 0)
        {
            text.Add(Numbers(notRun) + (notRun.Length == 1 ? " was" : " were") + " not run.");
        }

        if (succeeded > 0)
        {
            text.Add("The steps that succeeded are not undone.");
        }

        return string.Join(' ', text);
    }

    /// <summary>"Step 3", "Steps 3 and 4", "Steps 3, 4 and 5".</summary>
    private static string Numbers(IReadOnlyList<DiscoverStepOutcome> steps)
    {
        var numbers = steps.Select(static s => s.Number.ToString(CultureInfo.CurrentCulture)).ToArray();
        return numbers.Length == 1
            ? "Step " + numbers[0]
            : "Steps " + string.Join(", ", numbers[..^1]) + " and " + numbers[^1];
    }
}
