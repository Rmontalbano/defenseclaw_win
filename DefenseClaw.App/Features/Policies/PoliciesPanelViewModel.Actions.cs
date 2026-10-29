using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels;

public sealed partial class PoliciesPanelViewModel
{
    /// <summary>The sentence on the acknowledgement checkbox of a review that reduces protection.</summary>
    public const string WeakeningAcknowledgement = "I understand this reduces protection and want to continue.";

    /// <summary>Most change lines a review lists before it says how many more there are.</summary>
    private const int MaxChangeLines = 12;

    // ---- State of the actions ------------------------------------------------------------------------------------------

    /// <summary>A validate / test / activate check is running (a change may not start meanwhile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange), nameof(ChangesBlockedReason), nameof(CanRunReads))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationText), nameof(ValidationTone))]
    private PolicyValidation _validation;

    private DateTimeOffset? _validatedAt;

    /// <summary>validate / test can start: not while another command runs.</summary>
    public bool CanRunReads => !IsRunning && !IsBusy;

    /// <summary>What the last validate said, for the inspector and the caption under the table.</summary>
    public string ValidationText => Validation switch
    {
        PolicyValidation.Running => "Validating...",
        PolicyValidation.Passed => $"Validation passed at {_validatedAt?.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}",
        PolicyValidation.Failed => $"Validation failed at {_validatedAt?.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}",
        _ => "Not validated yet. Activate validates first.",
    };

    public string ValidationTone => Validation switch
    {
        PolicyValidation.Passed => "Ok",
        PolicyValidation.Failed => "Bad",
        _ => "Neutral",
    };

    // ---- Output and notices --------------------------------------------------------------------------------------------

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _noticeMessage = string.Empty;

    [ObservableProperty]
    private string _noticeTitle = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _noticeSeverity = InfoBarSeverity.Warning;

    public bool HasNotice => NoticeMessage.Length > 0;

    [RelayCommand]
    private void CloseOutput()
    {
        OutputText = string.Empty;
        OutputNote = string.Empty;
    }

    [RelayCommand]
    private void CloseNotice() => NoticeMessage = string.Empty;

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        NoticeTitle = title;
        NoticeSeverity = severity;
        NoticeMessage = message;
    }

    private void ShowOutput(string title, string text, bool succeeded, string note = "")
    {
        OutputTitle = title;
        OutputText = text.Length == 0 ? "(no output)" : text;
        OutputTone = succeeded ? "Ok" : "Bad";
        OutputNote = note;
    }

    // ---- What each button may do now -----------------------------------------------------------------------------------

    public bool CanActivate => ActivateBlockedReason is null;

    public bool CanEdit => EditBlockedReason is null;

    public bool CanDelete => DeleteBlockedReason is null;

    public bool CanCreate => CreateBlockedReason is null;

    public string ActivateTip => ActivateBlockedReason ?? "Validate the policies, then review what activating this one changes before it is applied.";

    public string EditTip => EditBlockedReason ?? "Change one section of this policy (severity actions, scanner overrides, guardrail or firewall). The command is shown first.";

    public string DeleteTip => DeleteBlockedReason ?? "Delete this custom policy. The command is shown first.";

    public string CreateTip => CreateBlockedReason ?? "Create a new custom policy, optionally from a built-in preset. It stays inactive until you activate it.";

    private string? ActivateBlockedReason =>
        ChangesBlockedReasonNow
        ?? (SelectedRow is not { } row
            ? "Select a policy to activate."
            : row.IsActive
                ? "This policy is already active."
                : !row.HasSafeName ? UnsafeNameReason : null);

    private string? EditBlockedReason =>
        ChangesBlockedReasonNow
        ?? (SelectedRow is not { } row ? "Select a policy to edit." : !row.HasSafeName ? UnsafeNameReason : null);

    private string? DeleteBlockedReason =>
        ChangesBlockedReasonNow
        ?? (SelectedRow is not { } row
            ? "Select a custom policy to delete."
            : row.IsBuiltIn
                ? "Built-in policies cannot be deleted."
                : PolicyNames.Protected.Contains(row.Name)
                    ? $"The CLI never deletes '{row.Name}': it is one of the three protected policy names."
                    : !row.HasSafeName ? UnsafeNameReason : null);

    private string? CreateBlockedReason => ChangesBlockedReasonNow;

    private const string UnsafeNameReason = "This policy's name has characters that cannot be passed to the CLI safely, so the app will not act on it.";

    /// <summary>
    /// The check made again at the moment a change is requested: the list must still be a complete, recent read and nothing else may be
    /// running. Refuses with the reason in the notice bar; true when the caller must stop.
    /// </summary>
    private bool RefuseChange()
    {
        // The bound values only look at what the watcher has reported; a change requested now looks at the files (CUST-312).
        _ = Trust.CheckConfig();
        if (ChangesBlockedReasonNow is not { } reason)
        {
            return false;
        }

        ShowNotice("Changes are off", reason);
        NotifyTrust();
        return true;
    }

    /// <summary>The same check for a flow that is already running its own checks (it holds <see cref="IsRunning"/>): only the list's trust matters.</summary>
    private bool RefuseUntrusted() => ReasonToRefuseRun() is not null;

    /// <summary>
    /// Why the list may not authorize a change, from its trust alone (read now, files included); shown in the notice bar when there is one.
    /// The review's last question as well, asked when the operator confirms: a review that was open while config.yaml or .env changed must not run.
    /// </summary>
    private string? ReasonToRefuseRun()
    {
        _ = Trust.CheckConfig();

        // A read-only installation comes first: one reason, and the one a refresh would not cure.
        var reason = Services.Installation.BlockedReason
            ?? (State is PoliciesState.Loaded or PoliciesState.Empty
                ? Trust.Reason
                : Trust.Reason ?? "Changes are off until the policies have been read.");
        if (reason is null)
        {
            return null;
        }

        ShowNotice("Changes are off", reason);
        NotifyTrust();
        return reason;
    }

    // ---- Validate and test (read-only, run directly) -------------------------------------------------------------------

    [RelayCommand]
    private async Task ValidateAsync()
    {
        if (!CanRunReads)
        {
            return;
        }

        IsRunning = true;
        try
        {
            _ = await ValidateCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
            NotifyTrust();
        }
    }

    /// <summary>Runs <c>policy validate</c> and shows what it printed; true when it exited 0.</summary>
    internal async Task<bool> ValidateCoreAsync()
    {
        Validation = PolicyValidation.Running;
        var (passed, transcript, note) = await RunCheckAsync(_backend.ValidateArgv).ConfigureAwait(true);
        _validatedAt = DateTimeOffset.Now;
        Validation = passed ? PolicyValidation.Passed : PolicyValidation.Failed;
        ShowOutput("defenseclaw " + string.Join(' ', _backend.ValidateArgv), transcript, passed, note);
        return passed;
    }

    [RelayCommand]
    private async Task RunTestsAsync()
    {
        if (!CanRunReads)
        {
            return;
        }

        IsRunning = true;
        try
        {
            var (passed, transcript, note) = await RunCheckAsync(_backend.TestArgv).ConfigureAwait(true);
            ShowOutput("defenseclaw " + string.Join(' ', _backend.TestArgv), transcript, passed, note);
        }
        finally
        {
            IsRunning = false;
            NotifyTrust();
        }
    }

    private async Task<(bool Passed, string Transcript, string Note)> RunCheckAsync(IReadOnlyList<string> argv)
    {
        CliInvocation invocation;
        try
        {
            invocation = await RunReadAsync(argv).ConfigureAwait(true);
        }
        catch (CliNotFoundException ex)
        {
            return (false, $"The defenseclaw CLI was not found. {ex.Message}", string.Empty);
        }

        if (invocation.FailureReason is { Length: > 0 } failure)
        {
            var partial = Transcript(invocation);
            return (false, partial.Length == 0 ? failure : partial + "\n" + failure, string.Empty);
        }

        var transcript = Transcript(invocation);
        var passed = invocation.ExitCode == 0;
        var note = !passed && transcript.Contains("'opa' binary not found", StringComparison.Ordinal)
            ? "OPA (the opa executable) is not installed or not on PATH, so DefenseClaw cannot compile or test the Rego bundle. Install OPA, then validate again."
            : string.Empty;
        return (passed, transcript, note);
    }

    // ---- Activate ------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (RefuseChange())
        {
            return;
        }

        if (ActivateBlockedReason is not null || SelectedRow is not { } row)
        {
            ShowNotice("Cannot activate", ActivateBlockedReason ?? "Select a policy to activate.");
            return;
        }

        IsRunning = true;
        try
        {
            // 1. Validate. A bundle that does not validate is never offered for activation.
            if (!await ValidateCoreAsync().ConfigureAwait(true))
            {
                ShowNotice(
                    "Activation is withheld",
                    $"'{row.Name}' was not offered because policy validate did not pass. The output is below; fix what it names, then try again.");
                return;
            }

            // The wait above is a chance for the list to have aged: ask again.
            if (RefuseUntrusted())
            {
                return;
            }

            // 2. What it changes: the policy and the one in force, as show reads them.
            var target = await ReadDetailAsync(row.Name).ConfigureAwait(true);
            if (target.Detail is null)
            {
                ShowNotice("Activation is withheld", $"'{row.Name}' could not be read ({target.Error}), so what activating it changes cannot be shown.");
                return;
            }

            PolicyDetail? current = null;
            if (ActiveName is { } active)
            {
                var read = await ReadDetailAsync(active).ConfigureAwait(true);
                current = read.Detail;
            }

            OpenActivateReview(row, target.Detail, current, PolicyDiff.Compare(current, target.Detail));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceFault("activate", ex);
            ShowNotice("Activation is withheld", $"Could not prepare the activation: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            NotifyTrust();
        }
    }

    private void OpenActivateReview(PolicyRow row, PolicyDetail target, PolicyDetail? current, PolicySwitchSummary summary)
    {
        var from = ActiveName is { } active ? $"the active policy '{active}'" : "the policy in force";
        var effect = summary.IsComplete
            ? summary.IsEmpty
                ? $"It reads the same as {from} in every section this app can see."
                : $"{Plural(summary.Lines.Count, "change")} from {from}"
                  + (summary.Weakens ? $", {summary.WeakeningLines.Count.ToString(CultureInfo.InvariantCulture)} of them reducing protection." : ", none of them reducing protection.")
            : "What it changes could not be compared with the policy in force.";

        Review.Open(
            heading: $"Activate policy '{row.Name}'?",
            explanation: $"Applies '{row.Name}' to config.yaml (skill actions and rescan settings, plus any Cisco AI Defense or webhook settings the policy file carries, which policy show does not display) and syncs the OPA data.json. {effect}",
            steps: new[] { new DiscoverStep(_backend.ActivateArgv(row.Name), $"Activate policy {row.Name}") },
            onFinished: result => AfterChangeAsync(result, $"Activated '{row.Name}'."),
            primaryText: "Activate policy",
            names: new[] { row.Name },
            extraWarnings: SwitchWarnings(summary, from),
            acknowledgement: summary.Weakens ? WeakeningAcknowledgement : null);
    }

    // ---- Delete --------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (RefuseChange())
        {
            return;
        }

        if (DeleteBlockedReason is not null || SelectedRow is not { } row)
        {
            ShowNotice("Cannot delete", DeleteBlockedReason ?? "Select a custom policy to delete.");
            return;
        }

        var active = row.IsActive;
        PolicySwitchSummary? fallback = null;
        IsRunning = true;
        try
        {
            if (active)
            {
                // The CLI refuses to delete the active policy unless forced, and then re-activates 'default': a switch like any other.
                var current = (await ReadDetailAsync(row.Name).ConfigureAwait(true)).Detail;
                var fallbackDetail = (await ReadDetailAsync("default").ConfigureAwait(true)).Detail;
                fallback = fallbackDetail is null
                    ? PolicyDiff.Incomplete("The 'default' policy could not be read, so what re-activating it changes cannot be compared. Assume it can reduce protection.")
                    : PolicyDiff.Compare(current, fallbackDetail);
            }
        }
        finally
        {
            IsRunning = false;
        }

        if (RefuseChange())
        {
            return;
        }

        var explanation = active
            ? $"Deletes the custom policy file '{row.Name}' from your policy folder. It is the active policy, so the CLI then re-activates 'default' (--force); " +
              (fallback!.IsComplete ? $"that is {Plural(fallback.Lines.Count, "change")} from it." : "what that changes could not be compared.")
            : $"Deletes the custom policy file '{row.Name}' from your policy folder. The app cannot bring it back.";

        Review.Open(
            heading: $"Delete policy '{row.Name}'?",
            explanation: explanation,
            steps: new[] { new DiscoverStep(_backend.DeleteArgv(row.Name, force: active), $"Delete policy {row.Name}", CommandTier.Destructive) },
            onFinished: result => AfterChangeAsync(result, $"Deleted '{row.Name}'."),
            primaryText: "Delete policy",
            names: new[] { row.Name },
            extraWarnings: fallback is null ? null : SwitchWarnings(fallback, "the active policy"),
            acknowledgement: fallback is { Weakens: true } ? WeakeningAcknowledgement : null);
    }

    // ---- Create and edit -----------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Create()
    {
        if (RefuseChange())
        {
            return;
        }

        Form.OpenCreate();
    }

    [RelayCommand]
    private void Edit()
    {
        if (RefuseChange())
        {
            return;
        }

        if (EditBlockedReason is not null || SelectedRow is not { } row)
        {
            ShowNotice("Cannot edit", EditBlockedReason ?? "Select a policy to edit.");
            return;
        }

        Form.OpenEdit(row.Name, row.IsBuiltIn, row.IsActive);
    }

    [RelayCommand]
    private void CancelForm() => Form.Close();

    [RelayCommand]
    private async Task SubmitFormAsync()
    {
        if (!Form.IsOpen)
        {
            return;
        }

        if (RefuseChange())
        {
            Form.Close();
            return;
        }

        if (Form.IsCreate)
        {
            SubmitCreate();
        }
        else
        {
            await SubmitEditAsync().ConfigureAwait(true);
        }
    }

    private void SubmitCreate()
    {
        if (!Form.TryBuildCreate(_all.Select(r => r.Name), out var create) || create is null)
        {
            return;
        }

        var mode = Form.Mode;
        Form.Close();
        var from = create.FromPreset.Length > 0 ? $" from the '{create.FromPreset}' preset" : string.Empty;
        Review.Open(
            heading: $"Create policy '{create.Name}'?",
            explanation: $"Writes a new custom policy file '{create.Name}.yaml'{from} to your policy folder. It is not applied: nothing changes until you activate it.",
            steps: new[] { new DiscoverStep(_backend.CreateArgv(create), $"Create policy {create.Name}") },
            onFinished: result =>
            {
                if (result.Succeeded)
                {
                    _selectAfterLoad = create.Name;
                }

                return AfterChangeAsync(result, $"Created '{create.Name}'.");
            },
            primaryText: "Create policy",
            onCancelled: () => Form.Mode = mode,
            names: new[] { create.Name });
    }

    private async Task SubmitEditAsync()
    {
        if (!Form.TryBuildEdit(out var edit) || edit is null || SelectedRow is not { } row || !row.HasSafeName)
        {
            return;
        }

        var read = await ReadDetailAsync(row.Name).ConfigureAwait(true);
        if (read.Detail is null)
        {
            Form.Error = $"'{row.Name}' could not be read ({read.Error}), so what this edit changes cannot be shown.";
            return;
        }

        if (RefuseChange())
        {
            Form.Close();
            return;
        }

        // An edit of the active policy is synced to the live OPA data at once (_save_and_maybe_sync); any other is a draft.
        var live = row.IsActive;
        var summary = PolicyDiff.Compare(read.Detail, edit.ApplyTo(read.Detail));
        var mode = Form.Mode;
        Form.Close();

        var where = row.IsBuiltIn
            ? $"'{row.Name}' is a built-in policy, so the CLI saves the result as a custom copy named '{row.Name}' in your policy folder, which then shadows the built-in. "
            : string.Empty;
        var effect = live
            ? "It is the active policy, so the change is applied to the live policy data immediately."
            : "It is not the active policy, so the change is saved as a draft and does not apply until you activate it.";

        Review.Open(
            heading: $"Edit policy '{row.Name}'?",
            explanation: where + effect,
            steps: new[] { new DiscoverStep(_backend.EditArgv(row.Name, edit), $"Edit policy {row.Name} ({edit.Section.ToString().ToLowerInvariant()})") },
            onFinished: result => AfterChangeAsync(result, $"Edited '{row.Name}'."),
            primaryText: "Apply edit",
            onCancelled: () => Form.Mode = mode,
            names: new[] { row.Name },
            extraWarnings: SwitchWarnings(summary, $"'{row.Name}' as it is now", showWeakening: live),
            acknowledgement: live && summary.Weakens ? WeakeningAcknowledgement : null);
    }

    // ---- After a change ------------------------------------------------------------------------------------------------

    private string? _selectAfterLoad;

    private async Task AfterChangeAsync(DiscoverReviewResult result, string doneText)
    {
        // The policies may have changed whether or not the command succeeded: read them again either way.
        _details.Clear();
        _rawText.Clear();
        await LoadAsync().ConfigureAwait(true);

        if (_selectAfterLoad is { } name)
        {
            _selectAfterLoad = null;
            SelectedRow = Rows.FirstOrDefault(r => r.Name == name) ?? SelectedRow;
        }

        if (result.Succeeded)
        {
            ShowNotice("Done", doneText, InfoBarSeverity.Success);
        }
        else
        {
            ShowNotice("The command did not finish", "The review dialog and the Activity panel have its output. The list above is read again.", InfoBarSeverity.Warning);
        }
    }

    // ---- Review warnings -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The warning bars of a review that switches or edits a policy: one for the changes that reduce protection (or for a comparison
    /// that could not be made), one for the rest.
    /// </summary>
    internal static IReadOnlyList<CommandReviewWarning> SwitchWarnings(PolicySwitchSummary summary, string comparedWith, bool showWeakening = true)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var warnings = new List<CommandReviewWarning>();
        if (!summary.IsComplete)
        {
            warnings.Add(new CommandReviewWarning("Cannot compare", summary.Lines[0].Text));
            return warnings;
        }

        var weaker = summary.Lines.Where(l => l.Weakens).ToList();
        var rest = summary.Lines.Where(l => !l.Weakens).ToList();
        if (weaker.Count > 0)
        {
            warnings.Add(new CommandReviewWarning(
                showWeakening ? "Reduces protection" : "Would reduce protection once activated",
                $"Compared with {comparedWith}:\n" + Bullets(weaker)));
        }

        if (rest.Count > 0)
        {
            warnings.Add(new CommandReviewWarning("Other changes", $"Compared with {comparedWith}:\n" + Bullets(rest)));
        }

        return warnings;
    }

    private static string Bullets(IReadOnlyList<PolicyChangeLine> lines)
    {
        var shown = lines.Take(MaxChangeLines).Select(l => "- " + l.Text).ToList();
        if (lines.Count > MaxChangeLines)
        {
            shown.Add($"... and {(lines.Count - MaxChangeLines).ToString(CultureInfo.InvariantCulture)} more.");
        }

        return string.Join('\n', shown);
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";
}
