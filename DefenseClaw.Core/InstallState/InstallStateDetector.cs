using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Install;

/// <summary>
/// The four states a Windows install can actually be in — every one of them observed on
/// real machines during 0.8.7 bring-up.
/// </summary>
public enum InstallState
{
    /// <summary>No <c>defenseclaw.exe</c> on PATH or in <c>%LOCALAPPDATA%\Programs\DefenseClaw\bin</c>.</summary>
    NotInstalled = 0,

    /// <summary>
    /// Binaries present but <c>~/.defenseclaw</c> has no config.yaml. The CLI answers
    /// "DefenseClaw is not initialized - run 'defenseclaw init' first." and exits 1.
    /// </summary>
    InstalledNotInitialized,

    /// <summary>Initialized, but nothing is listening on the API port.</summary>
    GatewayStopped,

    /// <summary>Sidecar answering on the API port.</summary>
    Running,
}

/// <summary>Full detection result, including the WSL coexistence flag.</summary>
public sealed record InstallStatus
{
    public required InstallState State { get; init; }

    /// <summary>
    /// True when the API port is owned by <c>wslrelay.exe</c>: the gateway answering is a
    /// WSL instance relayed onto Windows, not the native one. The app must say so rather
    /// than pretending the native install is healthy.
    /// </summary>
    public bool WslGatewayDetected { get; init; }

    public PortOwner? PortOwner { get; init; }

    public string? CliPath { get; init; }

    public string? GatewayCliPath { get; init; }

    /// <summary>Version reported by <c>/health</c> provenance, e.g. <c>0.8.7</c>.</summary>
    public string? BinaryVersion { get; init; }

    /// <summary>Raw outcome of the <c>/health</c> probe; null when it was not attempted.</summary>
    public GatewayStatus? HealthProbe { get; init; }

    public int Port { get; init; }

    /// <summary>Human-readable explanation for the status banner.</summary>
    public string Detail { get; init; } = string.Empty;

    public bool IsRunning => State == InstallState.Running;
}

/// <summary>
/// Composes paths, the gateway probe and the port owner lookup into a single answer for
/// the app's startup banner and tray icon.
/// </summary>
public sealed class InstallStateDetector
{
    private readonly DefenseClawPaths _paths;
    private readonly IGatewayClient _gateway;
    private readonly IPortOwnerInspector _portInspector;

    public InstallStateDetector(
        DefenseClawPaths paths,
        IGatewayClient gateway,
        IPortOwnerInspector? portInspector = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _portInspector = portInspector ?? new PortOwnerInspector();
    }

    /// <summary>Detects using the port from <paramref name="config"/>.</summary>
    public Task<InstallStatus> DetectAsync(DefenseClawConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return DetectAsync(config.Gateway.ApiPort, cancellationToken);
    }

    public async Task<InstallStatus> DetectAsync(
        int port = GatewaySection.DefaultApiPort,
        CancellationToken cancellationToken = default)
    {
        var cliPath = _paths.CliPath;
        var gatewayCliPath = _paths.GatewayCliPath;
        var owner = _portInspector.FindListener(port);
        var wslDetected = owner?.IsWslRelay ?? false;

        if (cliPath is null && gatewayCliPath is null)
        {
            return new InstallStatus
            {
                State = InstallState.NotInstalled,
                Port = port,
                PortOwner = owner,
                WslGatewayDetected = wslDetected,
                Detail = wslDetected
                    ? "DefenseClaw is not installed natively, but a WSL gateway is relaying port " +
                      $"{port} through {owner!.ProcessName}."
                    : "defenseclaw.exe was not found on PATH or in the installer's bin directory.",
            };
        }

        if (!_paths.IsInitialized)
        {
            return new InstallStatus
            {
                State = InstallState.InstalledNotInitialized,
                Port = port,
                CliPath = cliPath,
                GatewayCliPath = gatewayCliPath,
                PortOwner = owner,
                WslGatewayDetected = wslDetected,
                Detail = $"No config.yaml in {_paths.DataDirectory}. Run 'defenseclaw init' to set it up.",
            };
        }

        var health = await _gateway.GetHealthAsync(cancellationToken).ConfigureAwait(false);

        // /health is unauthenticated on 0.8.7, so anything other than "unreachable"
        // means something is listening and answering.
        var running = health.Status != GatewayStatus.Unreachable;

        return new InstallStatus
        {
            State = running ? InstallState.Running : InstallState.GatewayStopped,
            Port = port,
            CliPath = cliPath,
            GatewayCliPath = gatewayCliPath,
            PortOwner = owner,
            WslGatewayDetected = wslDetected,
            HealthProbe = health.Status,
            BinaryVersion = health.Value?.Provenance?.BinaryVersion,
            Detail = BuildDetail(running, port, owner, wslDetected, health),
        };
    }

    private static string BuildDetail(
        bool running,
        int port,
        PortOwner? owner,
        bool wslDetected,
        GatewayResult<Gateway.Models.GatewayHealth> health)
    {
        if (wslDetected)
        {
            return $"Port {port} is owned by {owner?.ProcessName} (pid {owner?.Pid}) — this is a WSL gateway " +
                   "relayed to Windows, not the native install.";
        }

        if (!running)
        {
            return $"Nothing is listening on 127.0.0.1:{port}. Start it with 'defenseclaw-gateway start'.";
        }

        return health.IsOk
            ? $"Gateway responding on 127.0.0.1:{port}."
            : $"Gateway responding on 127.0.0.1:{port}, but /health returned {health.Status}: {health.ErrorMessage}";
    }
}
