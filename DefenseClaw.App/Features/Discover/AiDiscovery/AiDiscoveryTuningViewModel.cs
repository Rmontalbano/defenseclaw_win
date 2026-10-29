using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.ViewModels;

/// <summary>One box of the AI Discovery tuning dialog: a number or a list of folders, what config.yaml says, what is typed, and what is wrong with it.</summary>
public sealed partial class TuningField : ObservableObject
{
    internal TuningField(string key, string label, string flag, string help, string text)
    {
        Key = key;
        Label = label;
        Flag = flag;
        Help = help;
        _text = text;
    }

    /// <summary>The config key (<c>scan_interval_min</c>), which <see cref="TuningChange.Key"/> names too.</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>One value per line (the folders), not a single line.</summary>
    public bool IsMultiLine { get; init; }

    /// <summary>The <c>agent discovery enable</c> flag that carries it.</summary>
    public string Flag { get; }

    /// <summary>One sentence on what it does and what the CLI accepts.</summary>
    public string Help { get; }

    [ObservableProperty]
    private string _text;

    /// <summary>What is wrong with the typed value, in a sentence; empty when the CLI would take it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problem = string.Empty;

    public bool HasProblem => Problem.Length > 0;

    /// <summary>"was 5": shown beside a box whose value differs from config.yaml's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _changeText = string.Empty;

    public bool IsChanged => ChangeText.Length > 0;

    /// <summary>False when the installed CLI's help does not list the flag: the box is off and the value is never sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    private bool _isAvailable = true;

    public bool IsUnavailable => !IsAvailable;

    public string UnavailableText => "This DefenseClaw does not list this option, so it cannot be changed here.";

    /// <summary>Raised when the operator types.</summary>
    internal event EventHandler? Edited;

    partial void OnTextChanged(string value) => Edited?.Invoke(this, EventArgs.Empty);
}

/// <summary>One switch of the AI Discovery tuning dialog: a source discovery looks at, or a privacy option.</summary>
public sealed partial class TuningSwitch : ObservableObject
{
    internal TuningSwitch(string key, string label, string flag, string help, bool original)
    {
        Key = key;
        Label = label;
        Flag = flag;
        Help = help;
        Original = original;
        _isOn = original;
    }

    public string Key { get; }

    public string Label { get; }

    /// <summary>The positive form of the flag (<c>--include-shell-history</c>).</summary>
    public string Flag { get; }

    public string Help { get; }

    /// <summary>What config.yaml said when the dialog read it.</summary>
    public bool Original { get; private set; }

    /// <summary>Starts the switch again from what config.yaml says (<paramref name="original"/>) and what the form shows (<paramref name="value"/>).</summary>
    internal void Reset(bool original, bool value)
    {
        Original = original;
        IsOn = value;
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(AutomationName));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(ChangeText), nameof(StateText), nameof(AutomationName))]
    private bool _isOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable), nameof(AutomationName))]
    private bool _isAvailable = true;

    public bool IsUnavailable => !IsAvailable;

    public bool IsChanged => IsAvailable && IsOn != Original;

    public string ChangeText => IsChanged ? (Original ? "was on" : "was off") : string.Empty;

    public string StateText => IsOn ? "On" : "Off";

    public string UnavailableText => "This DefenseClaw does not list this option, so it cannot be changed here.";

    public string AutomationName =>
        $"{Label}: {StateText}" + (IsChanged ? ", changed, " + ChangeText : string.Empty) + (IsAvailable ? string.Empty : ". Not available.") + ". " + Help;

    internal event EventHandler? Moved;

    partial void OnIsOnChanged(bool value) => Moved?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The AI Discovery tuning dialog (CUST-271), on the AI Discovery panel: the TUI's AI Discovery wizard and the Mac's - mode, cadence, scope and
/// sources over the flags of <c>defenseclaw agent discovery enable</c> - in this app's safety model. The form starts from config.yaml (the
/// runtime's defaults for a key it does not name), <b>Review</b> shows the one command that results with its argv, and the shared
/// <see cref="DiscoverActionReview"/> runs it when confirmed. Nothing runs from a field.
/// <para>
/// <b>Only what changed is sent</b> (<see cref="AiDiscoveryTuning"/>): a cadence change is <c>agent discovery enable --yes --scan-interval-min 10</c>
/// and nothing else, because every flag of the verb defaults to "leave as is" and an <c>enable</c> on a discovery that is already on applies
/// exactly the flags it is given. Off to on is the same verb (with whatever else changed); on to off is <c>agent discovery disable --yes</c>; and a
/// dialog with nothing changed says "Nothing to apply" rather than running an enable that prints "already enabled" and asks for a scan.
/// </para>
/// <para>
/// <b>Checked, never assumed.</b> Each flag the dialog offers must be listed by the installed CLI's <c>agent discovery enable --help</c> (and
/// <c>disable</c>'s for turning it off); an option the CLI does not list is off to the touch and never sent, and a help that cannot be read leaves
/// the review off with the reason. The values are checked against the CLI's own ranges before a review opens, so a mistake is a sentence beside
/// the box and not a command that fails. Guards (installation, unreadable config.yaml, a config.yaml that changed after it was read) are
/// <see cref="SetupDialogViewModel"/>'s, in its order.
/// </para>
/// </summary>
public sealed partial class AiDiscoveryTuningViewModel : SetupDialogViewModel
{
    /// <summary>The path of the command whose help is read for the flags.</summary>
    private static readonly string[] EnablePath = { "agent", "discovery", "enable" };

    private static readonly string[] DisablePath = { "agent", "discovery", "disable" };

    private readonly List<TuningChange> _pending = new();
    private AiDiscoverySettings _original = AiDiscoverySettings.Defaults;
    private IReadOnlyList<string> _planArgv = Array.Empty<string>();
    private IReadOnlyList<TuningChange> _planChanges = Array.Empty<TuningChange>();
    private AiDiscoverySettings _planFrom = AiDiscoverySettings.Defaults;
    private AiDiscoverySettings _planTo = AiDiscoverySettings.Defaults;
    private HashSet<string>? _enableFlags;
    private HashSet<string>? _disableFlags;
    private HelpProbeResult _disableHelp = new(string.Empty, "not read");
    private Dictionary<string, string> _problems = new(StringComparer.Ordinal);

    /// <param name="services">The composition the dialog works in.</param>
    /// <param name="helpReader">
    /// Reads the <c>--help</c> of an <c>agent discovery</c> verb, by its words (<c>agent discovery enable</c>). Null: the Setup help probe's
    /// <c>CliHelpAsync</c> (cached, and no Activity entry); a test hands it a screen.
    /// </param>
    /// <param name="applied">What the hosting panel does after a run: it reads its own state again.</param>
    public AiDiscoveryTuningViewModel(
        AppServices services,
        Func<IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>>? helpReader = null,
        Func<DiscoverReviewResult, IReadOnlyList<string>, Task>? applied = null)
        : base(services)
    {
        HelpReader = helpReader ?? DefaultHelpReader;
        Applied = applied;

        ScanInterval = Field("scan_interval_min", "Scan interval (minutes)", "--scan-interval-min", RangeHelp("Minutes between full scans.", AiDiscoveryTuning.ScanIntervalRange.Min, AiDiscoveryTuning.ScanIntervalRange.Max));
        ProcessInterval = Field("process_interval_s", "Process poll (seconds)", "--process-interval-s", RangeHelp("Seconds between the cheap checks of the running processes.", AiDiscoveryTuning.ProcessIntervalRange.Min, AiDiscoveryTuning.ProcessIntervalRange.Max));
        ScanRoots = new TuningField("scan_roots", "Folders to scan", "--scan-roots", "One folder per line; ~ is your home folder. A comma would split a folder in two, so none is accepted.", string.Empty) { IsMultiLine = true };
        MaxFiles = Field("max_files_per_scan", "Max files per scan", "--max-files-per-scan", RangeHelp("The most files one scan looks at.", AiDiscoveryTuning.MaxFilesRange.Min, AiDiscoveryTuning.MaxFilesRange.Max));
        MaxBytes = Field("max_file_bytes", "Max bytes per file", "--max-file-bytes", RangeHelp("The largest file it reads, in bytes.", AiDiscoveryTuning.MaxFileBytesRange.Min, AiDiscoveryTuning.MaxFileBytesRange.Max));
        Fields = new[] { ScanInterval, ProcessInterval, ScanRoots, MaxFiles, MaxBytes };

        SourceSwitches = new[]
        {
            Switch("include_shell_history", "Shell history", "--include-shell-history", "Look in shell history for AI command-line tools."),
            Switch("include_package_manifests", "Package manifests", "--include-package-manifests", "Look in package.json, pyproject, Cargo.toml and the like for AI SDKs."),
            Switch("include_env_var_names", "Environment variable names", "--include-env-var-names", "Look at the names (never the values) of variables that hold AI provider keys."),
            Switch("include_network_domains", "Network domains", "--include-network-domains", "Look at hosts and SSH config for AI provider domains, and ask vetted loopback model servers what they host."),
        };
        PrivacySwitches = new[]
        {
            Switch("allow_workspace_signatures", "Honour workspace signatures", "--allow-workspace-signatures", "Use signature packs found inside the workspaces it scans. Off by default: a workspace can then change what is detected."),
            Switch("store_raw_local_paths", "Store raw local paths", "--store-raw-local-paths", "Keep raw local paths in the discovery state file. Off by default for privacy; the CLI says to turn it on only for diagnostics on a trusted machine."),
        };

        foreach (var field in Fields)
        {
            field.Edited += OnEdited;
        }

        foreach (var toggle in SourceSwitches.Concat(PrivacySwitches))
        {
            toggle.Moved += OnEdited;
        }
    }

    /// <summary>Test seam, and the production reader.</summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>> HelpReader { get; set; }

    /// <summary>What the hosting panel does after a run (reads its state again); null for none.</summary>
    internal Func<DiscoverReviewResult, IReadOnlyList<string>, Task>? Applied { get; set; }

    private Task<HelpProbeResult> DefaultHelpReader(IReadOnlyList<string> path, CancellationToken cancellationToken) =>
        WizardCatalog.Shared(Services).Probe.CliHelpAsync(path, cancellationToken);

    private static TuningField Field(string key, string label, string flag, string help) => new(key, label, flag, help, string.Empty);

    private static TuningSwitch Switch(string key, string label, string flag, string help) => new(key, label, flag, help, original: false);

    private static string RangeHelp(string what, long min, long max) =>
        string.Create(CultureInfo.CurrentCulture, $"{what} {min:N0} to {max:N0}.");

    // ------------------------------------------------------------------ the form

    public const string DialogTitle = "Tune AI Discovery";

    public string Title => DialogTitle;

    public string Subtitle => "Choose what AI Discovery looks at, and how often. Nothing changes until you review the command and confirm it.";

    public IReadOnlyList<TuningField> Fields { get; }

    public TuningField ScanInterval { get; }

    public TuningField ProcessInterval { get; }

    public TuningField ScanRoots { get; }

    public TuningField MaxFiles { get; }

    public TuningField MaxBytes { get; }

    /// <summary>What discovery looks at: shell history, package manifests, environment variable names, network domains.</summary>
    public IReadOnlyList<TuningSwitch> SourceSwitches { get; }

    /// <summary>The two privacy options.</summary>
    public IReadOnlyList<TuningSwitch> PrivacySwitches { get; }

    private IEnumerable<TuningSwitch> AllSwitches => SourceSwitches.Concat(PrivacySwitches);

    /// <summary>The modes the CLI accepts.</summary>
    public IReadOnlyList<string> Modes => AiDiscoveryTuning.Modes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeNote), nameof(ModeChangeText))]
    private string _mode = AiDiscoveryTuning.EnhancedMode;

    public string ModeNote => Mode == AiDiscoveryTuning.PassiveMode
        ? "Passive looks at the known places and the folders you list. A folder that is your whole home (~) is not walked for model files."
        : "Enhanced also walks broader places, such as your home folder, to find models that applications download where the signature catalog does not know to look.";

    /// <summary>"was enhanced": shown beside the mode when it differs from config.yaml's.</summary>
    public string ModeChangeText => Mode != _original.Mode ? $"was {_original.Mode}" : string.Empty;

    /// <summary>AI discovery is on (after the review): the other switches matter only while it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnabledText), nameof(TuningActive), nameof(EnabledChangeText), nameof(TuningOffNote), nameof(ShowTuningOffNote))]
    private bool _enabled;

    public string EnabledText => Enabled ? "AI Discovery is on" : "AI Discovery is off";

    public string EnabledChangeText => Enabled != _original.Enabled ? (_original.Enabled ? "was on" : "was off") : string.Empty;

    /// <summary>The tuning boxes apply: discovery is, or is being turned, on.</summary>
    public bool TuningActive => Enabled;

    public bool ShowTuningOffNote => !Enabled;

    public string TuningOffNote => _original.Enabled
        ? "These settings are not used while it is off, and turning it off does not send them. Turn it on to change them."
        : "These settings take effect once AI Discovery is on. Turn it on to change them; the command then carries what you changed.";

    /// <summary>The TUI's own line for the state today: <c>AI discovery: on  ·  Mode: enhanced</c>.</summary>
    public string StateLine => $"AI discovery: {(_original.Enabled ? "on" : "off")}  ·  Mode: {_original.Mode}";

    [ObservableProperty]
    private bool _restartAfter = true;

    [ObservableProperty]
    private bool _scanAfter = true;

    /// <summary>True when the installed CLI's help lists <c>--no-restart</c> on the verb it would run: the box can be unticked.</summary>
    [ObservableProperty]
    private bool _canSkipRestart = true;

    [ObservableProperty]
    private bool _canSkipScan = true;

    /// <summary>The CLI lists <c>disable</c> with the flags this dialog sends: discovery can be turned off from here.</summary>
    [ObservableProperty]
    private bool _canDisable = true;

    public string RestartNote => !RestartAfter
        ? "The gateway is not restarted, so nothing changes until it is restarted; and with no service to answer, no scan is asked for."
        : "The discovery service reads these settings only when the gateway starts, so the change restarts it.";

    partial void OnEnabledChanged(bool value) => Recompute();

    partial void OnModeChanged(string value) => Recompute();

    partial void OnRestartAfterChanged(bool value)
    {
        OnPropertyChanged(nameof(RestartNote));
        Recompute();
    }

    partial void OnScanAfterChanged(bool value) => Recompute();

    private void OnEdited(object? sender, EventArgs e) => Recompute();

    // ------------------------------------------------------------------ seeding

    /// <summary>
    /// Starts the form from config.yaml as the app holds it (the runtime's defaults for a key it does not name).
    /// <paramref name="keepEdits"/> keeps what the operator typed, measured against the file as it is now.
    /// </summary>
    protected override void Seed(bool keepEdits)
    {
        var typed = keepEdits ? Current() : null;
        _suspend = true;
        try
        {
            _original = AiDiscoverySettings.Read(Services.Config);
            var shown = typed ?? _original;

            Enabled = shown.Enabled;
            Mode = shown.Mode;
            ScanInterval.Text = Number(shown.ScanIntervalMin);
            ProcessInterval.Text = Number(shown.ProcessIntervalS);
            ScanRoots.Text = string.Join(Environment.NewLine, shown.ScanRoots);
            MaxFiles.Text = Number(shown.MaxFilesPerScan);
            MaxBytes.Text = Number(shown.MaxFileBytes);
            SetSwitch("include_shell_history", _original.IncludeShellHistory, shown.IncludeShellHistory);
            SetSwitch("include_package_manifests", _original.IncludePackageManifests, shown.IncludePackageManifests);
            SetSwitch("include_env_var_names", _original.IncludeEnvVarNames, shown.IncludeEnvVarNames);
            SetSwitch("include_network_domains", _original.IncludeNetworkDomains, shown.IncludeNetworkDomains);
            SetSwitch("allow_workspace_signatures", _original.AllowWorkspaceSignatures, shown.AllowWorkspaceSignatures);
            SetSwitch("store_raw_local_paths", _original.StoreRawLocalPaths, shown.StoreRawLocalPaths);
        }
        finally
        {
            _suspend = false;
        }

        OnPropertyChanged(nameof(StateLine));
        OnPropertyChanged(nameof(ModeChangeText));
        OnPropertyChanged(nameof(EnabledChangeText));
        OnPropertyChanged(nameof(TuningOffNote));
        ApplyListedFlags();

        // The settings were read from the file as it is now: note its signature, so a change to it after this is noticed (CUST-312).
        BeginTrustRead();
        Recompute();
    }

    private bool _suspend;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A switch is rebuilt as a new original and an edited value: its <see cref="TuningSwitch.Original"/> is read-only, so the switch is replaced.</summary>
    private void SetSwitch(string key, bool original, bool value)
    {
        var toggle = AllSwitches.Single(t => t.Key == key);
        toggle.Reset(original, value);
    }

    // ------------------------------------------------------------------ what the form says

    /// <summary>
    /// The settings the form describes, the typed values read the way the CLI will read them; a box that is not a whole number leaves that
    /// setting at what config.yaml says (and says why, beside the box: <see cref="Recompute"/>).
    /// </summary>
    private AiDiscoverySettings Current()
    {
        static int WholeOr(string text, int fallback) =>
            int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

        var roots = ScanRoots.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var bytes = long.TryParse(MaxBytes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : _original.MaxFileBytes;

        return new AiDiscoverySettings(
            Enabled: Enabled,
            Mode: Mode,
            ScanIntervalMin: WholeOr(ScanInterval.Text, _original.ScanIntervalMin),
            ProcessIntervalS: WholeOr(ProcessInterval.Text, _original.ProcessIntervalS),
            ScanRoots: roots,
            MaxFilesPerScan: WholeOr(MaxFiles.Text, _original.MaxFilesPerScan),
            MaxFileBytes: bytes,
            IncludeShellHistory: Value("include_shell_history"),
            IncludePackageManifests: Value("include_package_manifests"),
            IncludeEnvVarNames: Value("include_env_var_names"),
            IncludeNetworkDomains: Value("include_network_domains"),
            AllowWorkspaceSignatures: Value("allow_workspace_signatures"),
            StoreRawLocalPaths: Value("store_raw_local_paths"));

        bool Value(string key) => AllSwitches.Single(t => t.Key == key).IsOn;
    }

    /// <summary>What is wrong with the typed values: a box that is not a whole number, then what the CLI would refuse (ranges, folders).</summary>
    private Dictionary<string, string> ProblemsOf(AiDiscoverySettings settings)
    {
        var problems = new Dictionary<string, string>(AiDiscoveryTuning.Problems(settings), StringComparer.Ordinal);
        foreach (var (field, range) in new[]
        {
            (ScanInterval, (long)AiDiscoveryTuning.ScanIntervalRange.Min),
            (ProcessInterval, AiDiscoveryTuning.ProcessIntervalRange.Min),
            (MaxFiles, AiDiscoveryTuning.MaxFilesRange.Min),
            (MaxBytes, AiDiscoveryTuning.MaxFileBytesRange.Min),
        })
        {
            if (!long.TryParse(field.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                _ = range;
                problems[field.Key] = field.Label + " must be a whole number.";
            }
        }

        return problems;
    }

    /// <summary>The changes the form describes, the problems with it, the command that results, and the buttons that follow.</summary>
    private void Recompute()
    {
        if (_suspend)
        {
            return;
        }

        var current = Current();
        _problems = ProblemsOf(current);
        foreach (var field in Fields)
        {
            field.Problem = Enabled && _problems.TryGetValue(field.Key, out var problem) ? problem : string.Empty;
        }

        ModeProblem = Enabled && _problems.TryGetValue("mode", out var modeProblem) ? modeProblem : string.Empty;

        // The settings of a discovery that is off are not sent; and what the installed CLI does not list is never sent.
        var changes = AiDiscoveryTuning.Diff(_original, current).Where(c => IsListed(c.Flag)).ToArray();
        _pending.Clear();
        _pending.AddRange(Enabled ? changes : Array.Empty<TuningChange>());

        foreach (var field in Fields)
        {
            field.ChangeText = _pending.FirstOrDefault(c => c.Key == field.Key) is { } change ? "was " + Shorten(change.Before) : string.Empty;
        }

        ChangeLines.Clear();
        if (Enabled != _original.Enabled)
        {
            ChangeLines.Add(Enabled ? "AI Discovery: off to on" : "AI Discovery: on to off");
        }

        foreach (var change in _pending)
        {
            ChangeLines.Add(change.Describe());
        }

        var argv = Rollout(out var rollout) ? AiDiscoveryTuning.Argv(_original, current, _pending, rollout) : null;
        CommandPreview = argv is null ? string.Empty : CommandReview.CommandLine(CommandReview.DefaultExecutable, argv);
        HasChanges = argv is not null;
        OnPropertyChanged(nameof(ModeChangeText));
        OnPropertyChanged(nameof(EnabledChangeText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasProblems));
        NotifyActions();
    }

    private TuningRollout RolloutValue => new(RestartAfter, ScanAfter);

    private bool Rollout(out TuningRollout rollout)
    {
        rollout = RolloutValue;
        return true;
    }

    private static string Shorten(string value) => value.Length <= 28 ? value : value[..27] + "…";

    public ObservableCollection<string> ChangeLines { get; } = new();

    /// <summary>The problem with the mode box, or empty.</summary>
    [ObservableProperty]
    private string _modeProblem = string.Empty;

    /// <summary>The command that the form describes, exactly as the review will show it; empty when there is nothing to apply.</summary>
    [ObservableProperty]
    private string _commandPreview = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _hasChanges;

    /// <summary>Something in the form is out of range or not a number (while discovery is on).</summary>
    public bool HasProblems => Enabled && _problems.Count > 0;

    /// <summary>"Nothing to apply", "Turn on AI Discovery", "Turn off AI Discovery", or how many settings change.</summary>
    public string Summary
    {
        get
        {
            if (!HasChanges)
            {
                return "Nothing to apply";
            }

            if (!Enabled)
            {
                return "Turn off AI Discovery";
            }

            if (!_original.Enabled)
            {
                return _pending.Count == 0 ? "Turn on AI Discovery" : $"Turn on AI Discovery with {Plural(_pending.Count)}";
            }

            return Plural(_pending.Count) + " to apply";
        }
    }

    private static string Plural(int count) =>
        count == 1 ? "1 change" : count.ToString(CultureInfo.CurrentCulture) + " changes";

    // ------------------------------------------------------------------ the CLI's help

    protected override async Task<HelpProbeResult> ReadHelpAsync(CancellationToken cancellationToken)
    {
        var enable = HelpReader(EnablePath, cancellationToken);
        var disable = HelpReader(DisablePath, cancellationToken);

        HelpProbeResult enableResult;
        try
        {
            enableResult = await enable.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            enableResult = new HelpProbeResult(string.Empty, ex.Message);
        }

        try
        {
            _disableHelp = await disable.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _disableHelp = new HelpProbeResult(string.Empty, ex.Message);
        }

        return enableResult;
    }

    /// <summary>
    /// Checks <c>agent discovery enable --help</c>: <c>--yes</c> must be an option (without it the verb stops at a question this app cannot
    /// answer), and every other flag the dialog could send is offered only if listed. <c>disable</c>'s help says whether it can be turned off from here.
    /// </summary>
    protected override string? ApplyHelp(HelpProbeResult result)
    {
        _enableFlags = null;
        _disableFlags = null;

        if (!result.Succeeded)
        {
            return "The options of agent discovery enable could not be read (" + result.Error + "), so the settings cannot be checked against this DefenseClaw. Press Refresh to try again.";
        }

        var help = SetupHelpParser.Parse(result.Text, commandDepth: 3);
        if (!help.Lists("--yes"))
        {
            return "The installed DefenseClaw's agent discovery enable has no --yes, so it would stop at a question this app cannot answer. Nothing can be sent from here.";
        }

        _enableFlags = FlagsOf(help);
        CanSkipRestart = help.Lists("--no-restart");
        CanSkipScan = help.Lists("--no-scan");

        if (_disableHelp.Succeeded)
        {
            var disable = SetupHelpParser.Parse(_disableHelp.Text, commandDepth: 3);
            _disableFlags = FlagsOf(disable);
            CanDisable = disable.Lists("--yes");
        }
        else
        {
            CanDisable = false;
        }

        return null;
    }

    private static HashSet<string> FlagsOf(ParsedHelp help)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in help.Options)
        {
            foreach (var name in option.Names)
            {
                _ = flags.Add(name);
            }

            if (option.NegativeFlag is { Length: > 0 } negative)
            {
                _ = flags.Add(negative);
            }
        }

        return flags;
    }

    /// <summary>True when the CLI's help lists <paramref name="flag"/>; before the help is read, everything is assumed listed (the plan stays off until it is).</summary>
    private bool IsListed(string flag) => _enableFlags is null || _enableFlags.Contains(flag);

    private void ApplyListedFlags()
    {
        foreach (var field in Fields)
        {
            field.IsAvailable = IsListed(field.Flag);
        }

        foreach (var toggle in AllSwitches)
        {
            toggle.IsAvailable = IsListed(toggle.Flag);
        }
    }

    protected override void HelpChecked()
    {
        ApplyListedFlags();
        OnPropertyChanged(nameof(CanTurnOff));
        Recompute();
    }

    /// <summary>The "AI Discovery is on" switch can be turned off: discovery is on and the installed CLI's <c>disable</c> takes the flags this dialog sends.</summary>
    public bool CanTurnOff => !_original.Enabled || CanDisable;

    /// <summary>The on/off switch can be moved: the form can be edited and, for a discovery that is on, the CLI can turn it off.</summary>
    public bool CanToggleEnabled => CanEdit && CanTurnOff;

    public bool CanChooseScan => RestartAfter && CanSkipScan;

    public bool HasEnabledChange => EnabledChangeText.Length > 0;

    public bool HasModeChange => ModeChangeText.Length > 0;

    public bool HasCommandPreview => CommandPreview.Length > 0;

    // ------------------------------------------------------------------ what is on and off

    protected override void NotifyActions()
    {
        RaiseGuardState();
        OnPropertyChanged(nameof(ReviewTip));
        OnPropertyChanged(nameof(ReviewBlocked));
        OnPropertyChanged(nameof(CanTurnOff));
        OnPropertyChanged(nameof(CanToggleEnabled));
        OnPropertyChanged(nameof(CanChooseScan));
        OnPropertyChanged(nameof(HasEnabledChange));
        OnPropertyChanged(nameof(HasModeChange));
        OnPropertyChanged(nameof(HasCommandPreview));
        ReviewChangesCommand.NotifyCanExecuteChanged();
    }

    protected override void OnDisposing()
    {
        foreach (var field in Fields)
        {
            field.Edited -= OnEdited;
        }

        foreach (var toggle in AllSwitches)
        {
            toggle.Moved -= OnEdited;
        }
    }

    /// <summary>
    /// Why "Review" is off, or null when it is on. In this order, so a control says one sentence and the first is the one that matters: the
    /// installation, the unreadable file, the settings' age or a change to config.yaml since, the CLI's help, another command, a value the CLI
    /// would refuse, turning it off with a CLI that has no <c>disable</c> for it, and last, nothing to apply.
    /// </summary>
    public string? ReviewBlockedReason
    {
        get
        {
            if (ChangesBlockedReason is { } reason)
            {
                return reason;
            }

            if (IsChecking)
            {
                return "Checking this DefenseClaw's options…";
            }

            if (HasHelpProblem)
            {
                return HelpProblem;
            }

            if (Review.IsOpen || Review.IsRunning)
            {
                return "Another command is running.";
            }

            if (HasProblems)
            {
                return "Fix the values marked above first: " + _problems.OrderBy(static p => p.Key, StringComparer.Ordinal).First().Value;
            }

            if (_original.Enabled && !Enabled && !CanDisable)
            {
                return "The installed DefenseClaw's agent discovery disable cannot be used from here (its help does not list --yes), so AI Discovery cannot be turned off.";
            }

            return HasChanges ? null : "Nothing to apply: nothing differs from config.yaml.";
        }
    }

    public bool ReviewBlocked => ReviewBlockedReason is not null;

    public string ReviewTip => ReviewBlockedReason ?? "Shows the exact command first. Nothing runs until you confirm.";

    private bool CanReview() => ReviewBlockedReason is null;

    // ------------------------------------------------------------------ the plan

    /// <summary>
    /// The plan for the form as it is: one step, the command of <see cref="AiDiscoveryTuning.Argv(AiDiscoverySettings, AiDiscoverySettings, IReadOnlyList{TuningChange}, TuningRollout)"/>,
    /// and the sentences that say what running it does. Null when nothing changed.
    /// </summary>
    internal SetupPlan? BuildPlan()
    {
        var current = Current();
        var argv = AiDiscoveryTuning.Argv(_original, current, _pending, RolloutValue);
        if (argv is null)
        {
            return null;
        }

        _planArgv = argv;
        _planChanges = _pending.ToArray();
        _planFrom = _original;
        _planTo = current;

        var turningOn = !_original.Enabled;
        var turningOff = !current.Enabled;
        var restart = RestartAfter;
        var changes = string.Join("; ", _pending.Select(static c => c.Describe()));

        string title;
        string summary;
        string purpose;
        string primary;
        if (turningOff)
        {
            title = "Turn off AI Discovery?";
            summary = "Sets ai_discovery.enabled to false in config.yaml. " +
                      (restart
                          ? "It then restarts the gateway so the discovery service stops. Components already found stay listed until you clear them."
                          : "The gateway is not restarted, so it keeps scanning until it is restarted.");
            purpose = "Disable the sidecar AI discovery service.";
            primary = "Turn off";
        }
        else if (turningOn)
        {
            title = "Turn on AI Discovery?";
            summary = "Sets ai_discovery.enabled to true in config.yaml" +
                      (_pending.Count > 0 ? $" and changes {Plural(_pending.Count)}: {changes}. " : " and leaves your other AI discovery settings as they are. ") +
                      (restart
                          ? "It then restarts the gateway so it starts the discovery service" + (ScanAfter ? ", and asks it for a first scan." : ".")
                          : "The gateway is not restarted, so nothing starts scanning until it is restarted.");
            purpose = "Enable the sidecar AI discovery service" + (_pending.Count > 0 ? " with the settings above." : ".");
            primary = "Turn on";
        }
        else
        {
            title = "Change AI Discovery settings?";
            summary = $"Sets, in the ai_discovery block of config.yaml: {changes}.Everything else stays as it is. " +
                      (restart
                          ? "The discovery service reads these only when it starts, so the gateway restarts" + (ScanAfter ? ", and the new settings are used for an immediate scan." : ".")
                          : "The gateway is not restarted, so the new settings are saved but not in effect until it next restarts.");
            purpose = "Apply the changed settings with agent discovery enable (it updates a discovery that is already on).";
            primary = "Apply changes";
        }

        var warnings = new List<CommandReviewWarning>();
        if (!restart && !turningOff)
        {
            warnings.Add(new CommandReviewWarning(
                "Gateway not restarted",
                "The gateway is not restarted, so the discovery service keeps its old settings until it next restarts, and no scan is asked for."));
        }

        if (!turningOff)
        {
            var stricter = new List<string>();
            if (current.StoreRawLocalPaths && !_original.StoreRawLocalPaths)
            {
                warnings.Add(new CommandReviewWarning(
                    "Stores raw local paths",
                    "Raw local paths will be kept in the discovery state file instead of hashed fingerprints only. The CLI says to turn this on only for diagnostics on a trusted machine."));
            }

            if (current.AllowWorkspaceSignatures && !_original.AllowWorkspaceSignatures)
            {
                warnings.Add(new CommandReviewWarning(
                    "Workspace signatures",
                    "Signature packs found inside the workspaces it scans will be honoured, so a workspace can change what is detected. Off by default for that reason."));
            }

            foreach (var (label, was, now) in new[]
            {
                ("shell history", _original.IncludeShellHistory, current.IncludeShellHistory),
                ("package manifests", _original.IncludePackageManifests, current.IncludePackageManifests),
                ("environment variable names", _original.IncludeEnvVarNames, current.IncludeEnvVarNames),
                ("network domains", _original.IncludeNetworkDomains, current.IncludeNetworkDomains),
            })
            {
                if (was && !now)
                {
                    stricter.Add(label);
                }
            }

            if (stricter.Count > 0)
            {
                warnings.Add(new CommandReviewWarning(
                    "Looks at less",
                    "Turned off, so discovery cannot find what only these would show: " + string.Join(", ", stricter) + "."));
            }
        }

        return new SetupPlan
        {
            Title = title,
            Summary = summary,
            Steps = new[] { new DiscoverStep(argv, purpose, CommandTier.StateChanging, TimeSpan.FromMinutes(5)) },
            RestartsGateway = restart,
            Warnings = warnings,
            PrimaryText = primary,
        };
    }

    /// <summary>Opens the review of the command. Nothing runs from here: it starts when the operator confirms it.</summary>
    [RelayCommand(CanExecute = nameof(CanReview))]
    private void ReviewChanges()
    {
        // The look at the files again (CUST-312): a button that was on a minute ago must not authorize anything now.
        _ = Trust.CheckConfig();
        if (ReviewBlockedReason is not null)
        {
            NotifyActions();
            return;
        }

        var plan = BuildPlan();
        if (plan is null)
        {
            return;
        }

        ResultText = string.Empty;
        Review.OpenPlan(plan, AfterPlanAsync);
    }

    /// <summary>
    /// The command ran: read config.yaml again, start the form from what it says now, say what was applied, and let the panel read its own state.
    /// </summary>
    private async Task AfterPlanAsync(DiscoverReviewResult result)
    {
        var argv = _planArgv;
        var changes = _planChanges;
        var from = _planFrom;
        var to = _planTo;
        Services.ReloadConfig();
        Seed(keepEdits: false);

        ResultKey = result.Succeeded ? "Ok" : "Bad";
        if (result.Succeeded)
        {
            var parts = new List<string>();
            if (to.Enabled != from.Enabled)
            {
                parts.Add(to.Enabled ? "AI Discovery is on" : "AI Discovery is off");
            }

            parts.AddRange(changes.Select(static c => c.Describe()));
            ResultText = "Applied: " + string.Join("; ", parts) + ".";
        }
        else
        {
            ResultText = "Not applied. " + PlanReport.Describe(result.Outcomes) + " The review and the Activity panel have the output.";
            ResultText = ResultText.Replace("  ", " ", StringComparison.Ordinal).Trim();
        }

        if (Applied is { } applied)
        {
            await applied(result, argv).ConfigureAwait(true);
        }
    }
}
