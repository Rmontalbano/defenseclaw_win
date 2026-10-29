using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What the Setup dialogs of CUST-271 share - the notification routing dialog on the Setup hub and the AI Discovery tuning dialog on its panel:
/// a form seeded from config.yaml, a review that turns the form into reviewed commands, and the same ladder of reasons a control is off.
/// <para>
/// <b>One reason per control, in this order</b> (<see cref="ChangesBlockedReason"/> is the first three; each dialog adds its own after them):
/// the installation (CUST-308: managed or invalid, so every control that changes anything is off and the runner would refuse it anyway), a
/// config.yaml that cannot be read (what the form starts from is unknown), and settings read before config.yaml or .env changed (CUST-312:
/// <see cref="CatalogTrust"/>; also checked at the moment a review is confirmed, where a refusal is recorded in Activity).
/// </para>
/// <para>
/// <b>Flags are checked, never assumed.</b> Each dialog reads the <c>--help</c> of the command it drives (<see cref="CheckHelpAsync"/>, through
/// the hub catalog's cached probe: no process of its own once the catalog has warmed, and no Activity entry) and offers only what that help
/// lists. A help that cannot be read leaves the plan off with the reason.
/// </para>
/// <para>
/// Not a panel: the panel that hosts the dialog owns it, calls <see cref="Open"/>, and <see cref="Dispose"/>s it. Its subscriptions (the installation,
/// config.yaml) exist only while it is open.
/// </para>
/// </summary>
public abstract partial class SetupDialogViewModel : ObservableObject, IDisposable
{
    private int _helpGeneration;
    private bool _subscribed;

    /// <param name="services">The composition the dialog works in.</param>
    /// <param name="readClause">What the trust says was read ("these settings were read").</param>
    protected SetupDialogViewModel(AppServices services, string readClause = "these settings were read")
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Trust = CatalogTrust.Watching(services.Paths, readClause);
        Review = new DiscoverActionReview(services) { RunGuard = ReasonToRefuseRun };
        Review.PropertyChanged += OnReviewChanged;
    }

    protected AppServices Services { get; }

    /// <summary>True once <see cref="Dispose"/> ran.</summary>
    protected bool IsDisposed { get; private set; }

    /// <summary>The confirm-and-run overlay the dialog's plan goes through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>Whether the settings on screen may authorize a change; see <see cref="CatalogTrust"/>.</summary>
    public CatalogTrust Trust { get; }

    // ------------------------------------------------------------------ open and close

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// Opens the dialog on what config.yaml says now: the file is read again (the form must start from the file, not from the copy the watcher
    /// last saw), the CLI's help is checked, and the result of an earlier visit is forgotten. Does nothing while it is open.
    /// </summary>
    public void Open()
    {
        if (IsOpen || IsDisposed)
        {
            return;
        }

        ResultText = string.Empty;
        Services.ReloadConfig();
        Subscribe();
        Seed(keepEdits: false);
        IsOpen = true;
        _ = CheckHelpAsync();
    }

    /// <summary>Closes the dialog. Does nothing while a command is running (there is no way to stop one from here); with a review open, it closes that first.</summary>
    [RelayCommand]
    private void Close()
    {
        if (Review.IsRunning)
        {
            return;
        }

        if (Review.IsOpen)
        {
            _ = Review.HandleEscape();
            return;
        }

        IsOpen = false;
        Unsubscribe();
    }

    /// <summary>Esc: the review first, then the dialog. True when the key was used.</summary>
    public bool HandleEscape()
    {
        if (!IsOpen)
        {
            return false;
        }

        if (Review.IsOpen)
        {
            return Review.HandleEscape();
        }

        Close();
        return true;
    }

    /// <summary>Reads config.yaml and the CLI's help again and starts the form from what the file says: what "Refresh" means.</summary>
    [RelayCommand]
    private void Refresh()
    {
        if (Review.IsOpen || Review.IsRunning)
        {
            return;
        }

        Services.ReloadConfig();
        Seed(keepEdits: false);
        _ = CheckHelpAsync();
    }

    /// <summary>
    /// Starts the form from config.yaml as the app holds it (and notes the file's signature: <see cref="BeginTrustRead"/>).
    /// <paramref name="keepEdits"/> keeps the operator's unsaved choices, measured against the file as it is now.
    /// </summary>
    protected abstract void Seed(bool keepEdits);

    /// <summary>The dialog's own buttons and tips are drawn again: something they depend on changed.</summary>
    protected abstract void NotifyActions();

    /// <summary>Reads the help screen of the command the dialog drives.</summary>
    protected abstract Task<HelpProbeResult> ReadHelpAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes in the help screen that was read. Returns why it cannot be used (it lists nothing the dialog needs), or null when it can; the
    /// dialog keeps what it learned (which flags are listed) in its own fields.
    /// </summary>
    protected abstract string? ApplyHelp(HelpProbeResult help);

    /// <summary>The help has been checked (or failed to be): the dialog's controls follow what it listed.</summary>
    protected virtual void HelpChecked()
    {
    }

    protected virtual void OnDisposing()
    {
    }

    // ------------------------------------------------------------------ the installation and config.yaml

    /// <summary>Why every control that changes something is off because the installation is managed or invalid; null while it may be changed.</summary>
    public string? InstallationBlockedReason => Services.Installation.BlockedReason;

    public bool HasInstallationBlock => InstallationBlockedReason is not null;

    /// <summary>config.yaml exists but could not be parsed, so what the form starts from is unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigProblem), nameof(CanEdit))]
    private string _configProblem = string.Empty;

    public bool HasConfigProblem => ConfigProblem.Length > 0;

    /// <summary>The form can be edited: the installation may be changed and config.yaml could be read.</summary>
    public bool CanEdit => InstallationBlockedReason is null && !HasConfigProblem;

    /// <summary>
    /// Why a change is off for a reason that is not about the form: the installation, then the unreadable file, then the settings' age or a
    /// change to config.yaml since they were read (a refresh cures that). Null when none applies.
    /// </summary>
    protected string? ChangesBlockedReason => InstallationBlockedReason ?? (HasConfigProblem ? ConfigProblem : null) ?? Trust.Reason;

    /// <summary>The settings on screen were read before config.yaml or .env changed (or are old): the form stays, changes are off.</summary>
    public string? StaleReason => HasConfigProblem ? null : Trust.Reason;

    public bool IsStale => StaleReason is not null;

    /// <summary>
    /// Notes the files as they are now as the ones the form was read under (CUST-312), after the dialog read config.yaml: a change to either
    /// after this makes the dialog stale. An unreadable file is a failed read.
    /// </summary>
    protected void BeginTrustRead()
    {
        ConfigProblem = Services.ConfigLoadError is { Length: > 0 } error
            ? "config.yaml could not be read (" + error + "), so what these settings are now is unknown. Fix the file (Setup, Config editor) and press Refresh."
            : string.Empty;

        Trust.BeginRead();
        if (HasConfigProblem)
        {
            Trust.MarkFailed("config.yaml could not be read");
        }
        else
        {
            Trust.MarkComplete();
        }
    }

    /// <summary>
    /// The last question before a confirmed review runs: why it must not, or null. Asked at Confirm because a review stays open as long as it is
    /// read: the installation (which the review itself also asks), then whether config.yaml or .env changed after the settings were read (the look
    /// is at the files, not at what the watcher has noticed). When it answers nothing runs and Activity records the refusal.
    /// </summary>
    private string? ReasonToRefuseRun()
    {
        _ = Trust.CheckConfig();
        var reason = Services.Installation.BlockedReason ?? Trust.Reason;
        if (reason is not null)
        {
            NotifyActions();
        }

        return reason;
    }

    private void OnInstallationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(InstallationBlockedReason));
        OnPropertyChanged(nameof(HasInstallationBlock));
        OnPropertyChanged(nameof(CanEdit));
        NotifyActions();
    }

    private void OnConfigReloaded(object? sender, EventArgs e)
    {
        if (Trust.CheckConfig())
        {
            NotifyActions();
        }
    }

    private void OnReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscoverActionReview.IsOpen) or nameof(DiscoverActionReview.IsRunning))
        {
            NotifyActions();
        }
    }

    /// <summary>The properties every dialog's buttons depend on, drawn again; a dialog's <see cref="NotifyActions"/> calls this and adds its own.</summary>
    protected void RaiseGuardState()
    {
        OnPropertyChanged(nameof(StaleReason));
        OnPropertyChanged(nameof(IsStale));
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Services.Installation.Changed += OnInstallationChanged;
        Services.ConfigReloaded += OnConfigReloaded;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        Services.Installation.Changed -= OnInstallationChanged;
        Services.ConfigReloaded -= OnConfigReloaded;
    }

    // ------------------------------------------------------------------ the CLI's help

    /// <summary>The installed CLI's help is being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHelpReady))]
    private bool _isChecking;

    /// <summary>Why the help could not be read or did not list what the dialog needs; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHelpProblem), nameof(IsHelpReady))]
    private string _helpProblem = string.Empty;

    public bool HasHelpProblem => HelpProblem.Length > 0;

    /// <summary>True once a help screen has been read and used: the plan can be built.</summary>
    public bool IsHelpReady => !IsChecking && !HasHelpProblem && _helpUsed;

    private bool _helpUsed;

    /// <summary>
    /// Reads the help of the command the dialog drives and checks it. A screen that cannot be read, or that lists nothing the dialog needs,
    /// leaves the plan off with the reason: the flags are checked, never assumed.
    /// </summary>
    protected async Task CheckHelpAsync()
    {
        var generation = ++_helpGeneration;
        IsChecking = true;
        HelpProblem = string.Empty;
        _helpUsed = false;
        NotifyActions();

        HelpProbeResult result;
        try
        {
            result = await ReadHelpAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new HelpProbeResult(string.Empty, ex.Message);
        }

        if (IsDisposed || generation != _helpGeneration)
        {
            return;
        }

        IsChecking = false;
        var problem = ApplyHelp(result);
        HelpProblem = problem ?? string.Empty;
        _helpUsed = problem is null;
        OnPropertyChanged(nameof(IsHelpReady));
        HelpChecked();
        NotifyActions();
    }

    // ------------------------------------------------------------------ the result of the last run

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _resultKey = "Neutral";

    public bool HasResult => ResultText.Length > 0;

    [RelayCommand]
    private void DismissResult() => ResultText = string.Empty;

    // ------------------------------------------------------------------ lifetime

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _helpGeneration++;
        Unsubscribe();
        Review.PropertyChanged -= OnReviewChanged;
        OnDisposing();
    }
}
