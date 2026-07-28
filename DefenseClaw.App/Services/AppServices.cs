using System.IO;
using DefenseClaw.Core.Audit;
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

    /// <summary>Raised after config.yaml or .env changed and the token was re-resolved.</summary>
    public event EventHandler? ConfigReloaded;

    public static AppServices Initialize()
    {
        Instance ??= new AppServices();
        return Instance;
    }

    /// <summary>Token provider handed to <see cref="GatewayClient"/>; re-read per request.</summary>
    public SecretValue? CurrentToken() => Token.Token;

    /// <summary>Re-reads config.yaml and the .env file, then re-resolves the token ladder.</summary>
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
        ConfigReloaded?.Invoke(this, EventArgs.Empty);
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

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e) => ReloadConfig();

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
