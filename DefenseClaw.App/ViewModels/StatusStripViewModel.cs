using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The chips of the shell's status strip beside the gateway pill (CUST-273): what the TUI's <c>widgets/status_strip.py</c> shows - Watchdog,
/// Guardrail, Keys, alerts, connector, redaction, policy posture, a command running, stale data, version - from what the app already holds.
/// <para>
/// <b>Nothing here reads anything.</b> Every chip is derived from state that exists for another reason: the monitor's snapshot (<c>/health</c>:
/// the Watchdog, Guardrail and Policy chips, the alert list, the connector roster, the version), config.yaml (policy, when the gateway does not
/// say), <see cref="ConnectorScope"/>, <see cref="CommandActivity"/> (the runner's started / finished events), and <see cref="StatusFacts"/> -
/// the two things a panel read for itself and handed over: the credentials still missing (Setup) and the aggregate redaction label (Overview).
/// A fact nobody has handed over yet draws no chip: nothing is claimed that nothing has said.
/// </para>
/// <para>
/// <b>Stale.</b> The Stale chip is on while the last good poll is more than three polling intervals old (<see cref="PollFreshness"/>). A monitor
/// whose poll is blocked publishes nothing, so no event can say so: while the strip is on screen (<see cref="SetActive"/>) it looks again every
/// polling interval - a comparison of two numbers on a timer - and turns the chip on within one interval of the line being crossed. It looks
/// again at once when the strip comes back on screen, when a snapshot arrives and when the health pulse or the pause changes.
/// </para>
/// <para>
/// <b>Width.</b> The view-model knows no widths. The panel that lays the chips out hides the ones that do not fit, lowest priority first, and
/// tells the view-model which (<see cref="ApplyCollapsed"/>); the <c>+N</c> chip lists them, in the tone of the worst, so a warning that has
/// no room is never silent.
/// </para>
/// </summary>
public sealed partial class StatusStripViewModel : ObservableObject, IDisposable
{
    /// <summary>The chips in the order they are drawn, with how they are drawn and where they stand in the collapse order (lower stays longer).</summary>
    private static readonly (StripChipKey Key, StripChipKind Kind, int Priority)[] ChipTable =
    {
        (StripChipKey.Detail, StripChipKind.Sentence, 0),
        (StripChipKey.Watchdog, StripChipKind.State, 20),
        (StripChipKey.Guardrail, StripChipKind.State, 10),
        (StripChipKey.Keys, StripChipKind.Badge, 30),
        (StripChipKey.Alerts, StripChipKind.Badge, 40),
        (StripChipKey.Connector, StripChipKind.Chip, 50),
        (StripChipKey.Redaction, StripChipKind.Badge, 60),
        (StripChipKey.Policy, StripChipKind.Chip, 70),
        (StripChipKey.Running, StripChipKind.Badge, 80),
        (StripChipKey.Stale, StripChipKind.Badge, 90),
        (StripChipKey.Version, StripChipKind.Chip, 100),
        (StripChipKey.Overflow, StripChipKind.Badge, 0),
    };

    private static readonly IReadOnlySet<string> NoDisabledConnectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly AppServices _services;
    private readonly IPollFreshness _freshness;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly CommandActivity _commands;
    private readonly bool _ownsCommands;
    private readonly Dictionary<StripChipKey, StripChip> _byKey = new();
    private readonly object _timerGate = new();

    private GatewaySnapshot _snapshot;
    private HashSet<StripChipKey> _hidden = new();
    private ITimer? _timer;
    private TimeSpan _timerPeriod;
    private bool _active;
    private bool _disposed;

    /// <param name="services">The composition root.</param>
    public StatusStripViewModel(AppServices services)
        : this(services, null, null, null, null)
    {
    }

    /// <param name="services">The composition root.</param>
    /// <param name="freshness">How old the monitor's last good poll is; the monitor itself when null. A test passes a fake.</param>
    /// <param name="time">The clock the stale look is timed on; the system's when null. A test passes one whose timers it fires.</param>
    /// <param name="post">Runs an action on the UI thread; the application's dispatcher when null. A test passes one that runs it in place.</param>
    /// <param name="commands">Counts the commands in flight; one over the app's runner when null (and then disposed with this).</param>
    internal StatusStripViewModel(
        AppServices services,
        IPollFreshness? freshness,
        TimeProvider? time,
        Action<Action>? post,
        CommandActivity? commands)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _freshness = freshness ?? services.Monitor;
        _time = time ?? TimeProvider.System;
        _post = post ?? UiPost.ToDispatcher;
        _ownsCommands = commands is null;
        _commands = commands ?? new CommandActivity(services.Cli, _post);

        var chips = new List<StripChip>(ChipTable.Length);
        foreach (var (key, kind, priority) in ChipTable)
        {
            var chip = new StripChip(key, kind, priority);
            _byKey[key] = chip;
            chips.Add(chip);
        }

        Chips = new ReadOnlyCollection<StripChip>(chips);
        _snapshot = services.Monitor.Current;

        services.ConfigReloaded += OnConfigReloaded;
        services.ConnectorScope.Changed += OnScopeChanged;
        services.StatusFacts.Changed += OnFactsChanged;
        services.Settings.Changed += OnSettingsChanged;
        _commands.Changed += OnCommandsChanged;

        BuildAll();
        RefreshOverflow();
    }

    /// <summary>The chips in the order they are drawn, the detail sentence first and the <c>+N</c> chip last. Fixed for the life of the strip; each changes in place.</summary>
    public IReadOnlyList<StripChip> Chips { get; }

    /// <summary>One chip by its key.</summary>
    public StripChip Chip(StripChipKey key) => _byKey[key];

    /// <summary>True while the strip is on screen and looking for a monitor that has stopped reporting (see <see cref="SetActive"/>).</summary>
    public bool IsActive => _active;

    /// <summary>
    /// A snapshot arrived from the monitor (the shell view-model forwards each material change): every chip that is read from it, or from config.yaml
    /// behind it, is drawn again.
    /// </summary>
    internal void Apply(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _snapshot = snapshot;
        BuildAll();
        RefreshOverflow();
    }

    /// <summary>
    /// The strip is on screen (true) or not - the dashboard is hidden to the tray, or minimized (false). On screen it looks for a stopped monitor every
    /// polling interval and once now; off screen it keeps no timer at all, and the look it takes when it comes back is the first thing it does.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_disposed || _active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            EvaluateFreshness();
            RefreshOverflow();
            ArmTimer();
        }
        else
        {
            DisarmTimer();
        }
    }

    /// <summary>
    /// Looks at the monitor's last good poll and turns the Stale chip on or off. The timer calls this; so do a snapshot, a settings change and
    /// <see cref="SetActive"/>. Internal so the tests can drive it with a clock instead of waiting.
    /// </summary>
    internal void EvaluateFreshness()
    {
        var chip = Chip(StripChipKey.Stale);
        var since = _freshness.SinceLastGoodPoll;
        var cadence = _freshness.Cadence;

        if (PollFreshness.IsStale(since, cadence, _freshness.IsPaused) && since is { } age)
        {
            chip.Show(
                "Stale",
                StripPresentation.Warn,
                StripPresentation.StaleDetail(age, cadence),
                $"Stale data: the last good gateway poll finished {StripPresentation.Span(age)} ago");
        }
        else
        {
            chip.Hide();
        }
    }

    /// <summary>
    /// The panel laid the chips out and these did not fit: the <c>+N</c> chip names them. Also what the panel's command calls
    /// (<see cref="ApplyHiddenCommand"/>), with the chips themselves.
    /// </summary>
    internal void ApplyCollapsed(IReadOnlyCollection<StripChipKey> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);

        _hidden = new HashSet<StripChipKey>(hidden);
        RefreshOverflow();
    }

    [RelayCommand]
    private void ApplyHidden(IReadOnlyList<object?>? chips) =>
        ApplyCollapsed(chips?.OfType<StripChip>().Select(static c => c.Key).ToArray() ?? Array.Empty<StripChipKey>());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.ConfigReloaded -= OnConfigReloaded;
        _services.ConnectorScope.Changed -= OnScopeChanged;
        _services.StatusFacts.Changed -= OnFactsChanged;
        _services.Settings.Changed -= OnSettingsChanged;
        _commands.Changed -= OnCommandsChanged;
        DisarmTimer();
        if (_ownsCommands)
        {
            _commands.Dispose();
        }
    }

    // ---- What the chips are drawn from ---------------------------------------------------------------------------------------------------

    private void BuildAll()
    {
        BuildDetail();
        BuildSubsystems();
        BuildKeys();
        BuildAlerts();
        BuildConnector();
        BuildRedaction();
        BuildPolicy();
        BuildRunning();
        EvaluateFreshness();
        BuildVersion();
    }

    private void BuildDetail()
    {
        var detail = _snapshot.Detail;
        if (detail.Length == 0)
        {
            Chip(StripChipKey.Detail).Hide();
            return;
        }

        Chip(StripChipKey.Detail).Show(detail, StripPresentation.Neutral, detail, detail);
    }

    /// <summary>
    /// Watchdog and Guardrail: the Overview's Services cards for them, drawn small - the same state word and tone, so the two cannot disagree (the
    /// TUI's strip mirrors its Services box the same way). Not drawn before the first poll, when there is nothing to report, nor where there is no
    /// gateway to ask (DefenseClaw not installed, or not initialized: the banner says so); and neutral while monitoring is paused, because what
    /// they show is then the last reading. A gateway that is installed and stopped is "unknown", which is the truth.
    /// </summary>
    private void BuildSubsystems()
    {
        var watchdog = Chip(StripChipKey.Watchdog);
        var guardrail = Chip(StripChipKey.Guardrail);
        if (_snapshot.PolledAt == DateTimeOffset.MinValue ||
            _snapshot.State is AppGatewayState.NotInstalled or AppGatewayState.NotInitialized)
        {
            watchdog.Hide();
            guardrail.Hide();
            return;
        }

        var cards = OverviewPanelViewModel.BuildServiceCards(
            _snapshot,
            _snapshot.Health,
            StripPresentation.RosterOf(_snapshot),
            NoDisabledConnectors,
            _services.Config.Config.Claw.Mode);

        Subsystem(watchdog, cards.First(static c => c.Key == "watcher"));
        Subsystem(guardrail, cards.First(static c => c.Key == "guardrail"));
    }

    private void Subsystem(StripChip chip, ServiceRow row)
    {
        var paused = _snapshot.IsPaused;
        var name = StripPresentation.SubsystemName(row.Name, row.StateText, paused);
        chip.Show(
            StripPresentation.SubsystemText(row.Name, row.StateText),
            paused ? StripPresentation.Neutral : row.StateKey,
            StripPresentation.SubsystemDetail(row.Name, row.StateText, row.Summary, paused),
            name,
            name);
    }

    private void BuildKeys()
    {
        var chip = Chip(StripChipKey.Keys);
        if (_services.StatusFacts.MissingKeys is not { Count: > 0 } missing)
        {
            chip.Hide();
            return;
        }

        var text = StripPresentation.KeysText(missing);
        chip.Show(text, StripPresentation.Bad, StripPresentation.KeysDetail(missing), StripPresentation.KeysName(missing), text);
    }

    private void BuildAlerts()
    {
        var text = GatewayPresentation.AlertText(_snapshot);
        Chip(StripChipKey.Alerts).Show(
            text,
            GatewayPresentation.AlertTone(_snapshot),
            GatewayPresentation.AlertDetail(_snapshot),
            StripPresentation.AlertsName(_snapshot),
            text);
    }

    private void BuildConnector()
    {
        var scope = _services.ConnectorScope.Current;
        var text = StripPresentation.ConnectorText(_snapshot, scope);
        var detail = StripPresentation.ConnectorDetail(_snapshot, scope);
        Chip(StripChipKey.Connector).Show(text, StripPresentation.Neutral, detail, detail, text);
    }

    private void BuildRedaction()
    {
        var chip = Chip(StripChipKey.Redaction);
        if (_services.StatusFacts.Redaction is not { } label)
        {
            chip.Hide();
            return;
        }

        var text = StripPresentation.RedactionText(label);
        chip.Show(text, StripPresentation.RedactionTone(label), StripPresentation.RedactionDetail(label), text, text);
    }

    private void BuildPolicy()
    {
        var chip = Chip(StripChipKey.Policy);
        if (StripPresentation.PolicyMode(_snapshot, _services.Config.Config, _services.ConnectorScope.Current) is not { } posture)
        {
            chip.Hide();
            return;
        }

        var (mode, fromGateway) = posture;
        var enforcing = fromGateway ? _snapshot.Health?.Guardrail?.DetailBool("enforcement_enabled") : null;
        var text = StripPresentation.PolicyText(mode);
        chip.Show(text, StripPresentation.Neutral, StripPresentation.PolicyDetail(mode, fromGateway, enforcing), $"Policy posture: {mode}", text);
    }

    private void BuildRunning()
    {
        var chip = Chip(StripChipKey.Running);
        var count = _commands.Count;
        if (count <= 0)
        {
            chip.Hide();
            return;
        }

        var text = StripPresentation.RunningText(count);
        var detail = StripPresentation.RunningDetail(count);
        chip.Show(text, StripPresentation.Busy, detail, detail, text);
    }

    private void BuildVersion()
    {
        var chip = Chip(StripChipKey.Version);
        var text = GatewayPresentation.VersionText(_snapshot);
        if (text.Length == 0)
        {
            chip.Hide();
            return;
        }

        chip.Show(text, StripPresentation.Neutral, StripPresentation.VersionDetail(_snapshot), text, text);
    }

    // ---- The +N chip ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Draws the <c>+N</c> chip from the chips the panel hid and that are still on: its tone is the worst of theirs, so a warning with no room still
    /// colours something, and its tooltip and automation name list them. Nothing hidden: it is empty, and the panel gives it no room.
    /// </summary>
    private void RefreshOverflow()
    {
        var overflow = Chip(StripChipKey.Overflow);
        var hidden = Chips.Where(c => c.Priority > 0 && c.IsShown && _hidden.Contains(c.Key)).ToList();
        if (hidden.Count == 0)
        {
            overflow.Show(string.Empty, StripPresentation.Neutral, string.Empty, string.Empty);
            return;
        }

        var worst = hidden.MaxBy(static c => ToneRank(c.Tone))!;
        var count = hidden.Count.ToString(CultureInfo.InvariantCulture);
        var noun = hidden.Count == 1 ? "status item is" : "status items are";
        var lines = hidden.Select(static c => c.Summary).ToList();

        overflow.Show(
            "+" + count,
            ToneRank(worst.Tone) == 0 ? StripPresentation.Neutral : worst.Tone,
            $"{count} {noun} hidden at this window width:{Environment.NewLine}" + string.Join(Environment.NewLine, lines.Select(static l => "• " + l)),
            $"{count} more {(hidden.Count == 1 ? "status item" : "status items")} hidden at this window width: {string.Join("; ", lines)}");
    }

    private static int ToneRank(string tone) => tone switch
    {
        "Bad" or "Critical" => 3,
        "Warn" or "High" => 2,
        "Medium" or "Low" => 1,
        _ => 0,
    };

    // ---- The timer -----------------------------------------------------------------------------------------------------------------------

    private void ArmTimer()
    {
        lock (_timerGate)
        {
            if (_disposed || !_active)
            {
                return;
            }

            var period = PollFreshness.LookEvery(_freshness.Cadence);
            if (_timer is null)
            {
                _timer = _time.CreateTimer(OnTimer, null, period, period);
                _timerPeriod = period;
            }
            else if (period != _timerPeriod)
            {
                _ = _timer.Change(period, period);
                _timerPeriod = period;
            }
        }
    }

    private void DisarmTimer()
    {
        lock (_timerGate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Raised on a pool thread; the look happens on the UI thread, where the chip is bound.</summary>
    private void OnTimer(object? state) =>
        _post(() =>
        {
            if (_disposed || !_active)
            {
                return;
            }

            EvaluateFreshness();
            RefreshOverflow();
            ArmTimer();
        });

    // ---- What else changes a chip --------------------------------------------------------------------------------------------------------

    /// <summary>config.yaml was read again (raised on the UI thread): the posture it states, when the gateway does not say.</summary>
    private void OnConfigReloaded(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed)
        {
            BuildPolicy();
            RefreshOverflow();
        }
    });

    /// <summary>The shared connector filter or its roster changed (on the thread that changed it).</summary>
    private void OnScopeChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed)
        {
            BuildConnector();
            BuildPolicy();
            RefreshOverflow();
        }
    });

    /// <summary>A panel handed over a credential list or the redaction label.</summary>
    private void OnFactsChanged(object? sender, EventArgs e) => _post(() =>
    {
        if (!_disposed)
        {
            BuildKeys();
            BuildRedaction();
            RefreshOverflow();
        }
    });

    /// <summary>A command started or the last ones finished (raised on the UI thread by <see cref="CommandActivity"/>).</summary>
    private void OnCommandsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        BuildRunning();
        RefreshOverflow();
    }

    /// <summary>The health pulse or the pause changed: what "three intervals" is, and whether anything is stale at all. Raised on the thread that wrote the setting.</summary>
    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        if (!e.Affects(AppSettingsSections.Monitoring))
        {
            return;
        }

        _post(() =>
        {
            if (_disposed)
            {
                return;
            }

            EvaluateFreshness();
            RefreshOverflow();
            ArmTimer();
        });
    }
}
