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

namespace DefenseClaw.App.Services;

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
    private readonly object _gate = new();
    private ConfigDocument _config;
    private TokenResolution _token;
    private SecretValue? _registeredToken;
    private bool _disposed;

    private AppServices()
    {
        Paths = new DefenseClawPaths();
        ConfigStore = new ConfigStore(Paths);
        _config = LoadConfigSafely(ConfigStore, out var configError);
        ConfigLoadError = configError;

        TokenResolver = new TokenResolver(Paths.EnvFilePath);
        _token = TokenResolver.Resolve(_config.Config);

        ApiPort = _config.Config.Gateway.ApiPort;
        Gateway = GatewayClient.Create(ApiPort, CurrentToken);

        PortInspector = new PortOwnerInspector();
        InstallDetector = new InstallStateDetector(Paths, Gateway, PortInspector);

        Audit = new AuditReader(Paths.AuditDatabasePath);
        Inventory = new InventoryReader(Paths.InventoryDatabasePath);
        ClaudeSettings = new ClaudeSettingsReader();
        GatewayLog = new LogTailer(Paths.GatewayLogPath, new LogTailerOptions { StartAtEnd = true });
        WatchdogLog = new LogTailer(Paths.WatchdogLogPath, new LogTailerOptions { StartAtEnd = true });

        Cli = new CliRunner(Paths);

        // The token must be registered before anything can shell out: CliRunner then
        // refuses to place it in argv and scrubs it out of any captured output.
        RegisterTokenWithCli();

        ConfigWatcher = new ConfigChangeToken(Paths);
        ConfigWatcher.Changed += OnConfigChanged;

        Monitor = new GatewayMonitor(this);
    }

    /// <summary>The single instance, created by <see cref="Initialize"/> at startup.</summary>
    public static AppServices Current =>
        Instance ?? throw new InvalidOperationException("AppServices.Initialize() has not run yet.");

    private static AppServices? Instance { get; set; }

    public DefenseClawPaths Paths { get; }

    public ConfigStore ConfigStore { get; }

    public TokenResolver TokenResolver { get; }

    public GatewayClient Gateway { get; }

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

    /// <summary>REST port the <see cref="Gateway"/> client was built against.</summary>
    public int ApiPort { get; }

    /// <summary>Set when config.yaml exists but could not be parsed; null otherwise.</summary>
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
    /// </summary>
    public event EventHandler? ConfigReloaded;

    public static AppServices Initialize()
    {
        Instance ??= new AppServices();
        return Instance;
    }

    /// <summary>Token provider handed to <see cref="GatewayClient"/>; re-read per request.</summary>
    public SecretValue? CurrentToken() => Token.Token;

    /// <summary>
    /// Re-reads config.yaml and the .env file, then re-resolves the token ladder.
    /// <para>
    /// The reading, parsing and hashing all happen on the calling thread — which for the
    /// watcher-driven path is a thread-pool or <c>FileSystemWatcher</c> thread, and must
    /// stay that way: this is file I/O plus a YAML parse, and the UI thread has no business
    /// doing it. Only the <see cref="ConfigReloaded"/> notification is marshalled.
    /// </para>
    /// </summary>
    public void ReloadConfig()
    {
        var document = LoadConfigSafely(ConfigStore, out var error);
        var token = TokenResolver.Resolve(document.Config);

        lock (_gate)
        {
            _config = document;
            _token = token;
            ConfigLoadError = error;
        }

        RegisterTokenWithCli();
        RaiseConfigReloaded();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ConfigWatcher.Changed -= OnConfigChanged;
        Monitor.Dispose();
        ConfigWatcher.Dispose();
        GatewayLog.Dispose();
        WatchdogLog.Dispose();
        Gateway.Dispose();
        Instance = null;
    }

    /// <summary>
    /// The App side of <see cref="ConfigChangeToken.Changed"/>. Runs on the watcher's or
    /// the poll timer's thread; <see cref="ReloadConfig"/> is idempotent, which is what
    /// that event's contract requires of its handlers.
    /// </summary>
    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e) => ReloadConfig();

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

    private static ConfigDocument LoadConfigSafely(ConfigStore store, out string? error)
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

        return ConfigStore.Parse(string.Empty, store.ConfigFilePath);
    }
}
