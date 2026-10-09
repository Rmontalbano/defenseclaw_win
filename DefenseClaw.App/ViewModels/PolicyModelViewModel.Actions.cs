using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

public sealed partial class PolicyModelViewModel
{
    // ---- the inspector: the selected row's detail and what can be done to it -------------------------------------------------

    /// <summary>The selected row's title (<c>Posture · codex</c>); empty while nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInspectorDetail))]
    private string _inspectorTitle = string.Empty;

    /// <summary>The selected row's detail, line by line, from the runtime's model.</summary>
    public ObservableCollection<PolicyInspectorLine> InspectorLines { get; } = new();

    /// <summary>The choices the model offers for the selected row, grouped (<c>Tool-call block level</c>, <c>Rule pack</c> ...).</summary>
    public ObservableCollection<PolicyActionGroupViewModel> ActionGroups { get; } = new();

    public bool HasInspectorDetail => InspectorTitle.Length > 0;

    public bool HasActions => ActionGroups.Count > 0;

    /// <summary>The sentence over the choices when they are off because the data may not be acted on (old, partial, failed, config moved); empty otherwise.</summary>
    public string ActionsNote => HasActions && TrustBlockedReason is { } reason ? reason : string.Empty;

    public bool HasActionsNote => ActionsNote.Length > 0;

    private void RefreshInspector()
    {
        InspectorLines.Clear();
        ActionGroups.Clear();
        InspectorTitle = string.Empty;

        if (_model is { } model && SelectedRow is { } row)
        {
            var scope = ScopeRowOfView();
            var detail = model.Detail(View, row.Key, scope);
            InspectorTitle = detail.Title.Length > 0 ? detail.Title : row.Key;
            foreach (var line in detail.Lines)
            {
                InspectorLines.Add(new PolicyInspectorLine(line));
            }

            foreach (var group in model.Actions(View, row.Key, scope).GroupBy(a => a.Group, StringComparer.Ordinal))
            {
                ActionGroups.Add(new PolicyActionGroupViewModel(group.Key, group.Select(a => new PolicyActionViewModel(this, a)).ToArray()));
            }
        }

        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(ActionsNote));
        OnPropertyChanged(nameof(HasActionsNote));
    }

    // ---- notices and output ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeMessage = string.Empty;

    [ObservableProperty]
    private string _noticeTitle = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Warning;

    public bool HasNotice => NoticeMessage.Length > 0;

    [RelayCommand]
    private void CloseNotice() => NoticeMessage = string.Empty;

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        NoticeTitle = title;
        NoticeSeverity = severity;
        NoticeMessage = message;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private string _outputTitle = string.Empty;

    [ObservableProperty]
    private string _outputTone = "Neutral";

    /// <summary>A line under the output that explains it (OPA is not installed ...); empty when there is nothing to add.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputNote))]
    private string _outputNote = string.Empty;

    public bool HasOutput => OutputText.Length > 0;

    public bool HasOutputNote => OutputNote.Length > 0;

    [RelayCommand]
    private void CloseOutput()
    {
        OutputText = string.Empty;
        OutputNote = string.Empty;
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (HasOutput)
        {
            _ = Views.Controls.DcClipboard.TrySetText(OutputText);
        }
    }

    private void ShowOutput(string title, string text, bool succeeded, string note = "")
    {
        OutputTitle = title;
        OutputText = text.Length == 0 ? "(no output)" : text;
        OutputTone = succeeded ? "Ok" : "Bad";
        OutputNote = note;
    }

    // ---- what each button may do now --------------------------------------------------------------------------------------------

    /// <summary>
    /// Why a choice cannot be used, or null when it can: the data is not a complete recent read, something else is running, it is already in
    /// force, or it is not one of the changes this panel may make. A validation (a read) is only held back by a command that is running.
    /// </summary>
    internal string? ActionBlockedReason(PolicyAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (action.IsRead)
        {
            return IsRunning || IsBusy ? "Another command is running." : null;
        }

        if (ChangesBlockedReasonNow is { } reason)
        {
            return reason;
        }

        if (action.IsCurrent)
        {
            return "This is already in force.";
        }

        return PolicyActionGuard.IsAllowedChange(action.Argv) ? null : "The Policies panel does not run this command.";
    }

    /// <summary>
    /// The check made again at the moment a change is requested: the data must still be a complete, recent read and nothing else may be
    /// running. Refuses with the reason in the notice bar; true when the caller must stop.
    /// </summary>
    private bool RefuseChange()
    {
        CheckConfigStamp();
        if (ChangesBlockedReasonNow is not { } reason)
        {
            return false;
        }

        ShowNotice("Changes are off", reason);
        NotifyTrust();
        return true;
    }

    /// <summary>The same check for a flow that is already running its own checks (it holds <see cref="IsRunning"/>): only the data's trust matters.</summary>
    private bool RefuseUntrusted()
    {
        CheckConfigStamp();
        if (TrustBlockedReason is not { } reason)
        {
            return false;
        }

        ShowNotice("Changes are off", reason);
        NotifyTrust();
        return true;
    }

    /// <summary>Shows the refusal and returns true when the CLI would act on something other than the arguments named (it expands %VAR%, ~ and wildcards on Windows).</summary>
    private bool RefuseExpandingArguments(IReadOnlyList<string> argv)
    {
        var changes = ArgvHazards.FindChanges(argv, CliWorkingDirectory.DefaultPath);
        if (changes.Count == 0)
        {
            return false;
        }

        ShowNotice("Command refused", ArgumentExpansionException.BuildMessage(changes), InfoBarSeverity.Error);
        return true;
    }

    // ---- running a choice: validate where the runtime's model says to, then review --------------------------------------------

    [RelayCommand]
    private async Task RunActionAsync(PolicyActionViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var action = item.Action;
        if (action.IsRead)
        {
            await RunValidationAsync(action).ConfigureAwait(true);
            return;
        }

        if (RefuseChange())
        {
            return;
        }

        if (ActionBlockedReason(action) is { } why)
        {
            ShowNotice("Cannot change this", why);
            return;
        }

        if (RefuseExpandingArguments(action.Argv))
        {
            return;
        }

        IsRunning = true;
        try
        {
            var consequence = action.Consequence;
            switch (action.Kind)
            {
                case PolicyActionKind.UsePack:
                {
                    // A rule pack that does not validate is never switched to.
                    var validation = await ValidatePackAsync(action, showWhenValid: false).ConfigureAwait(true);
                    if (!validation.IsValid)
                    {
                        ShowNotice(
                            "Switching is withheld",
                            $"'{action.Title}' was not offered because the pack did not validate ({validation.Summary}). The output is below; fix the pack, then try again.");
                        return;
                    }

                    consequence = consequence.With($"Validation: {validation.Summary}.");
                    break;
                }

                case PolicyActionKind.Activate:
                {
                    // A policy bundle that does not validate is never activated.
                    if (!await ValidatePoliciesAsync().ConfigureAwait(true))
                    {
                        ShowNotice(
                            "Activation is withheld",
                            $"'{action.Title}' was not offered because policy validate did not pass. The output is below; fix what it names, then try again.");
                        return;
                    }

                    consequence = consequence.With("Validation: policy validate passed.");
                    break;
                }

                default:
                    break;
            }

            // The waits above are a chance for the data to have aged or the configuration to have moved: ask again.
            if (RefuseUntrusted())
            {
                return;
            }

            OpenReview(action, consequence);
        }
        catch (CliNotFoundException ex)
        {
            ShowNotice("The defenseclaw CLI was not found", $"The change cannot be prepared without it. {ex.Message}", InfoBarSeverity.Error);
        }
        catch (ArgumentExpansionException ex)
        {
            ShowNotice("Command refused", ex.Message, InfoBarSeverity.Error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceFault("prepare a change", ex);
            ShowNotice("Could not prepare the change", ex.Message);
        }
        finally
        {
            IsRunning = false;
            NotifyTrust();
        }
    }

    /// <summary>Opens the review of the exact command. Nothing runs until the operator confirms; a change that protects less needs the tick first.</summary>
    private void OpenReview(PolicyAction action, PolicyConsequence consequence)
    {
        // The last gate before the command can run: only these shapes, with values in the form the CLI takes.
        if (!PolicyActionGuard.IsAllowedChange(action.Argv))
        {
            ShowNotice("Cannot change this", "The Policies panel does not run this command.");
            return;
        }

        var warnings = new List<CommandReviewWarning>();
        if (action.Weakens && consequence.Warning.Length > 0)
        {
            warnings.Add(new CommandReviewWarning("Reduces protection", consequence.Warning));
        }

        var question = consequence.Heading.TrimEnd('?', ' ');
        Review.Open(
            heading: consequence.Heading,
            explanation: Explain(consequence),
            steps: new[] { new DiscoverStep(action.Argv, question) },
            onFinished: result => AfterChangeAsync(result, $"{question}: done."),
            restartsGateway: CommandReview.RestartsGatewayFor(action.Argv),
            primaryText: consequence.ConfirmLabel,
            names: action.Argv.Where(a => a.Length > 0 && !a.StartsWith('-')).ToArray(),
            extraWarnings: warnings,
            acknowledgement: action.Weakens ? WeakeningAcknowledgement : null);
    }

    /// <summary>The runtime's own account of the change: the one line, then what it does and what it leaves alone.</summary>
    internal static string Explain(PolicyConsequence consequence)
    {
        ArgumentNullException.ThrowIfNull(consequence);

        var text = new StringBuilder(consequence.Summary);
        if (consequence.Details.Count > 0)
        {
            _ = text.Append("\n\n").Append(string.Join('\n', consequence.Details.Select(d => "- " + d)));
        }

        return text.ToString();
    }

    // ---- validation (reads, run directly) ---------------------------------------------------------------------------------------

    /// <summary>The Validate button of a rule pack: runs the validator and shows what it said.</summary>
    private async Task RunValidationAsync(PolicyAction action)
    {
        if (IsRunning || IsBusy)
        {
            return;
        }

        IsRunning = true;
        try
        {
            _ = await ValidatePackAsync(action, showWhenValid: true).ConfigureAwait(true);
        }
        catch (CliNotFoundException ex)
        {
            ShowNotice("The defenseclaw CLI was not found", $"A rule pack cannot be validated without it. {ex.Message}", InfoBarSeverity.Error);
        }
        catch (ArgumentExpansionException ex)
        {
            ShowNotice("Command refused", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsRunning = false;
            NotifyTrust();
        }
    }

    /// <summary>
    /// Runs <c>guardrail validate-pack FOLDER --json</c> and shows what it printed (always for the Validate button; only when it did not pass for
    /// a switch, whose review then carries the result). A validator that could not run counts as not valid: a pack is not switched to unchecked.
    /// </summary>
    private async Task<PackValidation> ValidatePackAsync(PolicyAction action, bool showWhenValid)
    {
        string[] argv;
        try
        {
            argv = PolicyIntents.ValidatePack(action.PackFolder ?? string.Empty).ToArray();
        }
        catch (ArgumentException)
        {
            var notChecked = new PackValidation("invalid", "the pack's folder is not a full path the CLI can be handed");
            ShowOutput("Rule pack not validated", notChecked.Summary, false);
            return notChecked;
        }

        // The CLI expands %VARIABLES%, ~ and wildcards in every argument on Windows: a folder it would read as another is not validated, because
        // what was validated would not be what is switched to. The caller shows the refusal.
        var changes = ArgvHazards.FindChanges(argv, CliWorkingDirectory.DefaultPath);
        if (changes.Count > 0)
        {
            throw new ArgumentExpansionException(changes);
        }

        var invocation = await RunReadAsync(argv).ConfigureAwait(true);
        var result = invocation.FailureReason is { Length: > 0 } failure
            ? new PackValidation("unavailable", failure)
            : PolicyCatalogJson.ParseValidation(invocation.ExitCode ?? -1, Stdout(invocation));

        if (showWhenValid || !result.IsValid)
        {
            var raw = Stdout(invocation).Trim();
            ShowOutput(
                "defenseclaw " + string.Join(' ', argv.Take(2)) + " " + (action.PackFolder ?? string.Empty),
                raw.Length == 0 ? result.Summary : result.Summary + "\n\n" + raw,
                result.IsValid);
        }

        return result;
    }

    /// <summary>Runs <c>policy validate</c>; true when it exited 0. Its output is shown when it did not.</summary>
    private async Task<bool> ValidatePoliciesAsync()
    {
        var argv = _backend.ValidateArgv;
        var invocation = await RunReadAsync(argv).ConfigureAwait(true);
        var passed = invocation.FailureReason is not { Length: > 0 } && invocation.ExitCode == 0;
        if (!passed)
        {
            var transcript = Transcript(invocation);
            if (invocation.FailureReason is { Length: > 0 } failure)
            {
                transcript = transcript.Length == 0 ? failure : transcript + "\n" + failure;
            }

            var note = transcript.Contains("'opa' binary not found", StringComparison.Ordinal)
                ? "OPA (the opa executable) is not installed or not on PATH, so DefenseClaw cannot compile or test the Rego bundle. Install OPA, then try again."
                : string.Empty;
            ShowOutput("defenseclaw " + string.Join(' ', argv), transcript, false, note);
        }

        return passed;
    }

    // ---- after a change ---------------------------------------------------------------------------------------------------------

    private async Task AfterChangeAsync(DiscoverReviewResult result, string doneText)
    {
        // The settings may have changed whether or not the command succeeded: read them again either way.
        _configMoved = true;
        NotifyTrust();
        await LoadAsync().ConfigureAwait(true);

        if (result.Succeeded)
        {
            ShowNotice("Done", doneText, InfoBarSeverity.Success);
        }
        else
        {
            ShowNotice("The command did not finish", "The review dialog and the Activity panel have its output. The settings above are read again.", InfoBarSeverity.Warning);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    private static string Stdout(CliInvocation invocation) =>
        string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));

    /// <summary>The whole transcript, both streams in the order they arrived: what <c>policy validate</c> prints.</summary>
    private static string Transcript(CliInvocation invocation) =>
        string.Join('\n', invocation.OutputLines.Select(l => l.Text)).TrimEnd();
}
