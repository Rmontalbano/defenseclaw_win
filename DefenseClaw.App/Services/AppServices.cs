using System.Diagnostics;
using System.IO;
using System.Windows;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Inventory;
using DefenseClaw.Core.Logs;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Security;
using DefenseClaw.App.Services.Settings;

namespace DefenseClaw.App.Services;

/// <summary>
/// One gateway REST target: the port and the client bound to that same port, published
/// together so a reader can never pair the port from one config generation with the client
/// from another. See <see cref="AppServices.Endpoint"/>.
/// </summary>
internal sealed record GatewayEndpoint(int Port, GatewayClient Client);

/// <summary>
/// The composition root. Everything the shell and the panels need is constructed here
/// once, in dependency order, and handed out through properties.
/// <para>
/// This is deliberately a hand-rolled container rather than
/// <c>Microsoft.Extensions.DependencyInjection</c>: the object graph is a dozen
/// singletons with no lifetime scoping, so a package plus a service-descriptor DSL would
/// buy nothing and add a dependency to a tray app whose whole distribution story is a
/// single self-contained exe.
/// </para>
/// <para>
/// <b>Panel authors:</b> take this type in your view-model constructor and reach for the
/// property you need. Do not construct Core services yourself — the token registration
/// and secret scrubbing wired up here are load bearing.
/// </para>
/// </summary>
public sealed class AppServices : IDisposable
{
    /// <summary>
    /// How long a replaced <see cref="GatewayClient"/> stays alive before it is disposed.
    /// Requests already in flight on it are allowed to finish: the client's own request
    /// timeout is 10 s, so nothing can still be using it after this. Disposing it on the spot
    /// would turn those requests into <see cref="ObjectDisposedException"/>s.
    /// </summary>
    private static readonly TimeSpan RetiredClientGrace = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();

    /// <summary>
    /// Serializes <see cref="ReloadConfig"/>. The config watcher and its poll-timer backstop can
    /// both notice the same edit on different threads; without this two reloads could apply out
    /// of order and leave an older document (or the older port's client) in place.
    /// </summary>
    private readonly object _reloadGate = new();
    private ConfigDocument _config;
    private TokenResolution _token;
    private GatewayEndpoint _endpoint;
    private SecretValue? _registeredToken;
    private bool _disposed;

    /// <summary>
    /// The file reads a fresh composition starts with, possibly still running on a pool thread:
    /// see <see cref="BeginInitialize"/>.
    /// </summary>
    private sealed record StartupLoad(DefenseClawPaths Paths, Task<LoadedStartup> Work);

    /// <summary>What <see cref="StartupLoad"/> produces: the two readers and the first config state.</summary>
    private sealed record LoadedStartup(ConfigStore ConfigStore, TokenResolver TokenResolver, LoadedConfig Initial);

    private AppServices(StartupLoad startup, string? claudeSettingsPath = null, string? settingsPath = null)
    {
        Paths = startup.Paths;

        // Joins the config read. A no-op wait when it already finished while the caller was busy
        // theming; otherwise the same wait the inline read used to be. Only the readers'
        // construction can throw here (the read itself is guarded — see BeginLoad), and that
        // propagates exactly as it did when it ran in this constructor.
        var loaded = startup.Work.GetAwaiter().GetResult();
        ConfigStore = loaded.ConfigStore;
        TokenResolver = loaded.TokenResolver;

        var initial = loaded.Initial;
        _config = initial.Document;
        _token = initial.Token;
        ConfigLoadError = initial.Error;

        PortInspector = new PortOwnerInspector();
        _peerVerifier = new GatewayPeerVerifier(Paths, PortInspector);

        _endpoint = new GatewayEndpoint(
            _config.Config.Gateway.ApiPort,
            CreateGatewayClient(_config.Config.Gateway.ApiPort));

        // The detector's own client is only its default for the parameterless overloads; the
        // monitor always hands it the live endpoint's client (see GatewayMonitor.PollAsync), so
        // the port it inspects and the client it probes through cannot drift apart.
        InstallDetector = new InstallStateDetector(Paths, _endpoint.Client, PortInspector);

        Audit = new AuditReader(Paths.AuditDatabasePath);
        Inventory = new InventoryReader(Paths.InventoryDatabasePath);
        ClaudeSettings = new ClaudeSettingsReader(claudeSettingsPath);
        GatewayLog = new LogTailer(Paths.GatewayLogPath, new LogTailerOptions { StartAtEnd = true });
        WatchdogLog = new LogTailer(Paths.WatchdogLogPath, new LogTailerOptions { StartAtEnd = true });

        Cli = new CliRunner(Paths);

        // The token must be registered before anything can shell out: CliRunner then
        // refuses to place it in argv and scrubs it out of any captured output.
        RegisterTokenWithCli();
        RegisterConfiguredSecretsWithCli();

        ConfigWatcher = new ConfigChangeToken(Paths);
        ConfigWatcher.Changed += OnConfigChanged;

        Monitor = new GatewayMonitor(this);

        // The shared building blocks the panels and the shell build on (docs/PARITY-FOUNDATIONS.md). Nothing here starts work:
        // the settings file is read on first use, the counts service runs only while something listens to it, and the scope
        // follows the monitor it is handed.
        Settings = AppSettingsStore.ForPath(settingsPath);
        Navigation = new ShellNavigation();
        AlertQueue = new AlertQueueReader(Paths.AuditDatabasePath);
        ConnectorScope = new ConnectorScope(Monitor);
        AlertCounts = new AlertCountsService(AlertQueue, Monitor);
    }

    /// <summary>The single instance, created by <see cref="Initialize"/> at startup.</summary>
    public static AppServices Current =>
        Instance ?? throw new InvalidOperationException("AppServices.Initialize() has not run yet.");

    private static AppServices? Instance { get; set; }

    public DefenseClawPaths Paths { get; }

    public ConfigStore ConfigStore { get; }

    public TokenResolver TokenResolver { get; }

    /// <summary>
    /// The REST client for the port config.yaml names <i>right now</i>. Follows
    /// <c>gateway.api_port</c> across config reloads: the instance behind this property is
    /// replaced when the port changes, so read it per call rather than caching it in a field
    /// (every existing caller does). A caller that needs the port and the client to agree
    /// reads <see cref="Endpoint"/> once instead.
    /// </summary>
    public GatewayClient Gateway => Endpoint.Client;

    public AuditReader Audit { get; }

    public InventoryReader Inventory { get; }

    /// <summary>Read-only view of Claude Code's settings.json; the app never writes it.</summary>
    public ClaudeSettingsReader ClaudeSettings { get; }

    /// <summary>Tail of <c>gateway.log</c>. Not started — the Logs panel owns the lifecycle.</summary>
    public LogTailer GatewayLog { get; }

    /// <summary>Tail of <c>watchdog.log</c>. Not started — the Logs panel owns the lifecycle.</summary>
    public LogTailer WatchdogLog { get; }

    public CliRunner Cli { get; }

    public InstallStateDetector InstallDetector { get; }

    public PortOwnerInspector PortInspector { get; }

    public ConfigChangeToken ConfigWatcher { get; }

    /// <summary>The one place UI state comes from. See <see cref="GatewayMonitor"/>.</summary>
    public GatewayMonitor Monitor { get; }

    /// <summary>
    /// The app's own settings, <c>%LOCALAPPDATA%\DefenseClaw.App\settings.json</c>: appearance, monitoring, notifications, startup,
    /// connection and updates. <c>Settings.Current</c> reads, <c>Settings.Update(s =&gt; s with { … })</c> changes,
    /// <c>Settings.Changed</c> announces. See <see cref="Settings.AppSettingsStore"/>.
    /// </summary>
    internal AppSettingsStore Settings { get; }

    /// <summary>The inbox for "show this panel, and tell it this" requests (deep links). See <see cref="ShellNavigation"/>.</summary>
    public ShellNavigation Navigation { get; }

    /// <summary>The alert queue read straight from <c>audit.db</c> (the Mac's "unacknowledged findings"). See <see cref="AlertQueueReader"/>; most callers want <see cref="AlertCounts"/>.</summary>
    public AlertQueueReader AlertQueue { get; }

    /// <summary>The kept-fresh counts over <see cref="AlertQueue"/>: subscribe to <c>Changed</c> for a badge, call <c>RefreshAsync</c> after an acknowledge. Idle while nothing subscribes.</summary>
    internal AlertCountsService AlertCounts { get; }

    /// <summary>The one connector filter every screen shares (All, or one connector). See <see cref="Services.ConnectorScope"/>.</summary>
    internal ConnectorScope ConnectorScope { get; }

    /// <summary>
    /// REST port the <see cref="Gateway"/> client is currently built against. Tracks
    /// <c>gateway.api_port</c>: it changes when a config reload changes the port.
    /// </summary>
    public int ApiPort => Endpoint.Port;

    /// <summary>
    /// The current port and the client bound to it, read atomically. What a poll uses so that
    /// the port it inspects for an owner and the client it probes through are the same target
    /// even when a reload swaps the endpoint mid-poll.
    /// </summary>
    internal GatewayEndpoint Endpoint
    {
        get
        {
            lock (_gate)
            {
                return _endpoint;
            }
        }
    }

    /// <summary>
    /// Set when config.yaml exists but could not be parsed or applied; null otherwise. When a
    /// reload fails the previously loaded configuration stays in effect and the text says so.
    /// </summary>
    public string? ConfigLoadError { get; private set; }

    /// <summary>Latest parsed config.yaml. Replaced wholesale when the file changes.</summary>
    public ConfigDocument Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    /// <summary>Which rung of the token ladder answered. Never carries the plaintext.</summary>
    public TokenResolution Token
    {
        get
        {
            lock (_gate)
            {
                return _token;
            }
        }
    }

    /// <summary>
    /// Raised after config.yaml or .env changed and the token was re-resolved.
    /// <para>
    /// <b>Always raised on the UI thread</b> when there is one — see
    /// <see cref="RaiseConfigReloaded"/>. Subscribers may mutate bound collections and
    /// view-model properties directly, exactly as they can from
    /// <c>GatewayMonitor.StateChanged</c>.
    /// </para>
    /// <para>
    /// Raised after every reload attempt, <i>including a failed one</i>: a failure changes
    /// <see cref="ConfigLoadError"/>, and the banner that shows it listens here.
    /// </para>
    /// </summary>
    public event EventHandler? ConfigReloaded;

    /// <summary>
    /// Starts the part of <see cref="Initialize"/> that is file I/O — config.yaml, its YAML parse
    /// and the token ladder (.env) — on a pool thread, so the caller can do UI-thread work while it
    /// runs. <see cref="Initialize"/> then joins it. Optional: without this call
    /// <see cref="Initialize"/> does the same reads inline. Idempotent, and a no-op once the
    /// singleton exists. UI thread only, like <see cref="Initialize"/>.
    /// </summary>
    public static void BeginInitialize() => BeginInitialize(new DefenseClawPaths());

    /// <summary>The same, over injected <paramref name="paths"/>; what harnesses use to keep clear of the real data directory.</summary>
    internal static void BeginInitialize(DefenseClawPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (Instance is null)
        {
            _pendingStartup ??= BeginLoad(paths, onPoolThread: true);
        }
    }

    public static AppServices Initialize()
    {
        if (Instance is null)
        {
            var startup = _pendingStartup ?? BeginLoad(new DefenseClawPaths(), onPoolThread: false);
            _pendingStartup = null;
            Instance = new AppServices(startup);
        }

        return Instance;
    }

    private static StartupLoad? _pendingStartup;

    /// <summary>
    /// Builds the config store and token resolver and reads config.yaml and the token through
    /// them, inline or on a pool thread.
    /// <para>
    /// The read never throws: startup has no "last good config" to fall back on, and an exception
    /// here happens before the tray exists, so the process would just vanish. Whatever goes wrong,
    /// the app comes up on defaults and the config-error banner says why — the same outcome
    /// whichever thread ran it, because the failure is captured into the result, not the task.
    /// </para>
    /// </summary>
    private static StartupLoad BeginLoad(DefenseClawPaths paths, bool onPoolThread)
    {
        LoadedStartup Read()
        {
            var store = new ConfigStore(paths);
            var resolver = new TokenResolver(paths.EnvFilePath);
            return new LoadedStartup(store, resolver, LoadConfigStateGuarded(store, resolver, lastGood: null));
        }

        return new StartupLoad(paths, onPoolThread ? Task.Run(Read) : Task.FromResult(Read()));
    }

    /// <summary>
    /// Test seam: a composition over injected <paramref name="paths"/> that is <b>not</b> the
    /// process singleton (<see cref="Current"/> is untouched), so view-models can be built against a
    /// scratch data directory and a runner that resolves no executable. Production code uses
    /// <see cref="Initialize"/>, whose defaults are unchanged.
    /// </summary>
    internal static AppServices CreateIsolated(
        DefenseClawPaths paths,
        string? claudeSettingsPath = null,
        bool readConfigOnPoolThread = false,
        string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // The app's own settings file lives under %LOCALAPPDATA%, not in the data directory, so an isolated composition that said
        // nothing about it would read and write the real one. It gets a file inside the scratch directory instead.
        settingsPath ??= Path.Combine(paths.DataDirectory, "DefenseClaw.App", "settings.json");
        return new AppServices(BeginLoad(paths, readConfigOnPoolThread), claudeSettingsPath, settingsPath);
    }

    /// <summary>Token provider handed to <see cref="GatewayClient"/>; re-read per request.</summary>
    public SecretValue? CurrentToken() => Token.Token;

    /// <summary>
    /// Decides whether the process on the API port is the gateway the token may go to. Assigned
    /// before the first client is built.
    /// </summary>
    private readonly GatewayPeerVerifier _peerVerifier;

    /// <summary>
    /// A client for <paramref name="port"/> that sends the bearer token only to the DefenseClaw
    /// gateway from the install directory — see <see cref="GatewayPeerVerifier"/>.
    /// </summary>
    private GatewayClient CreateGatewayClient(int port) =>
        GatewayClient.Create(port, CurrentToken, verifyPeer: _peerVerifier.ForPort(port));

    /// <summary>
    /// Re-reads config.yaml and the .env file, then re-resolves the token ladder, and
    /// re-targets the gateway client when <c>gateway.api_port</c> moved.
    /// <para>
    /// The reading, parsing and hashing all happen on the calling thread — which for the
    /// watcher-driven path is a thread-pool or <c>FileSystemWatcher</c> thread, and must
    /// stay that way: this is file I/O plus a YAML parse, and the UI thread has no business
    /// doing it. Only the <see cref="ConfigReloaded"/> notification is marshalled.
    /// </para>
    /// <para>
    /// <b>Never throws.</b> That thread has nothing above it to catch, so an escaped exception
    /// (a null section from a half-edited file, a locked .env, anything) would take the whole
    /// tray app down. Any failure keeps the last good configuration, the port and the token
    /// exactly as they were, is traced, and is surfaced through <see cref="ConfigLoadError"/>
    /// and <see cref="ConfigReloaded"/> like any other unreadable config.
    /// </para>
    /// <para>
    /// A port change swaps in a new <see cref="GatewayClient"/> (the old one is disposed after
    /// <see cref="RetiredClientGrace"/> so requests already on it finish). The token needs no
    /// such handling: clients read it through <see cref="CurrentToken"/> per request.
    /// </para>
    /// </summary>
    public void ReloadConfig()
    {
        GatewayClient? retired = null;

        lock (_reloadGate)
        {
            try
            {
                ConfigDocument lastGood;
                lock (_gate)
                {
                    lastGood = _config;
                }

                var state = LoadConfigState(ConfigStore, TokenResolver, lastGood);
                var port = state.Document.Config.Gateway.ApiPort;

                // Built before anything is published, so a failure here (it cannot really
                // happen — the port getter clamps to 1..65535 — but "cannot" is how this class
                // of bug gets written) leaves the old client, config and token all in place.
                GatewayClient? fresh = port == _endpoint.Port
                    ? null
                    : CreateGatewayClient(port);

                lock (_gate)
                {
                    _config = state.Document;
                    _token = state.Token;
                    ConfigLoadError = state.Error;

                    if (fresh is not null)
                    {
                        retired = _endpoint.Client;
                        _endpoint = new GatewayEndpoint(port, fresh);
                    }
                }
            }
#pragma warning disable CA1031 // The watcher thread has no handler above this one; see the method doc.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"config reload failed; keeping the last good configuration: {ex}");

                lock (_gate)
                {
                    ConfigLoadError =
                        $"config.yaml was read but could not be applied ({ex.GetType().Name}: {ex.Message}). " +
                        "The last good configuration is still in use.";
                }
            }
        }

        if (retired is not null)
        {
            RetireClient(retired);
        }

        // The port may have changed and the token may have rotated; neither is optional to
        // register, and RegisterTokenWithCli is idempotent per distinct value.
        try
        {
            RegisterTokenWithCli();
        }
#pragma warning disable CA1031 // Same reason as above: an exception here would end the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"could not register the reloaded token with the CLI runner: {ex}");
        }

        // .env or a *_env key may have changed along with the config.
        RegisterConfiguredSecretsWithCli();

        RaiseConfigReloaded();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Children go first. Stopping them is the one step here with a real wait (bounded — see
        // CliRunner.Shutdown), and everything below is quicker and safer to tear down once no
        // CLI run can still report into it. The upgrade installer/resolver is exempt inside the
        // runner and keeps going; everything else in flight is killed, process tree and all.
        // App.OnExit has usually already called Shutdown, in which case this is a no-op.
        Cli.Dispose();

        ConfigWatcher.Changed -= OnConfigChanged;
        AlertCounts.Dispose();
        ConnectorScope.Dispose();
        Monitor.Dispose();
        ConfigWatcher.Dispose();
        GatewayLog.Dispose();
        WatchdogLog.Dispose();
        Endpoint.Client.Dispose();

        // Only the process singleton clears the slot; an isolated instance (see CreateIsolated)
        // must not unseat it.
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    /// <summary>
    /// The App side of <see cref="ConfigChangeToken.Changed"/>. Runs on the watcher's or
    /// the poll timer's thread; <see cref="ReloadConfig"/> is idempotent, which is what
    /// that event's contract requires of its handlers.
    /// </summary>
    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e) => ReloadConfig();

    /// <summary>
    /// Disposes a replaced client once nothing can still be using it. A poll that started
    /// before the swap holds the old client for at most one request timeout; see
    /// <see cref="RetiredClientGrace"/>. Fire and forget by design — losing the timer at
    /// process exit just means the OS reclaims the socket a moment earlier.
    /// </summary>
    private static void RetireClient(GatewayClient client) =>
        _ = Task.Delay(RetiredClientGrace).ContinueWith(
            _ => client.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    /// <summary>
    /// Marshals <see cref="ConfigReloaded"/> onto the UI thread.
    /// <para>
    /// <b>Why this is the boundary.</b> <see cref="ConfigChangeToken"/> lives in Core and
    /// stays thread-agnostic — it raises on whichever thread spotted the file edit. Its
    /// only App-side subscriber is this class, and every downstream consumer is a
    /// view-model: <c>OverviewPanelViewModel.OnConfigReloaded</c> rebuilds the bound
    /// <c>ObservableCollection&lt;AttentionRow&gt;</c>, <c>MainWindowViewModel</c> sets
    /// banner properties. Marshalling once here makes every current and future subscriber
    /// safe by construction, rather than leaving each one to remember.
    /// </para>
    /// <para>
    /// <b>Why the Dispatcher rather than a captured <see cref="SynchronizationContext"/>.</b>
    /// <c>GatewayMonitor.Publish</c> captures a context because it has an explicit
    /// <c>Start()</c> that OnStartup calls last, on the UI thread, precisely so the capture
    /// is correct. This class has no such moment — it is constructed midway through
    /// composition and <c>Initialize()</c> is reachable from tests and tooling, where
    /// <c>SynchronizationContext.Current</c> is null or a pool context and a captured value
    /// would silently degrade back to raising on the watcher thread. Resolving
    /// <c>Application.Current.Dispatcher</c> at raise time cannot go stale, and the
    /// CheckAccess/BeginInvoke shape matches how the rest of the shell marshals
    /// (<c>UpgradeSectionViewModel.PostState</c>, the Activity, Logs and Setup panels,
    /// <c>App.OnActivationRequested</c>).
    /// </para>
    /// <para>
    /// A null dispatcher means no WPF application is running — headless tests — so the
    /// handler is invoked inline, preserving the synchronous behaviour those tests observe.
    /// </para>
    /// </summary>
    private void RaiseConfigReloaded()
    {
        var handler = ConfigReloaded;
        if (handler is null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            handler(this, EventArgs.Empty);
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            // Exiting: there is no UI left to update, and BeginInvoke would throw.
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(() => handler(this, EventArgs.Empty));
        }
        catch (InvalidOperationException)
        {
            // Shutdown began between the check above and the post. A dropped banner update
            // on the way out is fine; an exception on the watcher thread is not — nothing
            // above it can catch, so it would take the process down.
        }
    }

    /// <summary>
    /// Hands the resolved bearer token to the CLI runner so it can never reach argv or
    /// captured output. Registration is additive and idempotent per distinct value.
    /// </summary>
    private void RegisterTokenWithCli()
    {
        var token = Token.Token;
        if (token is null || token.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_registeredToken is not null && _registeredToken.Equals(token))
            {
                return;
            }

            _registeredToken = token;
        }

        Cli.RegisterSecret(token);
    }

    // ---- Other configured secrets (CUST-197 / D3-04) ---------------------------------------------------------

    /// <summary>The secrets from <see cref="RegisterConfiguredSecretsWithCli"/> the runner already knows, so a reload adds only what is new.</summary>
    private readonly List<SecretValue> _registeredConfiguredSecrets = new();

    /// <summary>
    /// Hands the runner every other secret DefenseClaw is configured with — the secret-shaped values in
    /// <c>~/.defenseclaw/.env</c> and the variables named by config <c>*_env</c> keys (see
    /// <see cref="ConfiguredSecrets"/>) — so it refuses them on a command line and masks them in captured
    /// output, as it already does for the gateway token. Read-only: the file is read, never written, and no value is
    /// traced. Additive and idempotent per distinct value, like <see cref="RegisterTokenWithCli"/>; a value that was
    /// rotated away stays registered for the life of the process, which only ever costs an extra refusal.
    /// <para>Never throws: it runs at startup and on the watcher thread, and a secret it could not register is no
    /// worse than one it was never asked about.</para>
    /// </summary>
    private void RegisterConfiguredSecretsWithCli()
    {
        try
        {
            var secrets = ConfiguredSecrets.Collect(
                Config.RawText,
                DotEnvFile.Load(Paths.EnvFilePath),
                ProcessEnvironmentReader.Instance);

            foreach (var secret in secrets)
            {
                lock (_gate)
                {
                    if (_registeredConfiguredSecrets.Contains(secret))
                    {
                        continue;
                    }

                    _registeredConfiguredSecrets.Add(secret);
                }

                Cli.RegisterSecret(secret);
            }
        }
#pragma warning disable CA1031 // Startup and the watcher thread have nothing above this to catch; see the method doc.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"could not register the configured secrets with the CLI runner: {ex.GetType().Name}");
        }
    }

    /// <summary>A parsed config, the token resolved from it, and the load error to surface (if any).</summary>
    private sealed record LoadedConfig(ConfigDocument Document, TokenResolution Token, string? Error);

    /// <summary>
    /// Loads config.yaml and resolves the token from it. May throw for anything the readers do
    /// not handle themselves — callers decide what "failed" means (startup falls back to
    /// defaults, a reload keeps the last good state).
    /// </summary>
    private static LoadedConfig LoadConfigState(ConfigStore store, TokenResolver resolver, ConfigDocument? lastGood)
    {
        var document = LoadConfigSafely(store, lastGood, out var error);
        var token = resolver.Resolve(document.Config);
        return new LoadedConfig(document, token, error);
    }

    /// <summary>
    /// <see cref="LoadConfigState"/> for the constructor: on any exception, returns defaults
    /// (an empty document, no token) with the error text, instead of letting startup die.
    /// </summary>
    private static LoadedConfig LoadConfigStateGuarded(ConfigStore store, TokenResolver resolver, ConfigDocument? lastGood)
    {
        try
        {
            return LoadConfigState(store, resolver, lastGood);
        }
#pragma warning disable CA1031 // Startup must survive a config it cannot use; the banner reports it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"config could not be loaded at startup; using defaults: {ex}");

            return new LoadedConfig(
                ConfigStore.Parse(string.Empty, store.ConfigFilePath),
                new TokenResolution(null, TokenSource.None, GatewaySection.DefaultTokenEnv),
                $"config.yaml was read but could not be applied ({ex.GetType().Name}: {ex.Message}). " +
                "Running on defaults until it is fixed.");
        }
    }

    /// <summary>
    /// Reads config.yaml, turning the two failures a user can cause (bad YAML, an unreadable
    /// file) into an error string. On such a failure the <paramref name="lastGood"/> document
    /// is kept — a config caught mid-edit must not reset the port, the token variable and the
    /// connector list to defaults for as long as the file is broken — and the error says so.
    /// With no last good document (startup) the result is an empty, all-defaults one.
    /// </summary>
    private static ConfigDocument LoadConfigSafely(ConfigStore store, ConfigDocument? lastGood, out string? error)
    {
        try
        {
            error = null;
            return store.Load();
        }
        catch (ConfigParseException ex)
        {
            error = ex.Message;
        }
        catch (IOException ex)
        {
            error = $"config.yaml could not be read: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"config.yaml could not be read: {ex.Message}";
        }

        if (lastGood is not null)
        {
            error += " The last good configuration is still in use.";
            return lastGood;
        }

        return ConfigStore.Parse(string.Empty, store.ConfigFilePath);
    }
}
