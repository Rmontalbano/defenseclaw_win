using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Setup;
using DefenseClaw.Core.Text;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels.SetupResources;

/// <summary>
/// A list-first Setup editor (CUST-270): one resource of the 0.8.10 CLI - observability destinations, webhooks or trusted paths - as a list with
/// a state per row and the verbs that act on the selected one. The three editors differ in their columns, their verbs and the words of their
/// reviews (the subclasses); everything else is here, once:
/// <list type="bullet">
///   <item><b>Reads run directly.</b> The list (<c>setup &lt;noun&gt; list --json</c>) and a webhook's <c>show</c> only read, so they go through
///     <see cref="RunReadAsync"/>, which refuses any other command (<see cref="SetupResourceArgv.IsRead"/>: the whole shape, so it also runs on a
///     managed installation). Each read is bounded in time and in output, runs off the UI thread, and every address in what it prints is cut to
///     its host before the line is stored (<see cref="EndpointHost.ScrubLine"/> as <see cref="CliRunOptions.OutputLineFilter"/>): neither the
///     rows, the details, Activity nor a review's result can show a path, a query or a token of one.</item>
///   <item><b>Every change is reviewed.</b> Enable, Disable, Test and Remove open the shared <see cref="DiscoverActionReview"/> with the exact
///     argv; Remove is destructive, Test is reviewed because it contacts a live endpoint (a webhook test delivers a real message). Nothing a row
///     does on Enter or a double click runs a change, let alone a test.</item>
///   <item><b>Add opens the existing wizard on <c>add</c></b> (<see cref="OpenWizard"/>), which ends on its own review.</item>
///   <item><b>A failed read is not an empty list.</b> <see cref="SetupListState.Empty"/> is a read that worked and found nothing;
///     <see cref="SetupListState.Failed"/> is a list nobody knows, and says what went wrong. Rows kept from an earlier read stay on screen, labelled,
///     and stop authorizing changes (<see cref="CatalogTrust"/>).</item>
///   <item><b>The installation comes first.</b> On a managed or invalid installation the list and a webhook's details are still read, and every
///     control that changes anything is off with the installation's sentence (<see cref="InstallationBlockedReason"/>); the runner
///     would refuse it anyway, and the review carries the same sentence. Then the list's trust (config.yaml or .env changed since the read, a read that
///     is old or failed), then busy, then the row's own reasons (a built-in is protected).</item>
/// </list>
/// Not a panel: <c>SetupResourceWindow.Open</c> creates it on demand and <see cref="Dispose"/> lets go of its subscriptions.
/// </summary>
public abstract partial class SetupResourceViewModel : ObservableObject, IDisposable
{
    /// <summary>How long a read may take. It only reads, and a stuck one must not hold the window for the runner's 30 minutes.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a change may take, restart of the gateway included.</summary>
    internal static readonly TimeSpan ChangeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long a test may take: a few probes of five seconds each, or one delivery.</summary>
    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The options of a read: bounded in time, the whole output kept (a JSON document cut at its head does not parse), every address cut to its host.</summary>
    private static readonly CliRunOptions Reading = CliRunOptions.WithTimeout(ReadTimeout) with
    {
        RetainFullOutput = true,
        OutputLineFilter = EndpointHost.ScrubLine,
    };

    private readonly CancellationTokenSource _life = new();
    private readonly List<SetupResourceRow> _rows = new();
    private DateTimeOffset? _lastReadAt;
    private bool _reading;
    private bool _disposed;
    private int _detailGeneration;

    protected SetupResourceViewModel(AppServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Trust = CatalogTrust.Watching(services.Paths);
        Review = new DiscoverActionReview(services) { RunGuard = ReasonToRefuseRun };
        Review.PropertyChanged += OnReviewChanged;
        Services.Installation.Changed += OnInstallationChanged;
        Services.ConfigReloaded += OnConfigReloaded;
    }

    protected AppServices Services { get; }

    /// <summary>The editor of <paramref name="resource"/>. <paramref name="prefill"/> and <paramref name="context"/> only mean something to the trusted-folder editor (a folder a failed setup named, and why the operator is here).</summary>
    public static SetupResourceViewModel Create(AppServices services, SetupResource resource, string prefill = "", string context = "") => resource switch
    {
        SetupResource.Observability => new ObservabilityDestinationsViewModel(services),
        SetupResource.Webhooks => new WebhooksViewModel(services),
        SetupResource.TrustedPaths => new TrustedPathsViewModel(services, prefill, context),
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, null),
    };

    /// <summary>The confirm-and-run overlay every change goes through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>Whether the list on screen may authorize a change; see <see cref="CatalogTrust"/>.</summary>
    public CatalogTrust Trust { get; }

    /// <summary>The rows on screen.</summary>
    public ObservableCollection<SetupResourceRow> Rows { get; } = new();

    // ------------------------------------------------------------------ what the editor is

    public abstract SetupResource Resource { get; }

    /// <summary>The window's heading.</summary>
    public abstract string Title { get; }

    /// <summary>One or two sentences under it: what the list is and what its verbs do.</summary>
    public abstract string Subtitle { get; }

    /// <summary>One row, as a noun: "destination", "webhook", "trusted folder".</summary>
    public abstract string Singular { get; }

    /// <summary>Rows, as a noun: "destinations", "webhooks", "trusted folders".</summary>
    public abstract string Plural { get; }

    /// <summary>The columns of the list, in order.</summary>
    public abstract IReadOnlyList<SetupColumn> Columns { get; }

    /// <summary>True when the editor has the button for <paramref name="verb"/>.</summary>
    public abstract bool Offers(SetupVerb verb);

    /// <summary>The heading of the empty state: the read worked and found nothing.</summary>
    public abstract string EmptyTitle { get; }

    /// <summary>What to say under it, and where to go from here.</summary>
    public abstract string EmptyDetail { get; }

    /// <summary>The heading of the failed state: the read did not work, so the list is unknown.</summary>
    public virtual string FailedTitle => $"The {Singular} list could not be read";

    /// <summary>What the failed state adds to the CLI's reason: why this is not the same as an empty list.</summary>
    public virtual string FailedDetail => $"This is not an empty list: nobody knows what is configured. Nothing here can be changed until the {Plural} have been read.";

    /// <summary>
    /// What this editor shows that the others do not (the trusted-folder editor's connectors and the reason the operator came), for the view to
    /// pick a template by type; null for an editor with nothing extra.
    /// </summary>
    public virtual object? Extras => null;

    /// <summary>The list a read asks for.</summary>
    protected IReadOnlyList<string> ListArgv => SetupResourceArgv.List(Resource);

    /// <summary>Turns what the list printed into rows, or says why it cannot. Runs off the UI thread.</summary>
    protected abstract bool TryParse(string stdout, out IReadOnlyList<SetupResourceRow> rows, out string error);

    // ------------------------------------------------------------------ test seams

    /// <summary>Test seam: runs a command instead of <c>Services.Cli.RunAsync</c>, so a test feeds exact output and never starts a process.</summary>
    internal Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>>? RunCli { get; set; }

    /// <summary>
    /// How Add opens the setup wizard on <c>add</c>, and returns when it closes (the window wires it to <c>WizardLauncher</c>); null leaves Add with
    /// nothing to open, and it says so.
    /// </summary>
    internal Func<Services.Wizards.WizardPreset, Task>? OpenWizard { get; set; }

    // ------------------------------------------------------------------ the list

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowList), nameof(ShowEmpty), nameof(ShowFailed))]
    private SetupListState _state = SetupListState.Loading;

    [ObservableProperty]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>Why the rows on screen are from an earlier read (a refresh failed); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshWarning))]
    private string _refreshWarning = string.Empty;

    [ObservableProperty]
    private string _asOf = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SetupResourceRow? _selectedRow;

    /// <summary>A read is in flight (the rows on screen are about to be replaced).</summary>
    [ObservableProperty]
    private bool _isBusy;

    public bool ShowLoading => State == SetupListState.Loading;

    public bool ShowList => State == SetupListState.Loaded;

    public bool ShowEmpty => State == SetupListState.Empty;

    public bool ShowFailed => State is SetupListState.Failed or SetupListState.CliUnavailable;

    public bool HasRefreshWarning => RefreshWarning.Length > 0;

    public bool HasSelection => SelectedRow is not null;

    /// <summary>The caption under the title: how many rows, and how many are on.</summary>
    public virtual string Caption => State is SetupListState.Loaded or SetupListState.Empty
        ? Rows.Count == 1 ? $"1 {Singular}" : $"{Rows.Count.ToString(CultureInfo.CurrentCulture)} {Plural}"
        : string.Empty;

    // ------------------------------------------------------------------ notices

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

    protected void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        NoticeTitle = title;
        NoticeSeverity = severity;
        NoticeMessage = message;
    }

    // ------------------------------------------------------------------ the installation and the trust

    /// <summary>Why every control that changes something is off because the installation is managed or invalid; null while it may be changed.</summary>
    public string? InstallationBlockedReason => Services.Installation.BlockedReason;

    public bool HasInstallationBlock => InstallationBlockedReason is not null;

    private void OnInstallationChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(InstallationBlockedReason));
        OnPropertyChanged(nameof(HasInstallationBlock));
        NotifyActions();
    }

    /// <summary>config.yaml or .env were rewritten (the existing watcher raises this for both): whether that postdates the read on screen is the trust's to say.</summary>
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

    /// <summary>
    /// Why <paramref name="verb"/> is off right now, or null when it is on. In this order, so a control says one sentence and the first is the
    /// one that matters: the installation (a change to a managed installation is refused whatever the list says), then the list (it must be a
    /// complete, recent read of a configuration that has not moved), then another command running, then no row, then the row's own reasons.
    /// A read (Show) needs only a row to read, and Add only the installation: it opens a wizard that reviews its own command.
    /// </summary>
    public string? BlockedReason(SetupVerb verb)
    {
        if (verb != SetupVerb.Show && Services.Installation.BlockedReason is { } installation)
        {
            return installation;
        }

        if (verb == SetupVerb.Add)
        {
            return null;
        }

        if (verb == SetupVerb.Show)
        {
            return SelectedRow is { } shown
                ? shown.BlockedReason(SetupVerb.Show)
                : $"Select a {Singular} first.";
        }

        if (State is not (SetupListState.Loaded or SetupListState.Empty))
        {
            return Trust.Reason ?? $"Changes are off until the {Plural} have been read.";
        }

        if (Trust.Reason is { } untrusted)
        {
            return untrusted;
        }

        if (IsBusy || Review.IsOpen || Review.IsRunning)
        {
            return "Another command is running.";
        }

        return SelectedRow is { } row ? row.BlockedReason(verb) : $"Select a {Singular} first.";
    }

    public bool HasAdd => Offers(SetupVerb.Add);

    public bool HasEnable => Offers(SetupVerb.Enable);

    public bool HasDisable => Offers(SetupVerb.Disable);

    public bool HasTest => Offers(SetupVerb.Test);

    public bool HasShow => Offers(SetupVerb.Show);

    public bool HasRemove => Offers(SetupVerb.Remove);

    public bool CanAdd => BlockedReason(SetupVerb.Add) is null;

    public bool CanEnable => BlockedReason(SetupVerb.Enable) is null;

    public bool CanDisable => BlockedReason(SetupVerb.Disable) is null;

    public bool CanTest => BlockedReason(SetupVerb.Test) is null;

    public bool CanShow => BlockedReason(SetupVerb.Show) is null && !IsDetailLoading;

    public bool CanRemove => BlockedReason(SetupVerb.Remove) is null;

    /// <summary>What Add does, or why it is off.</summary>
    public string AddTip => BlockedReason(SetupVerb.Add) ?? TipFor(SetupVerb.Add);

    public string EnableTip => BlockedReason(SetupVerb.Enable) ?? TipFor(SetupVerb.Enable);

    public string DisableTip => BlockedReason(SetupVerb.Disable) ?? TipFor(SetupVerb.Disable);

    public string TestTip => BlockedReason(SetupVerb.Test) ?? TipFor(SetupVerb.Test);

    public string ShowTip => BlockedReason(SetupVerb.Show) ?? TipFor(SetupVerb.Show);

    public string RemoveTip => BlockedReason(SetupVerb.Remove) ?? TipFor(SetupVerb.Remove);

    /// <summary>The tooltip of a button that is on: what it will do, and that the command is shown first.</summary>
    protected abstract string TipFor(SetupVerb verb);

    /// <summary>Tells every binding that depends on what may change (the buttons, their tooltips) to look again.</summary>
    protected void NotifyActions()
    {
        foreach (var name in ActionProperties)
        {
            OnPropertyChanged(name);
        }
    }

    private static readonly string[] ActionProperties =
    [
        nameof(CanAdd), nameof(CanEnable), nameof(CanDisable), nameof(CanTest), nameof(CanShow), nameof(CanRemove),
        nameof(AddTip), nameof(EnableTip), nameof(DisableTip), nameof(TestTip), nameof(ShowTip), nameof(RemoveTip),
    ];

    partial void OnIsBusyChanged(bool value) => NotifyActions();

    // ------------------------------------------------------------------ reading

    private Task<CliInvocation> RunCliAsync(IReadOnlyList<string> argv, CliRunOptions options) =>
        RunCli is { } run ? run(argv, options) : Services.Cli.RunAsync(argv, cancellationToken: _life.Token, options: options);

    /// <summary>
    /// Runs one of the reads (<see cref="SetupResourceArgv.IsRead"/>) without a review. Anything else throws: this is the one door the editor
    /// starts a command through on its own, and it is not a door for a change.
    /// </summary>
    internal Task<CliInvocation> RunReadAsync(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (!SetupResourceArgv.IsRead(argv))
        {
            throw new InvalidOperationException($"'{string.Join(' ', argv)}' is not one of the read-only setup commands, so it has to be reviewed first.");
        }

        return RunCliAsync(argv, Reading);
    }

    /// <summary>What a run came to: its output when it finished with exit 0, else why it did not. Never throws for a CLI that is missing or a run that fails: those are data.</summary>
    protected sealed record Run(string Stdout, string Error, bool CliMissing = false, bool Truncated = false)
    {
        public bool Ok => Error.Length == 0;
    }

    protected async Task<Run> ToRunAsync(Func<Task<CliInvocation>> start, string what)
    {
        try
        {
            var invocation = await start().ConfigureAwait(true);
            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                return new Run(Stdout(invocation), $"{what} did not finish: {reason}.");
            }

            if (invocation.ExitCode is not 0)
            {
                var code = invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "without a code";
                return new Run(Stdout(invocation), $"{what} exited {code}. {Tail(invocation)}".Trim());
            }

            return invocation.IsOutputTruncated
                ? new Run(string.Empty, $"{what} printed more than this app keeps for one command.", Truncated: true)
                : new Run(Stdout(invocation), string.Empty);
        }
        catch (CliNotFoundException ex)
        {
            return new Run(string.Empty, "The defenseclaw CLI was not found. " + ex.Message, CliMissing: true);
        }
        catch (OperationCanceledException)
        {
            return new Run(string.Empty, $"{what} was cancelled.");
        }
    }

    internal static string Stdout(CliInvocation invocation) => string.Join(
        '\n',
        invocation.OutputLines.Where(static l => l.Stream == CliStream.StandardOutput).Select(static l => l.Text));

    /// <summary>The last lines a command printed, either stream: what says why it failed. The runner already cut every address in them.</summary>
    private static string Tail(CliInvocation invocation) => string.Join(
        ' ',
        invocation.OutputLines.Select(static l => l.Text.Trim()).Where(static t => t.Length > 0).TakeLast(3));

    /// <summary>The window opened, or Refresh: reads the list. Overlapping calls collapse into the one in flight.</summary>
    public async Task ReadAsync()
    {
        if (_disposed || _reading)
        {
            return;
        }

        _reading = true;
        IsBusy = true;
        Trust.BeginRead();
        try
        {
            await ReadOnceAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing here may escape: this runs fire-and-forget when the window opens.
            TraceFault("read", ex);
            Fail(SetupListState.Failed, FailedTitle, EndpointDisplay.ScrubText(ex.Message));
        }
        finally
        {
            _reading = false;
            IsBusy = false;
            NotifyActions();
            OnPropertyChanged(nameof(Caption));
        }
    }

    private async Task ReadOnceAsync()
    {
        var command = "defenseclaw " + string.Join(' ', ListArgv);
        var run = await ToRunAsync(() => RunReadAsync(ListArgv), command).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        if (!run.Ok)
        {
            Fail(run.CliMissing ? SetupListState.CliUnavailable : SetupListState.Failed, run.CliMissing ? "The defenseclaw CLI was not found" : FailedTitle, run.Error);
            return;
        }

        // Parsed off the UI thread: it is a document the CLI printed, and its size is the CLI's to choose (bounded by the runner's retention).
        var stdout = run.Stdout;
        var (ok, rows, error) = await Task.Run(() =>
        {
            var parsed = TryParse(stdout, out var read, out var why);
            return (parsed, read, why);
        }).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        if (!ok)
        {
            Fail(SetupListState.Failed, "Unexpected output from " + command, error);
            return;
        }

        var keep = SelectedRow?.Key;
        _rows.Clear();
        _rows.AddRange(rows);
        _lastReadAt = DateTimeOffset.Now;
        Trust.MarkComplete();
        RefreshWarning = string.Empty;
        ErrorTitle = string.Empty;
        ErrorMessage = string.Empty;
        AsOf = "as of " + _lastReadAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
        ReplaceRows(_rows);
        State = _rows.Count == 0 ? SetupListState.Empty : SetupListState.Loaded;

        SelectedRow = keep is null ? null : Rows.FirstOrDefault(r => string.Equals(r.Key, keep, StringComparison.Ordinal));
        await AfterReadAsync().ConfigureAwait(true);
    }

    /// <summary>Where a subclass reads what else its window shows (the trusted-path editor's connectors) once the list has been read. The default does nothing.</summary>
    protected virtual Task AfterReadAsync() => Task.CompletedTask;

    /// <summary>
    /// A read that did not work. Rows kept from an earlier read stay on screen, labelled, and no longer authorize a change; with none, the
    /// list is <see cref="SetupListState.Failed"/>: unknown, which is not the same as empty.
    /// </summary>
    private void Fail(SetupListState failedState, string title, string message)
    {
        Trust.MarkFailed($"{title}: {message}");

        if (_lastReadAt is { } at && _rows.Count > 0)
        {
            RefreshWarning = $"{title}: {message} Showing the last good read (as of {at:HH:mm}); changes are off until the list is read again.";
            return;
        }

        _rows.Clear();
        Rows.Clear();
        _lastReadAt = null;
        AsOf = string.Empty;
        ErrorTitle = title;
        ErrorMessage = message;
        State = failedState;
        SelectedRow = null;
    }

    private void ReplaceRows(IReadOnlyList<SetupResourceRow> rows)
    {
        // The rows are tens at most: a rebuild is cheap, and the selection is put back by key by the caller.
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
    }

    /// <summary>The Refresh button and F5: not while a review is up (the review is a decision about the list as it was).</summary>
    [RelayCommand]
    private Task Refresh() => Review.IsOpen ? Task.CompletedTask : ReadAsync();

    // ------------------------------------------------------------------ the selected row

    [ObservableProperty]
    private IReadOnlyList<SetupFact> _detailFacts = Array.Empty<SetupFact>();

    /// <summary>The exact command whose output the details show (a webhook's <c>show</c>); empty when they come from the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailCommand))]
    private string _detailCommand = string.Empty;

    /// <summary>What that command printed, exactly as the runner stored it (every address already cut to its host).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailRaw))]
    private string _detailRaw = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailError))]
    private string _detailError = string.Empty;

    [ObservableProperty]
    private bool _isDetailLoading;

    public bool HasDetailCommand => DetailCommand.Length > 0;

    public bool HasDetailRaw => DetailRaw.Length > 0;

    public bool HasDetailError => DetailError.Length > 0;

    partial void OnIsDetailLoadingChanged(bool value) => NotifyActions();

    partial void OnSelectedRowChanged(SetupResourceRow? value)
    {
        // A newer selection supersedes a read of the older one.
        _detailGeneration++;
        IsDetailLoading = false;
        DetailFacts = value?.Facts ?? Array.Empty<SetupFact>();
        DetailCommand = string.Empty;
        DetailRaw = string.Empty;
        DetailError = string.Empty;
        NotifyActions();
    }

    /// <summary>What a row of this editor reads when it is shown (a webhook: <c>setup webhook show</c>). Only asked of an editor that <see cref="Offers"/> Show.</summary>
    protected virtual IReadOnlyList<string> ShowArgv(SetupResourceRow row) =>
        throw new InvalidOperationException($"The {Singular} editor has no show command.");

    /// <summary>Reads the facts a show command printed. Only asked of an editor that <see cref="Offers"/> Show.</summary>
    protected virtual bool TryParseShown(string stdout, SetupResourceRow row, out IReadOnlyList<SetupFact> facts, out string error)
    {
        facts = Array.Empty<SetupFact>();
        error = $"The {Singular} editor has no show command.";
        return false;
    }

    /// <summary>
    /// Show: reads the selected row again with the CLI's own <c>show</c> and puts what it printed beside the exact command. A read, so it runs
    /// without a review and on any installation; a newer selection ends it.
    /// </summary>
    [RelayCommand]
    private async Task ShowAsync()
    {
        if (!Offers(SetupVerb.Show))
        {
            return;
        }

        if (BlockedReason(SetupVerb.Show) is { } blocked)
        {
            ShowNotice("Cannot show", blocked);
            return;
        }

        // BlockedReason names the missing row, so there is one from here on.
        var row = SelectedRow!;
        var argv = ShowArgv(row);
        var generation = ++_detailGeneration;
        IsDetailLoading = true;
        try
        {
            var command = "defenseclaw " + string.Join(' ', argv);
            var run = await ToRunAsync(() => RunReadAsync(argv), command).ConfigureAwait(true);
            if (generation != _detailGeneration || _disposed)
            {
                return;
            }

            DetailCommand = CommandReview.CommandLine(CommandReview.DefaultExecutable, argv);
            DetailRaw = run.Stdout;
            if (!run.Ok)
            {
                DetailError = run.Error;
                return;
            }

            if (TryParseShown(run.Stdout, row, out var facts, out var error))
            {
                DetailFacts = facts;
                DetailError = string.Empty;
            }
            else
            {
                DetailError = error;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceFault("show", ex);
            if (generation == _detailGeneration)
            {
                DetailError = EndpointDisplay.ScrubText(ex.Message);
            }
        }
        finally
        {
            if (generation == _detailGeneration)
            {
                IsDetailLoading = false;
            }
        }
    }

    [RelayCommand]
    private void ClearSelection() => SelectedRow = null;

    [RelayCommand]
    private void CopyName()
    {
        if (SelectedRow is { } row)
        {
            _ = Views.Controls.DcClipboard.TrySetText(row.Key);
        }
    }

    /// <summary>
    /// Enter, or a double click, on a row. <b>It never runs a change and never a test</b>: where the editor has a read for the row (a webhook's
    /// Show) it runs that, as the TUI's Enter does; otherwise it only selects the row, whose details are beside the list, and says what Test would
    /// do. A reflexive Enter must not send a request to a live endpoint.
    /// </summary>
    [RelayCommand]
    private async Task ActivateRowAsync()
    {
        if (SelectedRow is null)
        {
            return;
        }

        if (Offers(SetupVerb.Show))
        {
            await ShowAsync().ConfigureAwait(true);
            return;
        }

        if (Offers(SetupVerb.Test))
        {
            ShowNotice(
                "Nothing was sent",
                $"Enter only selects the {Singular}. Press Test to check that it answers; the command is shown first and nothing runs until you confirm it.",
                InfoBarSeverity.Informational);
        }
    }

    // ------------------------------------------------------------------ Esc and lifetime

    /// <summary>Esc: closes the topmost transient surface (the review, then the notice, then the selection). True when it closed something.</summary>
    public bool HandleEscape()
    {
        if (Review.HandleEscape())
        {
            return true;
        }

        if (HasNotice)
        {
            CloseNotice();
            return true;
        }

        if (SelectedRow is not null)
        {
            SelectedRow = null;
            return true;
        }

        return false;
    }

    /// <summary>Lets go of the subscriptions and stops anything still running (the runner kills the process tree).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Services.Installation.Changed -= OnInstallationChanged;
        Services.ConfigReloaded -= OnConfigReloaded;
        Review.PropertyChanged -= OnReviewChanged;
        _life.Cancel();
        _life.Dispose();
        OnDispose();
    }

    /// <summary>Where a subclass lets go of what it holds. The default does nothing.</summary>
    protected virtual void OnDispose()
    {
    }

    protected bool IsDisposed => _disposed;

    internal static void TraceFault(string what, Exception ex) => Trace.TraceError($"setup editor: {what} failed: {ex}");
}
