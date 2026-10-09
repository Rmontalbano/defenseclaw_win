using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>One credential row as the card shows it: names and states only, never a value.</summary>
public sealed class CredentialRowViewModel : ObservableObject
{
    private readonly Action<CredentialRowViewModel>? _setInTerminal;
    private readonly Func<string?>? _installationBlockedReason;

    /// <param name="row">What <c>keys list</c> said about one variable.</param>
    /// <param name="setInTerminal">What the Set button does.</param>
    /// <param name="installationBlockedReason">Why nothing may be changed on this installation right now (managed or invalid); null, or one that answers null, means it may.</param>
    internal CredentialRowViewModel(CredentialRow row, Action<CredentialRowViewModel>? setInTerminal, Func<string?>? installationBlockedReason = null)
    {
        Row = row;
        _setInTerminal = setInTerminal;
        _installationBlockedReason = installationBlockedReason;
        SetInTerminalCommand = new RelayCommand(() => _setInTerminal?.Invoke(this), () => CanSet);
    }

    /// <summary>The installation turned read-only (or writable): the Set button and its tooltip are drawn again.</summary>
    internal void RefreshInstallation()
    {
        OnPropertyChanged(nameof(CanSet));
        OnPropertyChanged(nameof(SetToolTip));
        SetInTerminalCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The Set button's tooltip: what it opens, or why it cannot (a read-only installation).</summary>
    public string SetToolTip => _installationBlockedReason?.Invoke() ??
        $"Opens a console window running {SetCommandText}; the value is typed there, never here";

    internal CredentialRow Row { get; }

    public string EnvName => Row.EnvName;

    public string Feature => Row.Feature;

    public string Description => Row.Description;

    /// <summary><c>required</c>, <c>optional</c> or <c>not used</c>.</summary>
    public string Requirement => Row.Requirement.Replace('_', ' ');

    /// <summary><c>env</c> (process environment), <c>dotenv</c> (~/.defenseclaw/.env) or <c>unset</c>, as <c>keys list</c> prints it.</summary>
    public string Source => Row.IsSet && Row.Source.Length > 0 ? Row.Source : "unset";

    /// <summary>The TUI's last column: <c>✓ set</c>, <c>MISSING</c> (required), <c>unset</c> (optional) or <c>n/a</c> (not used by this config).</summary>
    public string SetText => Row.IsSet
        ? "✓ set"
        : Row.Requirement switch
        {
            "required" => "MISSING",
            "optional" => "unset",
            _ => "n/a",
        };

    /// <summary>Tone key of the state badge: Ok when set, Bad when a required key is missing, Neutral otherwise.</summary>
    public string SetKey => Row.IsSet ? "Ok" : Row.IsMissingRequired ? "Bad" : "Neutral";

    public bool IsMissingRequired => Row.IsMissingRequired;

    /// <summary>The exact command the Set button runs in a console; never carries a value.</summary>
    public string SetCommandText => WizardCredentials.KeysSetCommand(Row.EnvName);

    /// <summary>A name the CLI and a console command line can carry (a row with anything else cannot be set from here), on an installation that may be changed.</summary>
    public bool CanSet => WizardCredentials.IsValidName(Row.EnvName) && _installationBlockedReason?.Invoke() is null;

    public string SetAutomationName => $"Set {Row.EnvName} in a terminal";

    public string RowAutomationName =>
        $"{Row.EnvName}, {Row.Feature}, {Requirement}, source {Source}, {SetText}";

    public IRelayCommand SetInTerminalCommand { get; }
}

/// <summary>
/// The Setup panel's Credentials card (CUST-266): <c>defenseclaw keys list --json</c> as a table (env / feature / requirement / source /
/// set), the missing-required count, <c>keys check</c>, and the terminal route for <c>keys set</c> and <c>keys fill-missing</c>.
/// <para>
/// <b>Reads only names.</b> <c>keys list --json</c> prints a variable's name and whether it is set; a value appears only under
/// <c>--show-values</c>, which this never passes (0.8.10 <c>cmd_keys.py</c>), and <see cref="CredentialRow"/> has nowhere to hold one. Both
/// reads are read-only commands the shell's tier table allows without a review; both land in Activity like any run.
/// </para>
/// <para>
/// <b>Writes never run here.</b> <c>keys set</c> and <c>keys fill-missing</c> prompt with a hidden console prompt that reads the console,
/// not stdin, so there is no headless route until CUST-221's pseudo-console. <see cref="OpenFillMissingInTerminalCommand"/> and each row's
/// Set button open a console window running the exact command (<see cref="CredentialTerminal"/>) and write an Activity entry for the
/// hand-off; the list is read again when the operator comes back (<see cref="NotifyReturned"/>) or presses Refresh.
/// </para>
/// <para>
/// <b>When it reads.</b> Never on a timer: when the Setup panel activates and has nothing (or something stale), on Refresh, after a console
/// was opened and the operator returns, and after <c>keys check</c>.
/// </para>
/// </summary>
public sealed partial class CredentialsViewModel : ObservableObject
{
    /// <summary>How long a read is trusted when the panel is re-entered.</summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    /// <summary>The argv of the read; the only flag is <c>--json</c> (never <c>--show-values</c>).</summary>
    internal static readonly string[] ListArgv = { "keys", "list", "--json" };

    internal static readonly string[] CheckArgv = { "keys", "check" };

    internal static readonly string[] FillMissingArgv = { "keys", "fill-missing", "--yes" };

    private readonly AppServices _services;
    private readonly CredentialTerminal _terminal;
    private MonotonicStamp _loadedAt = MonotonicStamp.Never;
    private bool _awaitingReturn;
    private bool _reading;

    public CredentialsViewModel(AppServices services, CredentialTerminal? terminal = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _terminal = terminal ?? new CredentialTerminal(services.Paths);
    }

    /// <summary>How the two reads run; a test replaces it so no process starts. Only argv that the classifier calls read-only is ever passed.</summary>
    internal Func<IReadOnlyList<string>, Task<CliInvocation>>? RunRead { get; set; }

    private Task<CliInvocation> Read(IReadOnlyList<string> argv)
    {
        if (CommandTiers.Classify(argv) != CommandTier.ReadOnly)
        {
            throw new InvalidOperationException("The credential reads are read-only commands; anything else needs a review.");
        }

        return RunRead is { } run ? run(argv) : DiscoverCli.RunReadOnlyAsync(_services, argv, CancellationToken.None);
    }

    /// <summary>Raised on the UI thread after a read landed (rows, error or both changed): the readiness checklist rebuilds.</summary>
    public event EventHandler? Loaded;

    public ObservableCollection<CredentialRowViewModel> Rows { get; } = new();

    /// <summary>The rows of the last good read; empty until one has landed.</summary>
    public IReadOnlyList<CredentialRow> Snapshot { get; private set; } = Array.Empty<CredentialRow>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotLoaded))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotLoaded))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowNotLoaded))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _asOf = string.Empty;

    /// <summary>Required by the current config and not set; the same count as the TUI's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingRequired))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _missingRequiredCount;

    /// <summary>The outcome of <c>keys check</c> or of opening a console; empty hides the line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = string.Empty;

    /// <summary>Tone key of <see cref="Note"/>: Ok / Warn / Bad / Neutral.</summary>
    [ObservableProperty]
    private string _noteKey = "Neutral";

    [ObservableProperty]
    private bool _isChecking;

    public bool HasError => Error.Length > 0;

    public bool HasMissingRequired => MissingRequiredCount > 0;

    public bool HasNote => Note.Length > 0;

    public bool HasRows => Rows.Count > 0;

    /// <summary>Nothing has been read and nothing went wrong: "not loaded", as opposed to an error or an empty list.</summary>
    public bool ShowNotLoaded => !HasLoaded && !IsLoading && !HasError;

    /// <summary>A read succeeded and the CLI listed no credential.</summary>
    public bool ShowEmpty => HasLoaded && !HasError && Rows.Count == 0;

    public string SummaryText => !HasLoaded
        ? string.Empty
        : MissingRequiredCount == 0
            ? "All required credentials are set."
            : MissingRequiredCount == 1
                ? "1 required credential is missing."
                : $"{MissingRequiredCount.ToString(CultureInfo.CurrentCulture)} required credentials are missing.";

    /// <summary>The command the "fill missing" button runs in a console, for its tooltip.</summary>
    public static string FillMissingCommandText => "defenseclaw " + string.Join(' ', FillMissingArgv);

    /// <summary>True when there is nothing, or nothing recent: the panel reads on activation only then.</summary>
    public bool IsStale => _loadedAt.IsNever || _loadedAt.Elapsed() > FreshFor;

    /// <summary>
    /// <c>keys list --json</c>, once at a time. A failure (not found, non-zero exit, unreadable JSON) is shown in the card and keeps the last
    /// good rows: a credential list that blanks on a transient error would read as "nothing is missing".
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_reading)
        {
            return;
        }

        _reading = true;
        IsLoading = true;
        try
        {
            var invocation = await Read(ListArgv).ConfigureAwait(true);
            Apply(invocation);
        }
        catch (CliNotFoundException ex)
        {
            Fail("defenseclaw was not found, so the credential list cannot be read. " + ex.Message);
        }
        finally
        {
            _reading = false;
            IsLoading = false;
            Loaded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Apply(CliInvocation invocation)
    {
        if (invocation.FailureReason is { Length: > 0 } reason)
        {
            Fail("keys list did not finish: " + reason + ".");
            return;
        }

        var stdout = DiscoverCli.Stdout(invocation);
        if (invocation.ExitCode is not 0)
        {
            var stderr = DiscoverCli.Stderr(invocation).Trim();
            Fail($"keys list exited {(invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "unknown")}. {(stderr.Length > 0 ? stderr : stdout.Trim())}".Trim());
            return;
        }

        if (!CredentialListParser.TryParse(stdout, out var rows, out var problem))
        {
            Fail("keys list printed something that is not a credential list: " + problem);
            return;
        }

        ApplyRows(rows);
    }

    /// <summary>Shows <paramref name="rows"/> as the card's content (a read landed, or a test supplies one).</summary>
    internal void ApplyRows(IReadOnlyList<CredentialRow> rows)
    {
        Snapshot = rows;
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(new CredentialRowViewModel(row, OnSetInTerminal, () => _services.Installation.BlockedReason));
        }

        MissingRequiredCount = rows.Count(r => r.IsMissingRequired);
        Error = string.Empty;
        HasLoaded = true;
        _loadedAt = MonotonicStamp.Now();
        AsOf = "as of " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void Fail(string message)
    {
        Error = message;
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>
    /// <c>keys check</c>: exits 0 when every required key is set and 1 when not, printing which. A read, on the shell's allow-list,
    /// so it runs without a review; the answer is shown in the card and the full output is in Activity.
    /// </summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var invocation = await Read(CheckArgv).ConfigureAwait(true);
            var lines = invocation.OutputLines
                .Select(l => l.Text.Trim())
                .Where(t => t.Length > 0)
                .TakeLast(12)
                .ToArray();
            var text = string.Join(" ", lines);

            (Note, NoteKey) = (invocation.FailureReason, invocation.ExitCode) switch
            {
                ({ Length: > 0 } reason, _) => ("keys check did not finish: " + reason + ".", "Warn"),
                (_, 0) => (text.Length > 0 ? text : "All required credentials are set.", "Ok"),
                (_, 1) => (text.Length > 0 ? text : "Some required credentials are missing.", "Warn"),
                (_, { } code) => ($"keys check exited {code.ToString(CultureInfo.CurrentCulture)}. {text}".Trim(), "Bad"),
                _ => ("keys check ended without an exit code.", "Neutral"),
            };

            // The check read the same state the list did: bring the table in line with what it just said.
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (CliNotFoundException ex)
        {
            Note = "defenseclaw was not found. " + ex.Message;
            NoteKey = "Bad";
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Why the buttons that open a console to write a credential are off (the installation is managed or invalid); null while they are on.</summary>
    public string? ChangesBlockedReason => _services.Installation.BlockedReason;

    /// <summary>The installation may be changed, so a credential may be written.</summary>
    public bool CanChange => ChangesBlockedReason is null;

    /// <summary>The installation turned read-only (or writable): every Set button, and Fill missing, are drawn again.</summary>
    public void RefreshInstallation()
    {
        OnPropertyChanged(nameof(ChangesBlockedReason));
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(FillMissingToolTip));
        foreach (var row in Rows)
        {
            row.RefreshInstallation();
        }
    }

    /// <summary>Fill missing's tooltip: what it opens, or why it cannot.</summary>
    public string FillMissingToolTip => ChangesBlockedReason ??
        "Opens a console window running defenseclaw keys fill-missing --yes: a hidden prompt for each required key that is unset";

    /// <summary>Opens a console running <c>defenseclaw keys fill-missing --yes</c>: a hidden prompt for each required key that is unset.</summary>
    [RelayCommand]
    public Task OpenFillMissingInTerminalAsync() => OpenInTerminalAsync(FillMissingArgv);

    private void OnSetInTerminal(CredentialRowViewModel row)
    {
        if (row.CanSet)
        {
            _ = OpenInTerminalAsync(new[] { "keys", "set", row.EnvName });
        }
    }

    /// <summary>Opens <c>defenseclaw <paramref name="argv"/></c> in a console, records the hand-off in Activity and arms the re-read.</summary>
    internal async Task OpenInTerminalAsync(IReadOnlyList<string> argv)
    {
        // A console window the app opens is a run the runner never sees, so the read-only rule is asked here too: keys set and fill-missing write.
        if (_services.Installation.ReasonFor(argv) is { } blocked)
        {
            Note = blocked;
            NoteKey = "Warn";
            return;
        }

        var result = await _terminal.OpenAsync(argv).ConfigureAwait(true);
        if (!result.Started)
        {
            Note = result.Message;
            NoteKey = "Bad";
            return;
        }

        _ = _services.Cli.RecordHandOff(
            result.ExecutablePath ?? "defenseclaw",
            argv,
            "Opened in a console window: the value is typed at the CLI's own hidden prompt and never passes through this app. " +
            "The outcome is not observed here; the credential list is re-read when you return.");

        _awaitingReturn = true;
        Note = "Opened a console window running `defenseclaw " + string.Join(' ', argv) + "`. Type the value there; the list refreshes when you come back to this window.";
        NoteKey = "Neutral";
    }

    /// <summary>The window was activated again. Re-reads the list once if a console was opened since the last read.</summary>
    public void NotifyReturned()
    {
        if (!_awaitingReturn)
        {
            return;
        }

        _awaitingReturn = false;
        _ = RefreshAsync();
    }
}
