using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Setup hub: every <c>defenseclaw setup</c> flow the installed CLI exposes, as a
/// searchable card grid, the connector roster the wizards act on, and the guardrail quick controls.
/// <para>
/// <b>The card list is not written down anywhere.</b> It comes from
/// <see cref="WizardCatalog"/>, which parses the live CLI's help at runtime — so a setup
/// target added in a future 0.8.x appears here without an app update, and one that is removed
/// stops appearing. The certification badge is the CLI's own
/// <c>Platform status on windows:</c> answer, not a table in this app: on 0.8.7 that means
/// Claude Code and Codex are certified and every other connector is not, but the badge will
/// follow the binary if that changes. Certification is a question about <i>connectors</i>;
/// the other flows (rotate-token, webhook, llm, the scanners…) carry
/// <see cref="PlatformStatus.NotApplicable"/> and a neutral badge, and are not counted as
/// certified.
/// </para>
/// <para>
/// Uncertified cards stay launchable on purpose. The mac app's lesson — and the codex ghost
/// this project chased — is that hiding the option teaches nothing; the warning travels with
/// the operator into the wizard and onto its review screen instead. Targets that <i>cannot</i> work
/// on Windows (unsupported connectors, interactive-only wizards, Docker stacks) are the exception:
/// they sit in a last "not available" group, disabled, each with its reason and where to go instead
/// (<see cref="WizardWindowsPolicy"/>).
/// </para>
/// <para>
/// <b>Lifecycle.</b> The connector roster is re-derived on every gateway state change, but only
/// while this panel is <see cref="PanelViewModelBase.IsActive"/>: the subscription, and the
/// card updates that ride the catalog's <c>DefinitionChanged</c>, are attached in
/// <see cref="OnActivated"/> and detached in <see cref="OnDeactivated"/>, with one catch-up pass
/// on the way back in. A hidden tray app must not rebuild a card grid for nobody. The guardrail
/// status is a CLI call (about 1.7 s), so it is read once on activation when it is stale and on
/// Refresh — never on a timer.
/// </para>
/// </summary>
public sealed partial class SetupPanelViewModel : PanelViewModelBase
{
    /// <summary>How long a <c>guardrail status</c> read is trusted when the panel is re-entered.</summary>
    private static readonly TimeSpan GuardrailFreshFor = TimeSpan.FromMinutes(2);

    private readonly WizardCatalog _catalog;
    private readonly List<WizardCardViewModel> _all = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _certifiedOnly;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _statusNote = "Reading the setup catalog from the CLI…";

    [ObservableProperty]
    private string _loadError = string.Empty;

    [ObservableProperty]
    private bool _hasLoadError;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _connectorNote = string.Empty;

    public SetupPanelViewModel(AppServices services)
        : base(services)
    {
        // No subscriptions here: OnActivated attaches them and OnDeactivated lets go.
        _catalog = WizardCatalog.Shared(services);
    }

    public override string Title => "Setup";

    /// <summary>Opens the config.yaml editor (raw AvalonEdit + generated form tabs).</summary>
    [RelayCommand]
    private void OpenConfigEditor() => Views.ConfigEditor.ConfigEditorWindow.Show(Services);

    /// <summary>Opens the runtime update / release-trust awareness window.</summary>
    [RelayCommand]
    private void OpenUpdates() => Views.Updates.UpdatesWindow.Show(Services);

    public override string Description =>
        "Every defenseclaw setup flow on this machine, discovered from the CLI at runtime. Each one ends on a review screen showing the exact command before anything runs.";

    /// <summary>Cards, bucketed by <see cref="WizardGroups"/>, filtered by search and certification.</summary>
    public ObservableCollection<WizardGroupViewModel> Groups { get; } = new();

    /// <summary>Connectors DefenseClaw is currently configured with, from config.yaml and /health.</summary>
    public ObservableCollection<ConnectorChipViewModel> Connectors { get; } = new();

    public bool HasConnectors => Connectors.Count > 0;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BuildConnectors();

        try
        {
            var definitions = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(true);
            FillCards(definitions);
        }
        finally
        {
            // Whatever ended the read — a result, a failure, a cancelled wait — the spinner
            // stops. Left true, "Asking the CLI which setup targets it has" would show forever.
            IsLoading = false;
        }

        HasLoadError = _catalog.LoadError is { Length: > 0 };
        LoadError = _catalog.LoadError ?? string.Empty;
        ApplyFilters();

        // Warm the per-target help in the background. The hub is already complete and badged
        // from the top-level screen; this fills in real descriptions, the authoritative
        // platform-status line, and the option lists the wizards are built from.
        _ = WarmAsync(cancellationToken);
    }

    /// <summary>
    /// Attaches to the gateway monitor and the catalog, then catches up once: the panel may have
    /// been off screen while the roster changed and while per-target help landed, and none of it
    /// was being watched. The catch-up is a no-op merge when nothing moved. Runs possibly before
    /// <see cref="InitializeAsync"/> has finished on the first visit — with no cards yet, the
    /// card half of the catch-up simply has nothing to do.
    /// </summary>
    protected override void OnActivated()
    {
        _catalog.DefinitionChanged += OnDefinitionChanged;
        Services.Monitor.StateChanged += OnGatewayStateChanged;

        BuildConnectors();

        if (SyncCardsWithCatalog())
        {
            ApplyFilters();
        }

        // One read of the guardrail posture when there is none yet or it has gone stale — a CLI call,
        // so never more often than that.
        if (_guardrailLoadedAt is null || DateTimeOffset.UtcNow - _guardrailLoadedAt > GuardrailFreshFor)
        {
            _ = LoadGuardrailAsync();
        }
    }

    protected override void OnDeactivated()
    {
        _catalog.DefinitionChanged -= OnDefinitionChanged;
        Services.Monitor.StateChanged -= OnGatewayStateChanged;
    }

    /// <summary>Replaces the card list with one card per definition.</summary>
    private void FillCards(IReadOnlyList<WizardDefinition> definitions)
    {
        _all.Clear();
        foreach (var definition in definitions)
        {
            _all.Add(new WizardCardViewModel(definition));
        }
    }

    /// <summary>
    /// Brings every card up to the catalog's current definition for its target. Definitions are
    /// replaced wholesale and never mutated, so reference equality is exactly "is this card
    /// behind". Returns true if any card moved, which is when group membership and the
    /// certified filter may have changed too.
    /// </summary>
    private bool SyncCardsWithCatalog()
    {
        var changed = false;
        foreach (var card in _all)
        {
            if (_catalog.Find(card.Target) is { } definition && !ReferenceEquals(card.Definition, definition))
            {
                card.Apply(definition);
                changed = true;
            }
        }

        return changed;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnCertifiedOnlyChanged(bool value) => ApplyFilters();

    [RelayCommand]
    private async Task LaunchAsync(WizardCardViewModel? card)
    {
        if (card is null || !card.IsAvailable)
        {
            return;
        }

        card.IsOpening = true;
        try
        {
            await WizardLauncher
                .ShowAsync(Services, card.Target, Application.Current?.MainWindow)
                .ConfigureAwait(true);
        }
        finally
        {
            card.IsOpening = false;
        }

        // A wizard that ran may have changed the roster and the guardrail; the monitor's next poll would
        // catch the roster, but re-reading now makes the change visible immediately.
        BuildConnectors();
        _ = LoadGuardrailAsync();
    }

    /// <summary>
    /// The page-level refresh (F5 and the header button): the guardrail posture and the connector
    /// roster. Deliberately not the catalog — that is thirty-odd CLI help probes; see
    /// <see cref="ReloadCatalogCommand"/>.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        BuildConnectors();
        await LoadGuardrailAsync().ConfigureAwait(true);
    }

    /// <summary>Forgets every cached help screen and re-asks the CLI which setup targets it has.</summary>
    [RelayCommand]
    private async Task ReloadCatalogAsync()
    {
        // A second press while a read is in flight would interleave two reloads into one list.
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusNote = "Re-reading the setup catalog from the CLI…";
        Groups.Clear();
        _all.Clear();

        try
        {
            // Reload forgets the cached help screens too, so this genuinely re-asks the CLI.
            var definitions = await _catalog.ReloadAsync().ConfigureAwait(true);
            FillCards(definitions);
        }
        finally
        {
            IsLoading = false;
        }

        HasLoadError = _catalog.LoadError is { Length: > 0 };
        LoadError = _catalog.LoadError ?? string.Empty;
        ApplyFilters();
        BuildConnectors();

        _ = WarmAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        CertifiedOnly = false;
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        var targets = _all.Select(c => c.Target).ToArray();
        var probes = targets.Select(target => _catalog.EnsureDetailAsync(target, cancellationToken));

        try
        {
            await Task.WhenAll(probes).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        UpdateStatusNote();
    }

    private void OnDefinitionChanged(object? sender, WizardDefinitionChangedEventArgs e)
    {
        // Raised from the probe's continuation, which may be any thread pool thread. Attached
        // only while active; an event already queued when the panel goes away still lands here
        // and is applied in full, so the catch-up in OnActivated never finds a half-applied card.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            var card = _all.FirstOrDefault(c => string.Equals(c.Target, e.Definition.Target, StringComparison.Ordinal));
            card?.Apply(e.Definition);
            ApplyFilters();
        });
    }

    private void OnGatewayStateChanged(object? sender, GatewaySnapshotEventArgs e) => BuildConnectors();

    /// <summary>
    /// The roster the wizards act on: connector names from config.yaml unioned with what
    /// <c>/health</c> reports, each carrying its enforcement mode and hook fail-mode.
    /// Observe paired with fail-closed is called out, because it is the recurring bad default
    /// this project keeps running into: the connector records rather than blocks, yet a
    /// gateway error still blocks the tool call.
    /// </summary>
    private void BuildConnectors()
    {
        var config = Services.Config.Config;
        var snapshot = Services.Monitor.Current;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desired = new List<ConnectorChipViewModel>();

        foreach (var (name, settings) in config.Guardrail.Connectors.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(name))
            {
                continue;
            }

            desired.Add(new ConnectorChipViewModel(
                name,
                settings.Mode,
                settings.HookFailMode,
                settings.HasFailModeMismatch,
                fromConfig: true));
        }

        foreach (var name in snapshot.ActiveConnectors)
        {
            if (seen.Add(name))
            {
                desired.Add(new ConnectorChipViewModel(name, null, null, false, fromConfig: false));
            }
        }

        // Merged, not rebuilt: the roster is re-derived on every state change and on every
        // activation, and almost always comes out identical. Clear() + Add() would tear down and
        // re-create every chip's visuals each time for nothing.
        SyncCollection(Connectors, desired, chip => chip.Name, static (existing, wanted) => existing.IsSameAs(wanted));

        ConnectorNote = Connectors.Count == 0
            ? "No connectors are configured yet. Start with a certified one — Claude Code or Codex on this platform."
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} connector(s) configured. Mode and fail-mode come from guardrail.connectors in config.yaml; names without chips were reported by /health only.",
                Connectors.Count);

        OnPropertyChanged(nameof(HasConnectors));
    }

    private void ApplyFilters()
    {
        var needle = SearchText.Trim();

        // "Hide uncertified connectors": what it removes is a connector the CLI says is
        // not_certified / unsupported here, or one whose help is not read yet. A flow that
        // is not a connector (NotApplicable) has nothing to certify, so the filter leaves it
        // alone — exactly what it did before those cards stopped being mislabelled Certified.
        var matching = _all.Where(card =>
            (!CertifiedOnly || card.PlatformStatus is PlatformStatus.Certified or PlatformStatus.NotApplicable) &&
            (needle.Length == 0 || card.Matches(needle)))
            .ToList();

        Groups.Clear();
        foreach (var group in matching
            .GroupBy(c => c.Group, StringComparer.Ordinal)
            .OrderBy(g => WizardGroups.IndexOf(g.Key)))
        {
            Groups.Add(new WizardGroupViewModel(group.Key, group.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)));
        }

        IsEmpty = matching.Count == 0 && !IsLoading;
        UpdateStatusNote();
    }

    private void UpdateStatusNote()
    {
        if (IsLoading)
        {
            return;
        }

        var shown = Groups.Sum(g => g.Cards.Count);
        var certified = _all.Count(c => c.PlatformStatus == PlatformStatus.Certified);
        var pending = _all.Count(c => !c.IsDetailLoaded);

        StatusNote = string.Format(
            CultureInfo.CurrentCulture,
            "{0} of {1} setup target(s) shown · {2} certified connector(s) on Windows · {3} help screen(s) read{4}",
            shown,
            _all.Count,
            certified,
            _catalog.ProbeCount,
            pending > 0 ? $" · {pending} still loading" : string.Empty);
    }

    // ------------------------------------------------------------------ guardrail quick controls
    //
    // `defenseclaw guardrail status` is text (no --json), so it is parsed by GuardrailStatusParser and
    // shown verbatim as well. The verbs are the ones the CLI documents for day-to-day posture:
    // `guardrail enable|disable [--connector X] [--restart|--no-restart] [--yes]` and
    // `guardrail fail-mode open|closed [--connector X] [--restart|--no-restart] [--yes]` (verified against
    // each verb's --help). All of them restart the gateway by default, so every one goes through a review
    // that shows the exact argv, the CommandTiers tier, and offers --no-restart.

    private DateTimeOffset? _guardrailLoadedAt;
    private bool _guardrailReading;
    private string _pendingGuardrailVerb = string.Empty;
    private string? _pendingGuardrailArg;
    private string _guardrailReviewHeading = string.Empty;
    private string _guardrailReviewNote = string.Empty;

    [ObservableProperty]
    private bool _isGuardrailBusy;

    [ObservableProperty]
    private string _guardrailHeadline = "The guardrail posture has not been read yet.";

    /// <summary>Ok / Warn / Neutral — tone of the headline badge.</summary>
    [ObservableProperty]
    private string _guardrailHeadlineKey = "Neutral";

    [ObservableProperty]
    private string _guardrailAsOf = string.Empty;

    [ObservableProperty]
    private string _guardrailError = string.Empty;

    [ObservableProperty]
    private bool _hasGuardrailError;

    [ObservableProperty]
    private string _guardrailRaw = string.Empty;

    [ObservableProperty]
    private bool _showGuardrailRaw;

    /// <summary>A read succeeded and found no connector: a normal fresh install, not a fault.</summary>
    [ObservableProperty]
    private bool _showNoGuardrailRoster;

    [ObservableProperty]
    private bool _showEnableGuardrail = true;

    [ObservableProperty]
    private bool _showDisableGuardrail = true;

    [ObservableProperty]
    private GuardrailScopeOption? _selectedGuardrailScope;

    [ObservableProperty]
    private bool _isGuardrailReviewOpen;

    /// <summary>
    /// What the guardrail review dialog shows, in the model every confirmation surface shares. Rebuilt whenever the
    /// restart checkbox changes the argv; <c>disable</c> (which tears the live hooks down) is floored to Destructive.
    /// </summary>
    [ObservableProperty]
    private CommandReview? _guardrailReview;

    /// <summary>The review's "restart the gateway" checkbox; off adds <c>--no-restart</c>.</summary>
    [ObservableProperty]
    private bool _guardrailRestartAfter = true;

    [ObservableProperty]
    private bool _isGuardrailRunning;

    [ObservableProperty]
    private bool _hasGuardrailResult;

    [ObservableProperty]
    private string _guardrailResultHeading = string.Empty;

    [ObservableProperty]
    private string _guardrailResultBadge = string.Empty;

    [ObservableProperty]
    private string _guardrailResultKey = "Neutral";

    [ObservableProperty]
    private string _guardrailResultText = string.Empty;

    /// <summary>The roster rows of the last successful read.</summary>
    public ObservableCollection<GuardrailRowViewModel> GuardrailRows { get; } = new();

    /// <summary>Drift and other "!" notes the CLI printed under the roster.</summary>
    public ObservableCollection<string> GuardrailWarnings { get; } = new();

    /// <summary>"All connectors" plus one entry per connector in the roster; only offered when there are two or more.</summary>
    public ObservableCollection<GuardrailScopeOption> GuardrailScopes { get; } = new();

    public bool HasGuardrailRows => GuardrailRows.Count > 0;

    public bool HasGuardrailWarnings => GuardrailWarnings.Count > 0;

    /// <summary><c>--connector</c> is for multi-connector installs; with one connector there is nothing to scope.</summary>
    public bool HasMultipleGuardrailConnectors => GuardrailRows.Count > 1;

    /// <summary>Controls are off while a read or a change is in flight.</summary>
    public bool CanUseGuardrailControls => !IsGuardrailBusy && !IsGuardrailRunning;

    partial void OnIsGuardrailBusyChanged(bool value) => OnPropertyChanged(nameof(CanUseGuardrailControls));

    partial void OnIsGuardrailRunningChanged(bool value) => OnPropertyChanged(nameof(CanUseGuardrailControls));

    partial void OnGuardrailRestartAfterChanged(bool value) => RefreshGuardrailReview();

    /// <summary>Reads <c>guardrail status</c> once. Overlapping calls collapse into the one in flight.</summary>
    private async Task LoadGuardrailAsync()
    {
        if (_guardrailReading)
        {
            return;
        }

        _guardrailReading = true;
        IsGuardrailBusy = true;

        try
        {
            var invocation = await Services.Cli.RunAsync(new[] { "guardrail", "status" }).ConfigureAwait(true);
            ApplyGuardrailRead(invocation);
        }
        catch (CliNotFoundException ex)
        {
            SetGuardrailError("defenseclaw was not found, so the guardrail posture cannot be read. " + ex.Message);
        }
        finally
        {
            _guardrailReading = false;
            IsGuardrailBusy = false;
        }
    }

    private void ApplyGuardrailRead(CliInvocation invocation)
    {
        var text = JoinStdout(invocation);

        if (invocation.FailureReason is { Length: > 0 } reason)
        {
            SetGuardrailError("guardrail status did not finish: " + reason + ".");
            return;
        }

        if (invocation.ExitCode is not 0)
        {
            var detail = string.Join(' ', LastLines(invocation, 3));
            var code = invocation.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "without a code";
            SetGuardrailError($"guardrail status exited {code}. {detail}".Trim());
            return;
        }

        var status = GuardrailStatusParser.Parse(text);
        _guardrailLoadedAt = DateTimeOffset.UtcNow;
        GuardrailAsOf = "as of " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        GuardrailError = string.Empty;
        HasGuardrailError = false;
        GuardrailRaw = status.Raw;

        var keepScope = SelectedGuardrailScope?.Key ?? string.Empty;

        var rows = status.Connectors.Select(r => new GuardrailRowViewModel(r)).ToList();
        SyncCollection(GuardrailRows, rows, r => r.Key + "|" + r.Name, static (a, b) => a.IsSameAs(b));

        GuardrailWarnings.Clear();
        foreach (var warning in status.Warnings)
        {
            GuardrailWarnings.Add(warning);
        }

        GuardrailScopes.Clear();
        GuardrailScopes.Add(new GuardrailScopeOption("All connectors", string.Empty));
        foreach (var row in rows.Where(r => r.Key.Length > 0))
        {
            GuardrailScopes.Add(new GuardrailScopeOption(row.Name.Length > 0 ? row.Name : row.Key, row.Key));
        }

        SelectedGuardrailScope = GuardrailScopes.FirstOrDefault(s => s.Key == keepScope) ?? GuardrailScopes[0];

        (GuardrailHeadline, GuardrailHeadlineKey) = status.Enabled switch
        {
            true => ("The guardrail is enabled.", "Ok"),
            false => ("The guardrail is disabled: no connector is being enforced.", "Warn"),
            _ => ("The CLI's status text could not be interpreted; the verbatim output is below.", "Neutral"),
        };

        ShowEnableGuardrail = status.Enabled != true;
        ShowDisableGuardrail = status.Enabled != false;
        ShowNoGuardrailRoster = rows.Count == 0;

        OnPropertyChanged(nameof(HasGuardrailRows));
        OnPropertyChanged(nameof(HasGuardrailWarnings));
        OnPropertyChanged(nameof(HasMultipleGuardrailConnectors));
    }

    private void SetGuardrailError(string message)
    {
        GuardrailError = message;
        HasGuardrailError = true;
    }

    /// <summary>Stdout lines of a finished invocation, one string.</summary>
    private static string JoinStdout(CliInvocation invocation) => string.Join(
        '\n',
        invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));

    /// <summary>The last <paramref name="count"/> non-empty lines of either stream.</summary>
    private static IEnumerable<string> LastLines(CliInvocation invocation, int count) => invocation.OutputLines
        .Select(l => l.Text.Trim())
        .Where(t => t.Length > 0)
        .TakeLast(count);

    [RelayCommand]
    private void EnableGuardrail() => BeginGuardrailReview("enable", null);

    [RelayCommand]
    private void DisableGuardrail() => BeginGuardrailReview("disable", null);

    [RelayCommand]
    private void SetFailModeOpen() => BeginGuardrailReview("fail-mode", "open");

    [RelayCommand]
    private void SetFailModeClosed() => BeginGuardrailReview("fail-mode", "closed");

    [RelayCommand]
    private void CancelGuardrailReview() => IsGuardrailReviewOpen = false;

    [RelayCommand]
    private void DismissGuardrailResult() => HasGuardrailResult = false;

    private void BeginGuardrailReview(string verb, string? arg)
    {
        if (!CanUseGuardrailControls)
        {
            return;
        }

        _pendingGuardrailVerb = verb;
        _pendingGuardrailArg = arg;
        GuardrailRestartAfter = true;
        HasGuardrailResult = false;

        var scope = SelectedGuardrailScope is { Key.Length: > 0 } s ? $" for {s.Label}" : string.Empty;
        _guardrailReviewHeading = verb switch
        {
            "enable" => "Enable the guardrail" + scope + "?",
            "disable" => "Disable the guardrail" + scope + "?",
            _ => $"Set the hook fail mode to {arg}{scope}?",
        };

        _guardrailReviewNote = (verb, arg) switch
        {
            ("disable", _) => "Disabling tears down the connector hooks (for example the live Claude Code hooks), so agents on this machine " +
                              "run without DefenseClaw protection until you enable the guardrail again. The connector's policy is kept.",
            ("enable", _) => "Re-enables the guardrail from the existing configuration and runs connector setup at the next gateway start.",
            (_, "closed") => "Closed blocks a tool call or prompt whenever the gateway answers with an error or cannot be reached — " +
                             "including while the gateway restarts. Use it where every prompt must be inspected.",
            _ => "Open allows the tool call or prompt and logs the failure, so a misbehaving gateway never blocks an agent. Recommended for almost all installs.",
        };

        RefreshGuardrailReview();
        IsGuardrailReviewOpen = true;
    }

    /// <summary>The exact argv for the pending verb, from the review's current choices.</summary>
    private string[] BuildGuardrailArgv()
    {
        var argv = new List<string> { "guardrail", _pendingGuardrailVerb };
        if (_pendingGuardrailArg is not null)
        {
            argv.Add(_pendingGuardrailArg);
        }

        // These verbs ask "Continue?" without it, and the app has no way to answer.
        argv.Add("--yes");

        if (SelectedGuardrailScope is { Key.Length: > 0 } scope)
        {
            argv.Add("--connector");
            argv.Add(scope.Key);
        }

        if (!GuardrailRestartAfter)
        {
            argv.Add("--no-restart");
        }

        return argv.ToArray();
    }

    private void RefreshGuardrailReview()
    {
        if (_pendingGuardrailVerb.Length == 0)
        {
            return;
        }

        GuardrailReview = new CommandReview
        {
            Title = _guardrailReviewHeading,
            Summary = _guardrailReviewNote,
            Steps = new[]
            {
                new CommandReviewStep(
                    BuildGuardrailArgv(),
                    floor: _pendingGuardrailVerb == "disable" ? CommandTier.Destructive : CommandTier.StateChanging),
            },
            RestartsGateway = GuardrailRestartAfter,
            Warnings = new[]
            {
                GuardrailRestartAfter
                    ? CommandReviewWarning.GatewayRestart()
                    : new CommandReviewWarning(
                        "Gateway not restarted",
                        "The gateway is not restarted, so hooks are not regenerated until it next restarts: the change is saved but not yet in effect."),
            },
        };
    }

    [RelayCommand]
    private async Task ConfirmGuardrailAsync()
    {
        if (!IsGuardrailReviewOpen || IsGuardrailRunning)
        {
            return;
        }

        var argv = BuildGuardrailArgv();
        IsGuardrailRunning = true;

        try
        {
            var invocation = await Services.Cli.RunAsync(argv).ConfigureAwait(true);
            IsGuardrailReviewOpen = false;
            ShowGuardrailResult(argv, invocation);

            // Follow-ups are gated on the previous exit code: a failed change leaves the roster as it was
            // read, and re-reading it would only show the same thing while hiding the error.
            if (invocation.ExitCode is 0 && invocation.FailureReason is null)
            {
                IsGuardrailRunning = false;
                await LoadGuardrailAsync().ConfigureAwait(true);
            }
        }
        catch (CliNotFoundException ex)
        {
            IsGuardrailReviewOpen = false;
            HasGuardrailResult = true;
            GuardrailResultHeading = "defenseclaw was not found";
            GuardrailResultBadge = "not run";
            GuardrailResultKey = "Bad";
            GuardrailResultText = ex.Message;
        }
        finally
        {
            IsGuardrailRunning = false;
        }
    }

    private void ShowGuardrailResult(IReadOnlyList<string> argv, CliInvocation invocation)
    {
        HasGuardrailResult = true;
        GuardrailResultHeading = "defenseclaw " + string.Join(' ', argv) + " finished";

        (GuardrailResultBadge, GuardrailResultKey) = (invocation.FailureReason, invocation.ExitCode) switch
        {
            ({ Length: > 0 } reason, _) => (reason.StartsWith("cancelled", StringComparison.Ordinal) ? "cancelled" : "failed", "Warn"),
            (_, 0) => ("exit 0", "Ok"),
            (_, { } code) => ("exit " + code.ToString(CultureInfo.CurrentCulture), "Bad"),
            _ => ("exit unknown", "Neutral"),
        };

        var lines = invocation.OutputLines.Select(l => l.Text.TrimEnd()).Where(t => t.Length > 0).TakeLast(40);
        GuardrailResultText = invocation.FailureReason is { Length: > 0 } failure
            ? failure + "\n" + string.Join('\n', lines)
            : string.Join('\n', lines);
    }

}

/// <summary>One entry in the guardrail scope combo: a connector key for <c>--connector</c>, or empty for all of them.</summary>
public sealed record GuardrailScopeOption(string Label, string Key)
{
    public override string ToString() => Label;
}

/// <summary>One connector in the guardrail roster: its posture as <c>guardrail status</c> printed it.</summary>
public sealed class GuardrailRowViewModel
{
    private readonly GuardrailConnectorRow _row;

    public GuardrailRowViewModel(GuardrailConnectorRow row)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
    }

    public string Name => _row.Name.Length > 0 ? _row.Name : _row.Key;

    public string Key => _row.Key;

    public string State => _row.State;

    public string Mode => _row.Mode;

    public string Fail => _row.Fail;

    /// <summary>Ok when enabled, Warn otherwise — the state chip's tone.</summary>
    public string StateKey => string.Equals(State, "enabled", StringComparison.OrdinalIgnoreCase) ? "Ok" : "Warn";

    /// <summary>Observe + fail-closed is the recurring bad default: records only, yet still blocks on a gateway error.</summary>
    public bool HasMismatch =>
        string.Equals(Mode, "observe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Fail, "closed", StringComparison.OrdinalIgnoreCase);

    public string FailKey => HasMismatch ? "Warn" : "Neutral";

    /// <summary>The remaining columns as one line: "rule pack default · HILT off · scan regex_only · judge off".</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            AddPart(parts, "rule pack", _row.Column("Rule pack"));
            AddPart(parts, "HILT", _row.Column("HILT"));
            AddPart(parts, "scan", _row.Column("Scan"));
            AddPart(parts, "judge", _row.Column("Judge"));
            return string.Join(" · ", parts);
        }
    }

    public string MismatchNote =>
        "observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.";

    public bool IsSameAs(GuardrailRowViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return _row.Columns.Count == other._row.Columns.Count &&
               _row.Columns.Zip(other._row.Columns).All(p => p.First.Key == p.Second.Key && p.First.Value == p.Second.Value);
    }

    private static void AddPart(List<string> parts, string label, string value)
    {
        if (value.Length > 0)
        {
            parts.Add(label + " " + value);
        }
    }

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() =>
        $"{Name}: {State}, mode {Mode}, fail-{Fail}" + (HasMismatch ? ". Observe with fail-closed." : string.Empty);
}

/// <summary>One hub section: a <see cref="WizardGroups"/> bucket and its cards.</summary>
public sealed class WizardGroupViewModel
{
    public WizardGroupViewModel(string name, IEnumerable<WizardCardViewModel> cards)
    {
        Name = name;
        Cards = new ObservableCollection<WizardCardViewModel>(cards);
    }

    public string Name { get; }

    public ObservableCollection<WizardCardViewModel> Cards { get; }

    public string CountText => Cards.Count.ToString(CultureInfo.CurrentCulture);

    /// <summary>What a screen reader announces for the group heading.</summary>
    public string AutomationName => $"{Name}, {Cards.Count} setup target(s)";

    public override string ToString() => AutomationName;
}

/// <summary>One wizard card. Rebuilt in place as the per-target help lands.</summary>
public sealed partial class WizardCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _badge = string.Empty;

    /// <summary>Ok / Warn / Bad / Neutral — the badge tone key (<c>DcBadge Tag</c>).</summary>
    [ObservableProperty]
    private string _badgeKey = "Neutral";

    [ObservableProperty]
    private bool _isDetailLoaded;

    [ObservableProperty]
    private bool _isOpening;

    [ObservableProperty]
    private string _stepNote = string.Empty;

    /// <summary>Why this card cannot be launched on Windows, or empty when it can.</summary>
    [ObservableProperty]
    private string _unavailableReason = string.Empty;

    public WizardCardViewModel(WizardDefinition definition)
    {
        Target = definition?.Target ?? throw new ArgumentNullException(nameof(definition));
        Group = definition.Group;
        Apply(definition);
    }

    public string Target { get; }

    public string Group { get; private set; }

    public PlatformStatus PlatformStatus { get; private set; }

    /// <summary>
    /// The definition this card currently shows. Definitions are immutable and replaced
    /// wholesale, so comparing this by reference with the catalog's says whether the card is behind.
    /// </summary>
    public WizardDefinition Definition { get; private set; } = null!;

    /// <summary>The command this card runs, shown small on the card so nothing is a surprise.</summary>
    public string CommandHint => "defenseclaw setup " + Target;

    public bool IsAvailable => UnavailableReason.Length == 0;

    public bool HasUnavailableReason => UnavailableReason.Length > 0;

    /// <summary>The name a screen reader announces for the card.</summary>
    public string AutomationName => IsAvailable
        ? $"{Title} setup. {Badge}."
        : $"{Title} setup, not available on this machine. {UnavailableReason}";

    /// <summary>The name of the card's button: "Configure Claude Code" — the bare word alone says nothing in a list of cards.</summary>
    public string LaunchAutomationName => "Configure " + Title;

    public void Apply(WizardDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
        PlatformStatus = definition.PlatformStatus;
        Title = definition.Title;
        Summary = definition.Description;
        Badge = PlatformStatusText.Badge(definition.PlatformStatus);
        BadgeKey = PlatformStatusText.Key(definition.PlatformStatus);
        IsDetailLoaded = definition.IsDetailLoaded;

        UnavailableReason = WizardWindowsPolicy.UnavailableReason(Target, definition.PlatformStatus) ?? string.Empty;
        Group = IsAvailable ? definition.Group : WizardGroups.Unavailable;

        StepNote = definition.DetailError is { Length: > 0 } error
            ? error
            : definition.IsDetailLoaded
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} step(s) · {1}",
                    definition.Steps.Count,
                    definition.IsCurated ? "curated layout" : "generated from --help")
                : "reading --help…";

        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(HasUnavailableReason));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(LaunchAutomationName));
    }

    public bool Matches(string needle) =>
        Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Target.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Summary.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        Group.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => AutomationName;
}

/// <summary>One connector in the roster strip, with its mode and hook fail-mode.</summary>
public sealed class ConnectorChipViewModel
{
    public ConnectorChipViewModel(string name, string? mode, string? failMode, bool hasMismatch, bool fromConfig)
    {
        Name = name;
        Mode = string.IsNullOrWhiteSpace(mode) ? "mode unset" : mode.Trim().ToLowerInvariant();
        FailMode = string.IsNullOrWhiteSpace(failMode) ? "fail-mode unset" : "fail-" + failMode.Trim().ToLowerInvariant();
        HasMismatch = hasMismatch;
        FromConfig = fromConfig;
    }

    public string Name { get; }

    public string Mode { get; }

    public string FailMode { get; }

    /// <summary>Observe mode paired with a fail-closed hook: records nothing, blocks on error.</summary>
    public bool HasMismatch { get; }

    public bool FromConfig { get; }

    public string MismatchNote =>
        "observe + fail-closed: this connector never blocks on policy, but does block whenever the gateway errors.";

    public string SourceNote => FromConfig ? "config.yaml" : "/health only";

    /// <summary>Warn when observe is paired with fail-closed, otherwise neutral — the fail-mode chip's tone.</summary>
    public string FailKey => HasMismatch ? "Warn" : "Neutral";

    /// <summary>The tooltip: the mismatch explanation when there is one, otherwise where the values came from.</summary>
    public string ToolTipText => HasMismatch ? MismatchNote : "Read from " + SourceNote;

    /// <summary>True when <paramref name="other"/> would render exactly like this chip.</summary>
    public bool IsSameAs(ConnectorChipViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Name, other.Name, StringComparison.Ordinal) &&
               string.Equals(Mode, other.Mode, StringComparison.Ordinal) &&
               string.Equals(FailMode, other.FailMode, StringComparison.Ordinal) &&
               HasMismatch == other.HasMismatch &&
               FromConfig == other.FromConfig;
    }

    /// <summary>What a screen reader announces for the chip.</summary>
    public override string ToString() =>
        $"{Name}: {Mode}, {FailMode}, from {SourceNote}" + (HasMismatch ? ". " + MismatchNote : string.Empty);
}
