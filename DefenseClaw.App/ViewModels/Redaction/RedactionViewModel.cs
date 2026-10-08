using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Runtime;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels.Redaction;

/// <summary>
/// The redaction window (CUST-295): what DefenseClaw collects and how it is redacted, and every operation of
/// <c>defenseclaw setup redaction</c> as a reviewed form. It exists only on a runtime that has the command
/// (<see cref="RuntimeCapability.RedactionAdvanced"/>); on 0.8.10 nothing opens it and, if it were open when the runtime changed, it
/// shows the one sentence that says why and does nothing.
/// <list type="bullet">
///   <item><b>Reads run directly.</b> The six read operations (<c>status</c>, <c>bucket list</c>, <c>profile list|show</c>,
///     <c>destination show</c>, <c>route list</c>) only print. They go through <see cref="RunReadAsync"/>, which refuses any other command.</item>
///   <item><b>Every change is previewed first.</b> A preview is the same command with <c>--json --dry-run</c> at the end: the CLI computes the
///     effective plan before and after and writes nothing. It goes through <see cref="RunPreviewAsync"/>, which refuses a command that is not
///     exactly a preview, so a preview can neither write <c>config.yaml</c> nor restart the gateway
///     (<see cref="RedactionArgv.IsPreview"/>: no <c>--yes</c>, no <c>--restart</c>, no <c>--no-restart</c>). The CLI's difference is shown as rows
///     (<see cref="RedactionDiffRow"/>), those that start unredacted delivery first.</item>
///   <item><b>Every apply is reviewed.</b> Applying runs a fresh preview, then opens the shared <see cref="DiscoverActionReview"/> with the exact
///     command, what the preview said, whether the gateway restarts, and, when raw content would start to flow, a tick the operator must give
///     before it runs. The restart is a choice made in the form (off by default) and named in the command (<c>--restart</c> or
///     <c>--no-restart</c>); the review spells out what each means.</item>
///   <item><b>Only a read policy authorizes a change.</b> If the status could not be read, changes are off until it can (the preview, which only
///     reads, stays available).</item>
/// </list>
/// Not a panel: <c>RedactionWindow.Open</c> creates it on demand from Setup and Logs, so it starts no timer and holds no subscription beyond
/// the runtime check, which <see cref="Dispose"/> lets go.
/// </summary>
public sealed partial class RedactionViewModel : ObservableObject, IDisposable
{
    /// <summary>How long a read or a preview may take. They only read, and a stuck one must not hold the window for the runner's 30 minutes.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long an apply may take, restart included.</summary>
    internal static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(5);

    private readonly AppServices _services;
    private readonly CancellationTokenSource _life = new();
    private readonly Dictionary<string, IReadOnlyList<RedactionRoute>> _routes = new(StringComparer.Ordinal);
    private bool _disposed;

    public RedactionViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Review = new DiscoverActionReview(services);
        Form = new RedactionFormViewModel(RoutesOf);
        Form.Changed += OnFormChanged;
        Review.PropertyChanged += OnReviewChanged;
        _services.Runtime.Changed += OnRuntimeChanged;
        _quickProfile = "sensitive";
    }

    public string Title => "Redaction policy";

    public string Subtitle =>
        "What DefenseClaw collects and how it is redacted before it is stored or sent. Every change is previewed first; nothing is written until you review the command and apply it.";

    /// <summary>The confirm-and-run overlay every apply goes through.</summary>
    public DiscoverActionReview Review { get; }

    /// <summary>The advanced editor's form.</summary>
    public RedactionFormViewModel Form { get; }

    /// <summary>Test seam: runs a command instead of <c>Services.Cli.RunAsync</c>, so a test feeds exact output and never starts a process.</summary>
    internal Func<IReadOnlyList<string>, CliRunOptions, Task<CliInvocation>>? RunCli { get; set; }

    /// <summary>Test seam: answers the runtime check instead of <see cref="RuntimeService.Check"/>.</summary>
    internal Func<GateDecision>? Gate { get; set; }

    // ------------------------------------------------------------------ gate

    private GateDecision Decision => Gate is { } gate ? gate() : _services.Runtime.Check(RuntimeCapability.RedactionAdvanced);

    /// <summary>True while the connected runtime has the command. False hides everything but <see cref="UnsupportedMessage"/>.</summary>
    public bool IsSupported => Decision.IsAvailable;

    public bool IsUnsupported => !IsSupported;

    public string UnsupportedMessage => Decision.Reason ?? RuntimeCapabilityCatalog.UnsupportedMessage;

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsSupported));
        OnPropertyChanged(nameof(IsUnsupported));
        OnPropertyChanged(nameof(UnsupportedMessage));
        NotifyActionState();
    }

    // ------------------------------------------------------------------ what is going on

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(CanRun), nameof(CanQuickPreview), nameof(CanQuickApply), nameof(CanApplyPreview))]
    private bool _isBusy;

    /// <summary>The window may start something: it is open, the runtime has the command, nothing is running and no review is waiting.</summary>
    public bool CanAct => !_disposed && IsSupported && !IsBusy && !Review.IsOpen;

    private void OnReviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscoverActionReview.IsOpen) or nameof(DiscoverActionReview.IsRunning))
        {
            NotifyActionState();
        }
    }

    private void NotifyActionState()
    {
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanQuickPreview));
        OnPropertyChanged(nameof(CanQuickApply));
        OnPropertyChanged(nameof(CanApplyPreview));
    }

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

    private void ShowNotice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        NoticeTitle = title;
        NoticeSeverity = severity;
        NoticeMessage = message;
    }

    // ------------------------------------------------------------------ the command runner and its two doors

    private Task<CliInvocation> RunCliAsync(IReadOnlyList<string> argv, CliRunOptions options) =>
        RunCli is { } run ? run(argv, options) : _services.Cli.RunAsync(argv, cancellationToken: _life.Token, options: options);

    private static CliRunOptions Reading { get; } = CliRunOptions.WithTimeout(ReadTimeout) with { RetainFullOutput = true };

    /// <summary>
    /// What a run came to: its output as text when it finished with exit 0, else why it did not. Never throws for a CLI that is missing or a
    /// run that fails: those are data.
    /// </summary>
    private sealed record Run(string Stdout, string Error)
    {
        public bool Ok => Error.Length == 0;
    }

    /// <summary>
    /// Runs one of the six read commands without a review. Anything else throws: this is a door for reading, not for a change.
    /// </summary>
    internal Task<CliInvocation> RunReadAsync(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (!RedactionArgv.IsRead(argv))
        {
            throw new InvalidOperationException($"'{string.Join(' ', argv)}' is not a read-only redaction command, so it has to be previewed or reviewed.");
        }

        return RunCliAsync(argv, Reading);
    }

    /// <summary>
    /// Runs a preview without a review. Only a command that is exactly a preview passes (<see cref="RedactionArgv.IsPreview"/>: it ends
    /// <c>--json --dry-run</c> and has no <c>--yes</c>, <c>--restart</c> or <c>--no-restart</c>), so what this starts can neither write
    /// <c>config.yaml</c> nor restart the gateway.
    /// </summary>
    internal Task<CliInvocation> RunPreviewAsync(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (!RedactionArgv.IsPreview(argv))
        {
            throw new InvalidOperationException($"'{string.Join(' ', argv)}' is not a preview, so it has to be reviewed first.");
        }

        return RunCliAsync(argv, Reading);
    }

    private async Task<Run> ToRunAsync(Func<Task<CliInvocation>> start, string what)
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
                var tail = string.Join(' ', LastLines(invocation, 3));
                return new Run(Stdout(invocation), $"{what} exited {code}. {tail}".Trim());
            }

            return new Run(Stdout(invocation), string.Empty);
        }
        catch (CliNotFoundException ex)
        {
            return new Run(string.Empty, "The defenseclaw CLI was not found. " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new Run(string.Empty, $"{what} was cancelled.");
        }
    }

    internal static string Stdout(CliInvocation invocation) => string.Join(
        '\n',
        invocation.OutputLines.Where(static l => l.Stream == CliStream.StandardOutput).Select(static l => l.Text));

    internal static string Transcript(CliInvocation invocation) => string.Join(
        '\n',
        invocation.OutputLines.Where(static l => l.Stream != CliStream.Notice).Select(static l => l.Text.TrimEnd()).Where(static t => t.Length > 0).TakeLast(40));

    private static IEnumerable<string> LastLines(CliInvocation invocation, int count) => invocation.OutputLines
        .Select(static l => l.Text.Trim())
        .Where(static t => t.Length > 0)
        .TakeLast(count);

    internal static void TraceFault(string what, Exception ex) => Trace.TraceError($"redaction: {what} failed: {ex}");

    private IReadOnlyList<RedactionRoute> RoutesOf(string destination) =>
        _routes.TryGetValue(destination, out var routes) ? routes : [];

    // ------------------------------------------------------------------ lifetime

    /// <summary>Esc: closes the review if one is open. True when it did, so the window stays.</summary>
    public bool HandleEscape() => Review.HandleEscape();

    /// <summary>Lets go of the runtime subscription and stops anything still running (the runner kills the process tree).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Runtime.Changed -= OnRuntimeChanged;
        Review.PropertyChanged -= OnReviewChanged;
        Form.Changed -= OnFormChanged;
        _life.Cancel();
        _life.Dispose();
    }

    /// <summary>The routes read for the destinations that have ordered routes, by destination name.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<RedactionRoute>> Routes => _routes;

    /// <summary>The status as a chip reads it ("Redaction: none", "Redaction: mixed"): what the status strip and the observability card take when they show redaction (CUST-273, CUST-272).</summary>
    public string StatusChipText => Status?.ChipText ?? "Redaction: not read";

    public string StatusChipTone => Status?.Tone ?? "Neutral";
}
