using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Setup;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels.SetupResources;

/// <summary>
/// What a change is, before it is offered: the exact command, the sentence that says what it does, and the bars that say what to know first.
/// Built by the editor for one verb on one row; the base class opens the shared review with it.
/// </summary>
/// <param name="Verb">Enable, Disable, Test or Remove.</param>
/// <param name="Row">The row it acts on.</param>
/// <param name="Argv">The exact arguments handed to the CLI.</param>
/// <param name="Heading">The review's question.</param>
/// <param name="Explanation">One or two sentences on what running it does to the installation.</param>
/// <param name="PrimaryText">The confirm button's text.</param>
/// <param name="Tier">The lowest tier the review shows (the classifier can only raise it).</param>
/// <param name="Timeout">How long the command may take.</param>
/// <param name="RestartsGateway">Whether it restarts the live gateway when it succeeds.</param>
/// <param name="Warnings">The bars to show with the command.</param>
/// <param name="DoneText">What the notice says when it worked.</param>
internal sealed record SetupChangePlan(
    SetupVerb Verb,
    SetupResourceRow Row,
    IReadOnlyList<string> Argv,
    string Heading,
    string Explanation,
    string PrimaryText,
    CommandTier Tier,
    TimeSpan Timeout,
    bool RestartsGateway,
    IReadOnlyList<CommandReviewWarning> Warnings,
    string DoneText)
{
    /// <summary>True when the command can have changed what the list shows (everything but a test).</summary>
    public bool ChangesList => Verb != SetupVerb.Test;

    /// <summary>The step's purpose, for the review's step line.</summary>
    public string Purpose => $"{Verb} {Row.Title}";
}

public abstract partial class SetupResourceViewModel
{
    /// <summary>Builds the plan for <paramref name="verb"/> (Enable, Disable, Test or Remove) on <paramref name="row"/>. Only asked when the editor offers the verb and the row allows it.</summary>
    internal abstract SetupChangePlan Plan(SetupVerb verb, SetupResourceRow row);

    /// <summary>
    /// The check made again at the moment a change is requested: the list must still be a complete, recent read of a configuration that has not
    /// moved, nothing else may be running, and the row must allow the verb. Refuses with the reason in the notice bar; true when the caller must stop.
    /// </summary>
    protected bool RefuseChange(SetupVerb verb)
    {
        // The bound values only look at what the watcher has reported; a change requested now looks at the files (CUST-312).
        _ = Trust.CheckConfig();
        if (BlockedReason(verb) is not { } reason)
        {
            return false;
        }

        ShowNotice(verb == SetupVerb.Add ? "Changes are off" : $"Cannot {verb.ToString().ToLowerInvariant()}", reason);
        NotifyActions();
        return true;
    }

    /// <summary>
    /// Why a confirmed review must not run, or null: the review's last question, asked when the operator confirms (a review stays open for as long
    /// as it is read, and the list it was opened on can go out of date in that time). The installation comes first, and it is the one a refresh
    /// would not cure. When it answers, nothing runs: the dialog says why and Activity records the command as refused.
    /// </summary>
    private string? ReasonToRefuseRun()
    {
        _ = Trust.CheckConfig();

        var reason = Services.Installation.BlockedReason
            ?? (State is SetupListState.Loaded or SetupListState.Empty
                ? Trust.Reason
                : Trust.Reason ?? $"Changes are off until the {Plural} have been read.");
        if (reason is null)
        {
            return null;
        }

        ShowNotice("Changes are off", reason);
        NotifyActions();
        return reason;
    }

    // ------------------------------------------------------------------ Add

    /// <summary>Add: opens the setup wizard on <c>add</c>. The wizard ends on its own review, so nothing is run from here.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (RefuseChange(SetupVerb.Add))
        {
            return;
        }

        await OpenAddWizardAsync(null).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the wizard on <c>add</c>, with <paramref name="firstArgument"/> as the first thing it asks for when there is one, and reads the list
    /// again when it closes: the wizard may have changed it whether or not it finished.
    /// </summary>
    protected async Task OpenAddWizardAsync(string? firstArgument)
    {
        if (IsDisposed)
        {
            return;
        }

        if (OpenWizard is not { } open)
        {
            ShowNotice("The wizard cannot open here", $"Add needs the setup wizard, and this window has none to open. Use the {SetupResourceArgv.Noun(Resource)} card on the Setup page.");
            return;
        }

        try
        {
            await open(new WizardPreset("add", firstArgument)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceFault("wizard", ex);
            ShowNotice("The wizard could not be opened", EndpointDisplay.ScrubText(ex.Message));
            return;
        }

        await ReadAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ Enable, Disable, Test, Remove

    [RelayCommand]
    private void Enable() => BeginChange(SetupVerb.Enable);

    [RelayCommand]
    private void Disable() => BeginChange(SetupVerb.Disable);

    [RelayCommand]
    private void Test() => BeginChange(SetupVerb.Test);

    [RelayCommand]
    private void Remove() => BeginChange(SetupVerb.Remove);

    /// <summary>
    /// Opens the review of <paramref name="verb"/> on the selected row. Nothing runs from here: the shared review shows the exact command, its
    /// tier and whether the gateway restarts, and the command starts only when the operator confirms it.
    /// </summary>
    private void BeginChange(SetupVerb verb)
    {
        if (!Offers(verb) || RefuseChange(verb))
        {
            return;
        }

        // BlockedReason names the missing row, so there is one from here on.
        var row = SelectedRow!;
        var plan = Plan(verb, row);

        // The CLI rewrites %VARIABLES%, $VARIABLES, ~ and wildcards in every argument on Windows, so a target that has one would be reviewed as one
        // thing and acted on as another. A name is checked when the row is read; a directory is checked here.
        if (ArgvHazards.FindChangedTargets(plan.Argv, CliWorkingDirectory.DefaultPath) is { Count: > 0 } hazards)
        {
            ShowNotice($"Cannot {verb.ToString().ToLowerInvariant()} this {Singular}", ArgumentExpansionException.BuildMessage(hazards));
            return;
        }

        Review.Open(
            heading: plan.Heading,
            explanation: plan.Explanation,
            steps: [new DiscoverStep(plan.Argv, plan.Purpose, plan.Tier, plan.Timeout, OutputFilter: EndpointHost.ScrubLine)],
            onFinished: result => AfterChangeAsync(plan, result),
            restartsGateway: plan.RestartsGateway,
            primaryText: plan.PrimaryText,
            names: [row.Key],
            extraWarnings: plan.Warnings);
    }

    private async Task AfterChangeAsync(SetupChangePlan plan, DiscoverReviewResult result)
    {
        // The configuration may have changed whether or not the command succeeded: read the list again, unless the command only tested.
        if (plan.ChangesList)
        {
            await ReadAsync().ConfigureAwait(true);
        }

        if (result.Succeeded)
        {
            ShowNotice(plan.Verb == SetupVerb.Test ? "Test passed" : "Done", plan.DoneText, InfoBarSeverity.Success);
            return;
        }

        var why = result.Invocations.LastOrDefault() is { } last
            ? EndpointDisplay.ScrubText(string.Join(' ', last.OutputLines.Select(static l => l.Text.Trim()).Where(static t => t.Length > 0).TakeLast(2)), 300)
            : string.Empty;
        ShowNotice(
            plan.Verb == SetupVerb.Test ? "Test failed" : "The command did not finish",
            (why.Length > 0 ? why + " " : string.Empty) + "The review dialog and the Activity panel have the whole output." + (plan.ChangesList ? " The list above is read again." : string.Empty),
            InfoBarSeverity.Warning);
    }
}
