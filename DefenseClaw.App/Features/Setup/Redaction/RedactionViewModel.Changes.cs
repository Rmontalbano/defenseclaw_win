using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Text;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels.Redaction;

/// <summary>The two ways in: the quick sheet (one profile, everywhere) and the advanced editor (all 21 operations), and the result they share.</summary>
public sealed partial class RedactionViewModel
{
    /// <summary>The sentence on the tick a review asks for when raw content would start to flow.</summary>
    public const string RawAcknowledgement = "I understand that raw, unredacted content will start to flow where it is redacted today.";

    /// <summary>The sentence on the tick when the profile is none but no leg moves today: what follows that profile sends raw content.</summary>
    public const string NoneAcknowledgement = "I understand that profile none sends content unredacted, now and wherever it is followed later.";

    // ------------------------------------------------------------------ the result card

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome), nameof(CanApplyPreview), nameof(ShowOutcomeRaw))]
    private RedactionOutcomeViewModel? _outcome;

    /// <summary>"Show CLI output": the verbatim text under the result of any run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOutcomeRaw))]
    private bool _showRaw;

    public bool HasOutcome => Outcome is not null;

    /// <summary>The raw text is drawn when the operator asked for it, or when it is the result (a text-only read, a failure).</summary>
    public bool ShowOutcomeRaw => Outcome is { HasRaw: true } o && (ShowRaw || o.Kind == RedactionOutcomeKind.Failed || o.Facts.Count == 0 && !o.HasDiff && o.Kind == RedactionOutcomeKind.Read);

    [RelayCommand]
    private void DismissOutcome() => Outcome = null;

    // ------------------------------------------------------------------ the advanced editor

    /// <summary>The 21 operations, in the Mac's order.</summary>
    public IReadOnlyList<RedactionOperationInfo> Operations => RedactionOperations.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOperation), nameof(IsChange), nameof(CommandText), nameof(PrimaryLabel), nameof(RestartEnabled), nameof(CanRun), nameof(HasCommand))]
    private RedactionOperationInfo _selectedOperation = RedactionOperations.All[0];

    /// <summary>
    /// Preview only (the default): the command is run with <c>--dry-run</c> and writes nothing. Turned off, the same button reviews the apply.
    /// It is on again whenever the operation changes and after an apply, the way the Mac resets it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandText), nameof(PrimaryLabel), nameof(RestartEnabled), nameof(CanRun), nameof(CanApplyPreview))]
    private bool _dryRun = true;

    /// <summary>Restart the gateway after the apply (<c>--restart</c>); off by default, and off for any preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandText))]
    private bool _restart;

    partial void OnSelectedOperationChanged(RedactionOperationInfo value)
    {
        Form.Select(value.Operation);
        ResetEditor();
        OnFormChanged(this, EventArgs.Empty);
    }

    /// <summary>The advanced editor's own safe state: preview only, no restart. A new operation starts in it.</summary>
    private void ResetEditor()
    {
        DryRun = true;
        Restart = false;
    }

    /// <summary>
    /// Puts the whole window back in its safe state: the advanced editor previews (dry run on, no restart) and the quick sheet has no restart
    /// and nothing armed. This is what it starts in, and what it returns to when a review is cancelled or an apply finishes, and each time the
    /// window is opened again (<see cref="ReopenAsync"/>) — the way the Mac resets its dry-run toggle on every open and reset, so no earlier
    /// decision to apply carries over to the next time.
    /// </summary>
    public void ResetToPreview()
    {
        ResetEditor();
        QuickRestart = false;
        QuickArmed = false;
    }

    /// <summary>The window was opened again while it was already open: the editor is reset to a preview and the policy is read again.</summary>
    public async Task ReopenAsync()
    {
        ResetToPreview();
        await Refresh().ConfigureAwait(true);
    }

    partial void OnDryRunChanged(bool value)
    {
        // A preview has no restart to choose: the box is off and the command carries neither flag.
        if (value)
        {
            Restart = false;
        }
    }

    public bool IsReadOperation => !SelectedOperation.IsMutation;

    public bool IsChange => SelectedOperation.IsMutation;

    /// <summary>The restart box is live only while the command will apply.</summary>
    public bool RestartEnabled => IsChange && !DryRun;

    public string PrimaryLabel => IsReadOperation ? "Run" : DryRun ? "Preview" : "Review and apply…";

    public string Problem => Form.Problem;

    public bool HasProblem => Problem.Length > 0;

    public bool HasCommand => !HasProblem;

    /// <summary>The exact command the primary button would run, or empty while the form is incomplete.</summary>
    public string CommandText => HasProblem ? string.Empty : CommandReview.CommandLine(CommandReview.DefaultExecutable, CurrentArgv());

    /// <summary>The primary button can be pressed. An apply also needs a policy that was read.</summary>
    public bool CanRun => CanAct && !HasProblem && (IsReadOperation || DryRun || MayChange);

    private string[] CurrentArgv()
    {
        var operation = SelectedOperation.Operation;
        var inputs = Form.ToInputs();
        return SelectedOperation.IsMutation
            ? DryRun ? RedactionArgv.Preview(operation, inputs) : RedactionArgv.Apply(operation, inputs, Restart)
            : RedactionArgv.Read(operation, inputs);
    }

    private void OnFormChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasCommand));
        OnPropertyChanged(nameof(CommandText));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanApplyPreview));
    }

    /// <summary>The primary button: run a read, preview a change, or (dry run off) review the apply.</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (!CanRun)
        {
            if (CanAct && HasProblem)
            {
                ShowNotice("Complete the form", Problem);
            }
            else if (!IsReadOperation && !DryRun && !MayChange)
            {
                ShowNotice("Changes are off", ChangesBlockedReason);
            }

            return;
        }

        var info = SelectedOperation;
        var inputs = Form.ToInputs();
        if (!info.IsMutation)
        {
            await ReadOperationAsync(info, inputs).ConfigureAwait(true);
        }
        else if (DryRun)
        {
            _ = await PreviewAsync(info.Operation, inputs, fromQuick: false).ConfigureAwait(true);
        }
        else
        {
            await BeginApplyAsync(info.Operation, inputs, Restart, fromQuick: false).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ the quick sheet

    /// <summary>The profile the quick sheet applies: one of the four built-ins (the view's segments are those four, in this order).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickNone), nameof(QuickDescription), nameof(QuickCommandText), nameof(CanApplyPreview))]
    private string _quickProfile;

    /// <summary>What the chosen profile does, in the CLI's words.</summary>
    public string QuickDescription => RedactionVocabulary.Describe(QuickProfile);

    /// <summary>Restart the gateway after applying; off by default, as on the Mac.</summary>
    [ObservableProperty]
    private bool _quickRestart;

    /// <summary>
    /// The first press of Apply with the profile <c>none</c> only arms the sheet; the second goes on to the review. Choosing another profile
    /// disarms it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickApplyLabel))]
    private bool _quickArmed;

    partial void OnQuickProfileChanged(string value) => QuickArmed = false;

    public bool IsQuickNone => string.Equals(QuickProfile, RedactionVocabulary.NoRedaction, StringComparison.Ordinal);

    public string QuickNoneWarning =>
        "Profile none stops redacting: raw governed content is kept in local SQLite and sent to every destination you configured. The managed enterprise destination, if there is one, stays locked.";

    public string QuickArmedText => "Danger: press Apply again to review removing all redaction.";

    public string QuickApplyLabel => QuickArmed ? "Apply: press again" : "Apply…";

    private RedactionInputs QuickInputs => new() { Profile = QuickProfile };

    /// <summary>The preview command for the chosen profile.</summary>
    public string QuickCommandText => CommandReview.CommandLine(
        CommandReview.DefaultExecutable,
        RedactionArgv.Problems(RedactionOperation.ApplyEverywhere, QuickInputs).Count == 0
            ? RedactionArgv.Preview(RedactionOperation.ApplyEverywhere, QuickInputs)
            : []);

    public bool CanQuickPreview => CanAct;

    public bool CanQuickApply => CanAct && MayChange;

    [RelayCommand]
    private async Task PreviewQuickAsync()
    {
        if (CanQuickPreview)
        {
            _ = await PreviewAsync(RedactionOperation.ApplyEverywhere, QuickInputs, fromQuick: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ApplyQuickAsync()
    {
        if (!CanQuickApply)
        {
            if (CanAct)
            {
                ShowNotice("Changes are off", ChangesBlockedReason);
            }

            return;
        }

        if (IsQuickNone && !QuickArmed)
        {
            QuickArmed = true;
            return;
        }

        QuickArmed = false;
        await BeginApplyAsync(RedactionOperation.ApplyEverywhere, QuickInputs, QuickRestart, fromQuick: true).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ previews

    /// <summary>
    /// A preview is on screen for the very command the form (or the sheet) would run now, it changes the configuration, and a change may start:
    /// the result card then offers to go on and apply it.
    /// </summary>
    public bool CanApplyPreview =>
        CanAct && MayChange && Outcome is { CanApply: true, Signature.Length: > 0 } o && string.Equals(o.Signature, SignatureOfCurrent(o.FromQuick), StringComparison.Ordinal);

    private string SignatureOfCurrent(bool fromQuick)
    {
        try
        {
            if (fromQuick)
            {
                return Signature(RedactionArgv.Preview(RedactionOperation.ApplyEverywhere, QuickInputs));
            }

            return IsChange && !HasProblem ? Signature(RedactionArgv.Preview(SelectedOperation.Operation, Form.ToInputs())) : string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string Signature(IReadOnlyList<string> argv) => string.Join('\u001f', argv);

    /// <summary>Goes on to apply what the result card previewed. The restart choice is the one beside the button that started it.</summary>
    [RelayCommand]
    private async Task ApplyPreviewedAsync()
    {
        if (!CanApplyPreview || Outcome is not { Operation: { } operation, Inputs: { } inputs } outcome)
        {
            return;
        }

        if (!outcome.FromQuick)
        {
            DryRun = false;
        }

        await BeginApplyAsync(operation, inputs, outcome.FromQuick ? QuickRestart : Restart, outcome.FromQuick).ConfigureAwait(true);
    }

    /// <summary>Runs the preview of <paramref name="operation"/> and shows it. Null when it did not produce one (the result card says why).</summary>
    internal async Task<RedactionResult?> PreviewAsync(RedactionOperation operation, RedactionInputs inputs, bool fromQuick)
    {
        if (!CanAct)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            return await PreviewCoreAsync(operation, inputs, fromQuick).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceFault("preview", ex);
            Outcome = Failed(RedactionOperations.Info(operation), string.Empty, "The preview failed: " + ex.Message, string.Empty);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<RedactionResult?> PreviewCoreAsync(RedactionOperation operation, RedactionInputs inputs, bool fromQuick)
    {
        var info = RedactionOperations.Info(operation);
        var argv = RedactionArgv.Preview(operation, inputs);
        var command = CommandReview.CommandLine(CommandReview.DefaultExecutable, argv);

        var run = await ToRunAsync(() => RunPreviewAsync(argv), "The preview of " + info.Title.ToLowerInvariant()).ConfigureAwait(true);
        if (!run.Ok)
        {
            Outcome = Failed(info, command, run.Error, run.Stdout);
            return null;
        }

        if (!RedactionResultParser.TryParse(run.Stdout, out var result, out var error) || result is null)
        {
            Outcome = Failed(info, command, "The preview could not be read: " + error, run.Stdout);
            return null;
        }

        Outcome = new RedactionOutcomeViewModel
        {
            Kind = RedactionOutcomeKind.Preview,
            Heading = "Preview: " + info.Title,
            Badge = "Dry run: nothing written",
            Tone = result.NewlyUnredacted > 0 ? "Bad" : "Neutral",
            Command = command,
            Headline = result.NewlyUnredacted > 0
                ? $"{result.Headline} {Legs(result.NewlyUnredacted)} would carry raw, unredacted content."
                : result.Headline,
            Breakdown = result.Breakdown,
            Diff = result.Groups.Select(static g => new RedactionDiffRow(g)).ToArray(),
            Notes = PreviewNotes(result),
            Warnings = result.Warnings.Select(static w => new RedactionWarningRow(w)).ToArray(),
            Raw = run.Stdout,
            Result = result,
            Signature = Signature(argv),
            Operation = operation,
            Inputs = inputs,
            FromQuick = fromQuick,
        };
        return result;
    }

    private static IReadOnlyList<string> PreviewNotes(RedactionResult result)
    {
        var notes = new List<string> { "Nothing was written and the gateway was not restarted." };
        if (result.Changed)
        {
            notes.Add(result.PlanUnchanged
                ? $"The effective plan stays exactly the same (digest {Short(result.AfterPlanDigest)}): only the file would be rewritten."
                : $"The effective plan would go from {Short(result.BeforePlanDigest)} to {Short(result.AfterPlanDigest)}.");
        }

        notes.AddRange(result.Locked.Select(static l => DisplayNames.Visible($"Managed policy stays locked: {l.Destination} ({string.Join(", ", l.Profiles)}).")));
        return notes;
    }

    private static string Short(string digest) => digest.Length > 12 ? digest[..12] : digest;

    private static string Legs(int count) =>
        count == 1 ? "1 delivery leg" : $"{count.ToString(CultureInfo.InvariantCulture)} delivery legs";

    private static RedactionOutcomeViewModel Failed(RedactionOperationInfo info, string command, string message, string raw) => new()
    {
        Kind = RedactionOutcomeKind.Failed,
        Heading = info.Title,
        Badge = "Failed",
        Tone = "Bad",
        Command = command,
        Headline = DisplayNames.Visible(message),
        Raw = raw,
    };

    // ------------------------------------------------------------------ reads

    private async Task ReadOperationAsync(RedactionOperationInfo info, RedactionInputs inputs)
    {
        if (!CanAct)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (info.Operation == RedactionOperation.Status)
            {
                await ReadPolicyAsync().ConfigureAwait(true);
                Outcome = StatusIsCurrent
                    ? new RedactionOutcomeViewModel
                    {
                        Kind = RedactionOutcomeKind.Read,
                        Heading = info.Title,
                        Badge = "Read",
                        Tone = SummaryTone,
                        Command = CommandReview.CommandLine(CommandReview.DefaultExecutable, RedactionArgv.Read(RedactionOperation.Status, inputs)),
                        Headline = Summary,
                        Notes = ["The status card above shows the result."],
                    }
                    : Failed(info, string.Empty, StatusError, string.Empty);
                return;
            }

            var argv = RedactionArgv.Read(info.Operation, inputs);
            var command = CommandReview.CommandLine(CommandReview.DefaultExecutable, argv);
            var run = await ToRunAsync(() => RunReadAsync(argv), "setup redaction " + string.Join(' ', info.Path)).ConfigureAwait(true);
            Outcome = run.Ok ? ReadOutcome(info, inputs, command, run.Stdout) : Failed(info, command, run.Error, run.Stdout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceFault("read", ex);
            Outcome = Failed(info, string.Empty, "The read failed: " + ex.Message, string.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private RedactionOutcomeViewModel ReadOutcome(RedactionOperationInfo info, RedactionInputs inputs, string command, string stdout)
    {
        RedactionOutcomeViewModel Read(string headline, IReadOnlyList<RedactionFact>? facts = null, string raw = "") => new()
        {
            Kind = RedactionOutcomeKind.Read,
            Heading = info.Title,
            Badge = "Read",
            Command = command,
            Headline = headline,
            Facts = facts ?? [],
            Raw = raw,
        };

        switch (info.Operation)
        {
            case RedactionOperation.ProfileList:
                if (RedactionReadings.TryParseProfileList(stdout, out var list, out var listError) && list is not null)
                {
                    Form.SetProfiles(list.Names);
                    return Read(
                        $"{list.Names.Count.ToString(CultureInfo.InvariantCulture)} profiles.",
                        [
                            new("Built-in", string.Join(", ", list.Names.Where(RedactionVocabulary.BuiltInProfiles.Contains))),
                            new("Custom", list.Custom.Count == 0 ? "none" : DisplayNames.Visible(string.Join(", ", list.Custom))),
                        ],
                        stdout);
                }

                return Failed(info, command, "The profile list could not be read: " + listError, stdout);

            case RedactionOperation.ProfileShow:
                if (RedactionReadings.TryParseProfile(stdout, out var profile, out var profileError) && profile is not null)
                {
                    var facts = new List<RedactionFact> { new("Kind", DisplayNames.Visible(profile.Kind)), new("Detectors", DisplayNames.Visible(profile.DetectorsText)) };
                    facts.AddRange(profile.FieldModes.Select(static m => new RedactionFact(DisplayNames.Visible(m.Class), DisplayNames.Visible(m.Mode))));
                    return Read(DisplayNames.Visible($"The {profile.Name} profile."), facts, stdout);
                }

                return Failed(info, command, "The profile could not be read: " + profileError, stdout);

            case RedactionOperation.RouteList:
                if (RedactionReadings.TryParseRoutes(stdout, out var routes, out var routesError) && routes is not null)
                {
                    _routes[inputs.Destination] = routes.Routes;
                    Form.RefreshRoutes();
                    return Read(
                        routes.Routes.Count == 0 ? DisplayNames.Visible($"{inputs.Destination} has no ordered routes: it uses its default policy or one send policy.") : $"{Plural(routes.Routes.Count, "route")}, tried in this order.",
                        routes.Routes.Select(static r => new RedactionFact(DisplayNames.Visible($"{r.Position.ToString(CultureInfo.InvariantCulture)}. {r.Name}"), DisplayNames.Visible(r.Summary))).ToArray(),
                        stdout);
                }

                return Failed(info, command, "The routes could not be read: " + routesError, stdout);

            default:
                // bucket list and destination show print text only: the text is the answer.
                return Read("The CLI printed this (the command has no JSON form).", raw: stdout.TrimEnd());
        }
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";
}
