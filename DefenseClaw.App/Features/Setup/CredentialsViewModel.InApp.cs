using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Credentials card's in-app writes (CUST-221): Set and Fill missing with the value typed in a masked box, reviewed with the value masked,
/// and stored by the CLI itself through a pseudo-console (<see cref="SecretPtyRunner"/>) - and, where that cannot work, the console window of
/// CUST-266, opened automatically.
/// <para>
/// <b>The order of every change:</b> the installation guard (a managed or invalid installation is only read: nothing is offered, and a box
/// that is open closes and forgets), then the masked box, then the review (<see cref="Review"/>: the exact command and the value masked; the
/// shared overlay asks the guard again at Confirm and records one refusal if it turned read-only meanwhile), and only then the run - which the
/// runner gates once more. Nothing runs from a button press alone.
/// </para>
/// <para>
/// <b>Fill missing</b> opens the box on every required credential that is unset and reviews one <c>keys set</c> per value typed, run in order and
/// stopping at the first that fails (the shared review's rule). That is deliberate: <c>keys fill-missing</c> asks the same question once per
/// variable, and driving a conversation of unknown length from outside is the one place a mismatch between what was reviewed and what ran could
/// hide. <c>keys fill-missing --yes</c> itself is still there as a console window.
/// </para>
/// <para>
/// <b>The console window is the fallback and nothing else:</b> it is used when the app cannot type (Windows before 10 1809, a container runtime),
/// and when a run reports that the route itself failed with nothing typed (<see cref="SecretPtyResult.RouteFailed"/>: the pseudo-console could not
/// start, or the CLI never showed its prompt). In that case the card remembers it and offers only the console for the rest of the session. A run that
/// typed the value and was then told no by the CLI is not that: a console would hear the same, and the CLI's answer is shown.
/// </para>
/// </summary>
public sealed partial class CredentialsViewModel
{
    /// <summary>What stands for a hidden value in a review: a fixed run of dots, not the value's length (which is stated in words).</summary>
    internal const string MaskedValue = "••••••••••••";

    private bool _routeFailed;

    /// <summary>The confirm-and-run overlay a typed value goes through (the Setup page's own).</summary>
    public DiscoverActionReview Review { get; }

    private Func<bool>? _ptyAvailable;

    /// <summary>Whether a pseudo-console can be used; null asks Windows. A test replaces it, so the console route can be exercised on any machine; what is drawn follows.</summary>
    internal Func<bool>? PtyAvailable
    {
        get => _ptyAvailable;
        set
        {
            _ptyAvailable = value;
            RaiseRoute();
        }
    }

    /// <summary>
    /// How a value is stored: <c>(variable name, value, cancel)</c> to the result of the run. A test replaces it so no pseudo-console starts and no
    /// <c>defenseclaw</c> is run; the default is <see cref="SecretPtyRunner.SetKeyAsync"/> over the app's runner.
    /// </summary>
    internal Func<string, SecretValue, CancellationToken, Task<SecretPtyResult>>? RunSet { get; set; }

    /// <summary>The app can type a value itself: Windows has a pseudo-console, the runtime is not a container, and it has not failed this session.</summary>
    public bool InAppAvailable =>
        !_routeFailed &&
        (PtyAvailable?.Invoke() ?? SecretPtyRunner.IsSupportedHere) &&
        _services.Paths.Runtime.Kind != RuntimeKind.Container;

    /// <summary>The console buttons beside Set and Fill missing: drawn only while those type the value in the app (otherwise they <i>are</i> the console).</summary>
    public bool ShowFillTerminalButton => InAppAvailable;

    /// <summary>Fill missing's primary button: what it does, or why it cannot.</summary>
    public string FillMissingButtonToolTip => ChangesBlockedReason ??
        (InAppAvailable
            ? "Opens a masked box on every required credential that is unset. You review the commands, with the values masked, before anything runs."
            : FillMissingToolTip);

    public string FillMissingAutomationName => InAppAvailable ? "Fill missing credentials" : "Fill missing credentials in a terminal";

    /// <summary>The caption under the card: how a value gets to the CLI, said for the route that is in use.</summary>
    public string FooterText => InAppAvailable
        ? "A value is typed into a masked box here, reviewed with the value hidden, and handed to the CLI's own hidden prompt in a pseudo-console. " +
          "It is never on a command line or in Activity, and the app does not keep it. The console button on a row opens a console window instead."
        : "Values are never shown or passed through this app. The CLI reads them at a hidden console prompt, so Set and Fill missing open a console window; " +
          "this list refreshes when you come back.";

    /// <summary>Fill missing is open: the boxes of the credentials that are unset are showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReviewFill))]
    private bool _isFilling;

    /// <summary>How many of the open boxes hold a value.</summary>
    public int FillEntryCount => Rows.Count(r => r.IsEditing && r.HasEntry);

    public bool CanReviewFill => IsFilling && FillEntryCount > 0 && CanChange && CanStartReview;

    public string FillReviewText => FillEntryCount == 1 ? "Review 1 value…" : $"Review {FillEntryCount.ToString(CultureInfo.CurrentCulture)} values…";

    /// <summary>The Review button of the Fill missing bar: what it does, or why it cannot.</summary>
    public string FillReviewToolTip => ChangesBlockedReason ??
        (FillEntryCount > 0
            ? "Show the exact commands with the values masked. Nothing runs until you confirm."
            : "Type a value in at least one box first.");

    /// <summary>Nothing else is being reviewed or stored: a row may open its review.</summary>
    internal bool CanStartReview => !Review.IsOpen && !Review.IsRunning;

    /// <summary>Called by a row when it opened, closed, or gained or lost its value: the Fill missing bar follows.</summary>
    internal void EntriesChanged()
    {
        OnPropertyChanged(nameof(FillEntryCount));
        OnPropertyChanged(nameof(CanReviewFill));
        OnPropertyChanged(nameof(FillReviewText));
        OnPropertyChanged(nameof(FillReviewToolTip));
        ReviewFillCommand.NotifyCanExecuteChanged();
    }

    private void OnReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscoverActionReview.IsOpen) or nameof(DiscoverActionReview.IsRunning))
        {
            foreach (var row in Rows)
            {
                row.RefreshEntryState();
            }

            EntriesChanged();
        }
    }

    /// <summary>The route changed (it failed this session): Set, Fill missing and the footer are drawn for the other one.</summary>
    private void RaiseRoute()
    {
        OnPropertyChanged(nameof(InAppAvailable));
        OnPropertyChanged(nameof(ShowFillTerminalButton));
        OnPropertyChanged(nameof(FillMissingButtonToolTip));
        OnPropertyChanged(nameof(FillMissingAutomationName));
        OnPropertyChanged(nameof(FooterText));
        foreach (var row in Rows)
        {
            row.RefreshRoute();
        }
    }

    /// <summary>Drops every typed value and closes every box (and Fill missing): the list was read again, the installation turned read-only, the page was left.</summary>
    public void CancelEntries() => ForgetEntries();

    private void ForgetEntries()
    {
        foreach (var row in Rows)
        {
            row.IsEditing = false;
            row.ClearEntry();
        }

        IsFilling = false;
        EntriesChanged();
    }

    // ------------------------------------------------------------------------------------------------------- fill missing

    /// <summary>
    /// Fill missing: opens a masked box on every required credential that is unset, where the app can type values; otherwise the console window
    /// running <c>keys fill-missing --yes</c>, as before.
    /// </summary>
    [RelayCommand]
    public async Task BeginFillAsync()
    {
        // The guard comes first, whichever route follows.
        if (_services.Installation.ReasonFor(FillMissingArgv) is { } blocked)
        {
            Note = blocked;
            NoteKey = "Warn";
            return;
        }

        if (!InAppAvailable)
        {
            await OpenFillMissingInTerminalAsync().ConfigureAwait(true);
            return;
        }

        var missing = Rows.Where(r => r.IsMissingRequired && r.CanSet && r.OffersInApp).ToArray();
        if (missing.Length == 0)
        {
            Note = HasLoaded ? "No required credential is missing." : "Read the credential list first (Refresh).";
            NoteKey = HasLoaded ? "Ok" : "Neutral";
            return;
        }

        foreach (var row in missing)
        {
            row.IsEditing = true;
        }

        IsFilling = true;
        Note = "Type a value in the box of each credential you want to store, then review. A box left empty is skipped.";
        NoteKey = "Neutral";
        EntriesChanged();
    }

    /// <summary>Shows the exact commands, one <c>keys set</c> per value typed, with the values masked; they run once confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewFill))]
    private void ReviewFill() =>
        OpenReview(Rows.Where(r => r.IsEditing && r.HasEntry && r.CanReview).ToArray());

    /// <summary>Leaves Fill missing: the boxes close and nothing typed is kept.</summary>
    [RelayCommand]
    private void CancelFill()
    {
        ForgetEntries();
        Note = string.Empty;
        NoteKey = "Neutral";
    }

    // ------------------------------------------------------------------------------------------------------- set from the palette

    /// <summary>The longest the card waits for a read already in flight before it opens a variable's box (the read has its own limits; this only stops a wait for ever).</summary>
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The palette's <c>keys set NAME</c> (CUST-328): opens the box of the row for <paramref name="envName"/> - the same flow as pressing that row's Set - or, where
    /// the app cannot type values, the console window. The CLI's credential list is read first when there is none, so the row is the real one; a name it does not
    /// list gets a row of its own, marked as a variable the operator named. The installation's guard comes before anything opens.
    /// </summary>
    public async Task BeginSetForAsync(string envName)
    {
        if (!WizardCredentials.IsValidName(envName))
        {
            Note = "That is not the NAME of an environment variable, so no box was opened.";
            NoteKey = "Warn";
            return;
        }

        if (_services.Installation.ReasonFor(SecretPtyRunner.SetKeyArgv(envName)) is { } blocked)
        {
            Note = blocked;
            NoteKey = "Warn";
            return;
        }

        // A read in flight (the panel just came on screen) lands first: it would close a box opened before it.
        var waited = Stopwatch.StartNew();
        while (_reading && waited.Elapsed < ReadWait)
        {
            await Task.Delay(25).ConfigureAwait(true);
        }

        if (!HasLoaded && !_reading)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        if (!CanStartReview)
        {
            Note = "Finish or cancel the review that is open first, then ask again.";
            NoteKey = "Warn";
            return;
        }

        // Another variable's box (or Fill missing) is closed: one value at a time, from the palette.
        ForgetEntries();

        var row = Rows.FirstOrDefault(r => string.Equals(r.EnvName, envName, StringComparison.Ordinal));
        if (row is null)
        {
            row = new CredentialRowViewModel(NamedRow(envName), OnSetInTerminal, () => _services.Installation.BlockedReason, owner: this);
            Rows.Insert(0, row);
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(ShowEmpty));
        }

        if (!row.CanSet)
        {
            Note = ChangesBlockedReason ?? "That variable cannot be set from here.";
            NoteKey = "Warn";
            return;
        }

        // Where the app cannot type, the console route says what it opened itself.
        Note = InAppAvailable ? $"Type the value for {envName} in the box below, then review. Nothing runs until you confirm." : string.Empty;
        NoteKey = "Neutral";

        // Exactly what pressing Set on the row does: the masked box where the app can type, the console window where it cannot.
        row.SetCommand.Execute(null);
    }

    /// <summary>A row for a variable the CLI's list does not have: only what the app can honestly say (whether a value is there, from the same look the wizards take).</summary>
    private CredentialRow NamedRow(string envName)
    {
        var presence = new WizardCredentials(_services.Paths).Check(envName, fresh: true);
        var isSet = presence is CredentialPresence.InDotEnv or CredentialPresence.InEnvironment;
        return new CredentialRow(
            envName,
            envName,
            "Named in the palette",
            "optional",
            presence switch { CredentialPresence.InDotEnv => "dotenv", CredentialPresence.InEnvironment => "env", _ => "unset" },
            isSet,
            "A variable you named. The CLI keeps its value in ~/.defenseclaw/.env; it is not in the CLI's credential list, so nothing here says what reads it.");
    }

    // ------------------------------------------------------------------------------------------------------- set

    /// <summary>The Review button of a row: the exact command and the value masked, then the run once confirmed.</summary>
    internal void ReviewSet(CredentialRowViewModel row)
    {
        // The live installation first: the button is off for a read-only one, but a command can be invoked without the button.
        if (_services.Installation.ReasonFor(SecretPtyRunner.SetKeyArgv(row.EnvName)) is { } blocked)
        {
            Note = blocked;
            NoteKey = "Warn";
            return;
        }

        if (row.CanReview)
        {
            OpenReview(new[] { row });
        }
    }

    private void OpenReview(IReadOnlyList<CredentialRowViewModel> rows)
    {
        if (rows.Count == 0 || !CanStartReview)
        {
            return;
        }

        // Before the box, before the review: a managed or invalid installation is only read.
        if (_services.Installation.ReasonFor(SecretPtyRunner.SetKeyArgv(rows[0].EnvName)) is { } blocked)
        {
            Note = blocked;
            NoteKey = "Warn";
            return;
        }

        var outcomes = rows.Select(r => new SetOutcome(r)).ToArray();
        Review.Open(
            heading: outcomes.Length == 1 ? $"Store a value for {outcomes[0].Name}?" : $"Store {outcomes.Length.ToString(CultureInfo.CurrentCulture)} values?",
            explanation: Explain(outcomes),
            steps: outcomes.Select(StepFor).ToArray(),
            onFinished: result => FinishedAsync(outcomes, result),
            primaryText: outcomes.Length == 1 ? "Store value" : $"Store {outcomes.Length.ToString(CultureInfo.CurrentCulture)} values",
            extraWarnings: WarningsFor(outcomes));
    }

    /// <summary>What a review step knows about its run: the row it is for and, once it ran, what the runner said.</summary>
    private sealed class SetOutcome
    {
        public SetOutcome(CredentialRowViewModel row)
        {
            Row = row;
            Name = row.EnvName;
            Length = row.EntryLength;
        }

        public CredentialRowViewModel Row { get; }

        public string Name { get; }

        /// <summary>How many characters were typed; the only fact about the value the review and the notes ever state.</summary>
        public int Length { get; }

        public SecretPtyResult? Result { get; set; }
    }

    private DiscoverStep StepFor(SetOutcome outcome) => new(
        SecretPtyRunner.SetKeyArgv(outcome.Name),
        $"Stores the value you typed for {outcome.Name} ({outcome.Length.ToString(CultureInfo.CurrentCulture)} characters, hidden) in ~/.defenseclaw/.env.",
        CommandTier.StateChanging,
        Verify: _ => outcome.Result is { Succeeded: true } ? null : "The CLI ended without taking the value, so nothing was stored.",
        Run: () => RunSetAsync(outcome));

    /// <summary>
    /// The confirmed step: the value leaves the row for this one call and the box is emptied at once, so nothing but the runner holds it while
    /// the CLI is asked. A run that could not start at all still returns an entry (a refusal, in Activity) for the review to report.
    /// </summary>
    private async Task<CliInvocation> RunSetAsync(SetOutcome outcome)
    {
        var argv = SecretPtyRunner.SetKeyArgv(outcome.Name);
        var secret = outcome.Row.MaterializeSecret();
        outcome.Row.ClearEntry();
        if (secret is null)
        {
            return _services.Cli.RecordRefusal(CommandReview.DefaultExecutable, argv, "The value was cleared before it could be stored.");
        }

        var result = await SetKeyAsync(outcome.Name, secret, CancellationToken.None).ConfigureAwait(true);
        outcome.Result = result;
        return result.Invocation ?? _services.Cli.RecordRefusal(CommandReview.DefaultExecutable, argv, result.Message);
    }

    private Task<SecretPtyResult> SetKeyAsync(string name, SecretValue value, CancellationToken cancellationToken) =>
        RunSet is { } run ? run(name, value, cancellationToken) : new SecretPtyRunner(_services.Cli).SetKeyAsync(name, value, cancellationToken);

    /// <summary>The review's explanation: the value as a mask and a count - never a character of it - and where it goes.</summary>
    private static string Explain(IReadOnlyList<SetOutcome> outcomes)
    {
        var text = new StringBuilder();
        if (outcomes.Count == 1)
        {
            text.Append("Value: ").Append(MaskedValue).Append("  (").Append(outcomes[0].Length.ToString(CultureInfo.CurrentCulture)).Append(" characters, hidden)");
        }
        else
        {
            text.Append("Values (hidden):");
            foreach (var outcome in outcomes)
            {
                text.AppendLine().Append("  ").Append(outcome.Name).Append("  ").Append(MaskedValue)
                    .Append("  (").Append(outcome.Length.ToString(CultureInfo.CurrentCulture)).Append(" characters)");
            }
        }

        text.AppendLine().AppendLine().Append(outcomes.Count == 1
            ? "This app types it at the CLI's own hidden prompt, in a pseudo-console it starts for the command below. It is not on the command line, " +
              "it is not recorded in Activity and it is not kept after the run. The CLI stores it in ~/.defenseclaw/.env."
            : "This app types each one at the CLI's own hidden prompt, in a pseudo-console it starts for each command below. None is on a command line, " +
              "recorded in Activity or kept after the run. The CLI stores each in ~/.defenseclaw/.env.");
        return text.ToString();
    }

    private static IReadOnlyList<CommandReviewWarning> WarningsFor(IReadOnlyList<SetOutcome> outcomes)
    {
        var warnings = new List<CommandReviewWarning>();

        var replacing = outcomes.Where(o => o.Row.Row is { IsSet: true } row && string.Equals(row.Source, "dotenv", StringComparison.OrdinalIgnoreCase)).Select(o => o.Name).ToArray();
        if (replacing.Length > 0)
        {
            warnings.Add(new CommandReviewWarning(
                "Replaces a stored value",
                $"{string.Join(", ", replacing)} {(replacing.Length == 1 ? "already has" : "already have")} a value in ~/.defenseclaw/.env. This replaces {(replacing.Length == 1 ? "it" : "them")}."));
        }

        var shadowed = outcomes.Where(o => o.Row.Row is { IsSet: true } row && string.Equals(row.Source, "env", StringComparison.OrdinalIgnoreCase)).Select(o => o.Name).ToArray();
        if (shadowed.Length > 0)
        {
            warnings.Add(new CommandReviewWarning(
                "The environment wins",
                $"{string.Join(", ", shadowed)} {(shadowed.Length == 1 ? "is" : "are")} set in this app's environment, which the CLI reads before ~/.defenseclaw/.env. " +
                "Storing a value there will not change what the CLI uses until the variable is removed from the environment."));
        }

        return warnings;
    }

    /// <summary>
    /// After the review ran: the typed values are gone (the rows emptied them as each step started); a route that did not work with nothing typed
    /// becomes the console window; everything else is the CLI's answer, said in a sentence, and the list is read again so the rows show it.
    /// </summary>
    private async Task FinishedAsync(IReadOnlyList<SetOutcome> outcomes, DiscoverReviewResult review)
    {
        _ = review;

        // Whatever happened, nothing typed is kept: a step that was skipped after an earlier one failed still holds its value until here.
        ForgetEntries();

        var ran = outcomes.Where(o => o.Result is not null).ToArray();
        if (ran.FirstOrDefault(o => o.Result!.RouteFailed) is { } broken)
        {
            // The pseudo-console route does not work here. Remember it, and let the console do what was asked.
            _routeFailed = true;
            RaiseRoute();

            var argv = outcomes.Count == 1 ? SecretPtyRunner.SetKeyArgv(broken.Name) : FillMissingArgv;
            await OpenInTerminalAsync(argv).ConfigureAwait(true);
            if (_awaitingReturn)
            {
                Note = $"The app could not type the value itself ({broken.Result!.Message.TrimEnd('.')}), so a console window was opened instead. " +
                       "Type the value there; this list refreshes when you come back to this window.";
                NoteKey = "Warn";
            }

            return;
        }

        if (ran.Length == 0)
        {
            // Not one of them reached the CLI (it was not found): the review says why, and there is nothing to read again.
            Note = "Nothing was stored: the command could not be started (the review shows why).";
            NoteKey = "Bad";
            return;
        }

        await RefreshAsync().ConfigureAwait(true);

        var stored = ran.Where(o => o.Result!.Succeeded).ToArray();
        var failed = ran.FirstOrDefault(o => !o.Result!.Succeeded);
        var untried = outcomes.Count - ran.Length;

        if (failed is not null)
        {
            Note = $"{(stored.Length > 0 ? $"Stored {stored.Length.ToString(CultureInfo.CurrentCulture)} of {outcomes.Count.ToString(CultureInfo.CurrentCulture)}. " : string.Empty)}" +
                   $"{failed.Name} was not stored: {failed.Result!.Message}" +
                   $"{(untried > 0 ? $" {untried.ToString(CultureInfo.CurrentCulture)} more {(untried == 1 ? "was" : "were")} not tried." : string.Empty)} " +
                   "The command and its output are in the Activity panel.";
            NoteKey = "Bad";
            return;
        }

        // The CLI said yes; the list it was just asked for says whether it can see the values.
        var unseen = stored.Where(o => Rows.FirstOrDefault(r => r.EnvName == o.Name) is { Row.IsSet: false }).Select(o => o.Name).ToArray();
        if (unseen.Length > 0)
        {
            Note = $"The CLI finished, but the list still shows {string.Join(", ", unseen)} as unset. Refresh in a moment, or check ~/.defenseclaw/.env.";
            NoteKey = "Warn";
            return;
        }

        Note = stored.Length == 1
            ? $"Stored {stored[0].Name}. The value is shown nowhere; the row now reads set."
            : $"Stored {stored.Length.ToString(CultureInfo.CurrentCulture)} values: {string.Join(", ", stored.Select(o => o.Name))}. They are shown nowhere; the rows now read set.";
        NoteKey = "Ok";
    }
}
