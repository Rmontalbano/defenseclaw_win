using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The in-app half of a credential row (CUST-221): the masked box under the row, what is held while the operator types, and the route the row's
/// Set button takes (the box where the app can type the value itself, a console window where it cannot).
/// <para>
/// <b>What is held, and for how long.</b> The password box reports each change by handing over its own <see cref="SecureString"/> copy
/// (<c>CredentialEntryBox</c>), as the wizards' secret fields do (<see cref="SecretEntry"/>), so no string property of this view-model ever holds the
/// value. It is turned into a <see cref="SecretValue"/> at the moment the confirmed run needs it (<see cref="MaterializeSecret"/>) and dropped
/// right after, with the box emptied. Nothing is held for a row that is not being edited, and every other way out - Cancel, the list being read
/// again, the installation turning read-only, leaving the page - drops it too (<see cref="ClearEntry"/>). A value the CLI would refuse (a line break
/// in it, which would put a second variable into <c>.env</c>) or that is not a credential at all (longer than <see cref="MaxValueLength"/>) is not held.
/// </para>
/// </summary>
public sealed partial class CredentialRowViewModel
{
    /// <summary>The longest value taken: far beyond any API key, token or service secret, and a bound on what is typed into a console one key at a time.</summary>
    internal const int MaxValueLength = 8192;

    private SecureString? _entry;
    private int _entryLength;

    /// <summary>The masked box under the row is open (the operator pressed Set, or Fill missing opened it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditor))]
    private bool _isEditing;

    /// <summary>A usable value is held.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryStatus))]
    [NotifyPropertyChangedFor(nameof(CanReview))]
    [NotifyPropertyChangedFor(nameof(ReviewToolTip))]
    private bool _hasEntry;

    /// <summary>Why what was typed cannot be used; empty when nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntryProblem))]
    [NotifyPropertyChangedFor(nameof(EntryStatus))]
    private string _entryProblem = string.Empty;

    /// <summary>The primary button: opens the masked box, or a console window where the app cannot type the value itself.</summary>
    public IRelayCommand SetCommand { get; }

    /// <summary>Shows the exact command, with the value masked, and runs it once confirmed.</summary>
    public IRelayCommand ReviewCommand { get; }

    /// <summary>Closes the box and forgets what was typed.</summary>
    public IRelayCommand CancelEntryCommand { get; }

    /// <summary>Raised when a held value was dropped, so the password box can empty itself and never claims a value the app no longer holds.</summary>
    public event EventHandler? EntryCleared;

    /// <summary>The app can type this row's value itself: the card has a pseudo-console to do it in and the name is one the CLI can be given.</summary>
    public bool OffersInApp => _owner is { InAppAvailable: true } && WizardCredentials.IsValidName(Row.EnvName);

    /// <summary>The masked box is on screen.</summary>
    public bool ShowEditor => IsEditing && OffersInApp;

    /// <summary>The small console button beside Set: drawn only where Set itself types the value in the app (otherwise Set <i>is</i> the console).</summary>
    public bool ShowTerminalButton => OffersInApp;

    /// <summary>What the Set button does, or why it cannot (a read-only installation).</summary>
    public string SetButtonToolTip => _installationBlockedReason?.Invoke() ??
        (OffersInApp
            ? "Type a value into a masked box here. You review the exact command first; the app then hands the value to the CLI's own hidden prompt."
            : SetToolTip);

    public string SetButtonAutomationName => OffersInApp ? $"Set {Row.EnvName}" : SetAutomationName;

    /// <summary>What a screen reader calls the masked box.</summary>
    public string EntryAutomationName => $"Value for {Row.EnvName}";

    public string ReviewAutomationName => $"Review the command that stores {Row.EnvName}";

    public string CancelAutomationName => $"Cancel entering a value for {Row.EnvName}";

    public bool HasEntryProblem => EntryProblem.Length > 0;

    /// <summary>The characters held, after the whitespace a paste carries at either end is dropped; 0 when nothing is.</summary>
    internal int EntryLength => _entryLength;

    /// <summary>The line under the box: what is held (a count, never a character) or how to enter it.</summary>
    public string EntryStatus => HasEntryProblem
        ? EntryProblem
        : HasEntry
            ? $"A value is entered ({_entryLength} characters, hidden). Nothing runs until you review the command and confirm."
            : "Type or paste the value. It is hidden as you type, and it never appears on a command line or in Activity.";

    /// <summary>Review is live: a value is held, the installation may be changed and nothing else is being stored.</summary>
    public bool CanReview => HasEntry && CanSet && (_owner?.CanStartReview ?? false);

    /// <summary>What the Review button does, or why it cannot.</summary>
    public string ReviewToolTip => _installationBlockedReason?.Invoke() ??
        (HasEntry ? "Show the exact command with the value masked. Nothing runs until you confirm." : "Type a value first.");

    private void OnSet()
    {
        if (!CanSet)
        {
            return;
        }

        if (OffersInApp)
        {
            IsEditing = true;
            return;
        }

        _setInTerminal?.Invoke(this);
    }

    private void CancelEntry()
    {
        ClearEntry();
        IsEditing = false;
        _owner?.EntriesChanged();
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (!value)
        {
            ClearEntry();
        }

        _owner?.EntriesChanged();
    }

    /// <summary>
    /// The password box reports what is typed. <paramref name="value"/> must be a copy the caller hands over
    /// (<c>PasswordBox.SecurePassword</c> is one); this takes ownership and disposes the previous entry.
    /// </summary>
    internal void SetEntry(SecureString? value)
    {
        var shape = SecretEntry.Inspect(value);
        var tooLong = shape.TrimmedLength > MaxValueLength;
        var usable = !shape.IsEmpty && !shape.HasControlCharacter && !tooLong;

        // A value the CLI would refuse is not held: better an empty state and a sentence than a secret kept for nothing.
        var previous = _entry;
        _entry = usable ? value : null;
        _entryLength = usable ? shape.TrimmedLength : 0;
        if (!usable)
        {
            value?.Dispose();
        }

        previous?.Dispose();

        EntryProblem = shape.HasControlCharacter
            ? "That value has a line break or another control character in it, which the CLI refuses. Paste it again."
            : tooLong
                ? $"That value is longer than {MaxValueLength:N0} characters, which is not a credential. Paste it again."
                : string.Empty;

        HasEntry = usable;
        RefreshEntryState();
        _owner?.EntriesChanged();
    }

    /// <summary>
    /// The held value as a <see cref="SecretValue"/> for the run that is about to start, or null when there is nothing usable. The caller hands it
    /// straight to the runner and then calls <see cref="ClearEntry"/>; nothing here keeps it.
    /// </summary>
    internal SecretValue? MaterializeSecret() => SecretEntry.ToSecret(_entry);

    /// <summary>Drops the held value and tells the password box to empty itself.</summary>
    internal void ClearEntry()
    {
        var held = _entry is not null || EntryProblem.Length > 0;

        _entry?.Dispose();
        _entry = null;
        _entryLength = 0;
        EntryProblem = string.Empty;
        HasEntry = false;

        if (held)
        {
            EntryCleared?.Invoke(this, EventArgs.Empty);
        }

        RefreshEntryState();
        _owner?.EntriesChanged();
    }

    /// <summary>The state that depends on the card (a review is open, the installation changed): the buttons are drawn again.</summary>
    internal void RefreshEntryState()
    {
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(ReviewToolTip));
        OnPropertyChanged(nameof(SetButtonToolTip));
        OnPropertyChanged(nameof(EntryStatus));
        SetCommand.NotifyCanExecuteChanged();
        ReviewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The card learned whether the app can type values (or that it cannot): which route Set takes and what is drawn changes.</summary>
    internal void RefreshRoute()
    {
        // A box for a route that is gone does not stay open holding a value nothing can use.
        if (IsEditing && !OffersInApp)
        {
            IsEditing = false;
        }

        OnPropertyChanged(nameof(OffersInApp));
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowTerminalButton));
        OnPropertyChanged(nameof(SetButtonToolTip));
        OnPropertyChanged(nameof(SetButtonAutomationName));
    }
}
