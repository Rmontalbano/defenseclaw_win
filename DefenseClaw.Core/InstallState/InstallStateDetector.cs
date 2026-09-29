using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
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

    /// <summary>
    /// The full <c>/health</c> result the probe got, so the caller does not have to GET it a
    /// second time. Null exactly when <see cref="HealthProbe"/> is: detection stops before the
    /// probe when the binaries are missing or the install is uninitialized, and a caller that
    /// still wants a health answer in those states fetches one itself.
    /// </summary>
    public GatewayResult<GatewayHealth>? Health { get; init; }

    public int Port { get; init; }

    /// <summary>Human-readable explanation for the status banner.</summary>
    public string Detail { get; init; } = string.Empty;

    public bool IsRunning => State == InstallState.Running;
}

/// <summary>
/// Composes paths, the gateway probe and the port owner lookup into a single answer for
/// the app's startup banner and tray icon.
/// <para>
/// <b>Which client, which port.</b> The instance-level client passed to the constructor is
/// only the default. A long-lived host whose <c>gateway.api_port</c> can change (the app
/// re-reads config.yaml while running) passes the client that is bound to the <i>same</i>
/// port per call — see <see cref="DetectAsync(int, IGatewayClient, CancellationToken)"/> — so
/// the port that is inspected for an owner and the port that is probed can never disagree.
/// </para>
/// <para>
/// <b>The port owner is looked up whether or not the port answers</b>, deliberately: the WSL
/// case this exists to catch is a relay that <i>answers</i> (<c>wslrelay.exe</c> forwards
/// <c>/health</c> to a gateway inside WSL), so skipping the lookup for a healthy-looking
/// port would hide exactly that. The lookup is a size query plus a fetch of the
/// <c>GetExtendedTcpTable</c> listener table per address family (IPv6 only if IPv4 had no
/// match), and is cheap next to the HTTP probe.
/// </para>
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
        return DetectAsync(config.Gateway.ApiPort, _gateway, cancellationToken);
    }

    /// <summary>Detects on <paramref name="port"/> using the constructor's gateway client.</summary>
    public Task<InstallStatus> DetectAsync(
        int port = GatewaySection.DefaultApiPort,
        CancellationToken cancellationToken = default) =>
        DetectAsync(port, _gateway, cancellationToken);

    /// <summary>
    /// Detects on <paramref name="port"/>, probing <c>/health</c> through
    /// <paramref name="gateway"/>, which the caller guarantees is bound to that same port.
    /// The probe's full result comes back in <see cref="InstallStatus.Health"/>, so a poller
    /// needs one <c>GET /health</c> per cycle instead of two.
    /// </summary>
    public async Task<InstallStatus> DetectAsync(
        int port,
        IGatewayClient gateway,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

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

        var health = await gateway.GetHealthAsync(cancellationToken).ConfigureAwait(false);

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
            Health = health,
            BinaryVersion = health.Value?.Provenance?.BinaryVersion,
            Detail = BuildDetail(running, port, owner, wslDetected, health),
        };
    }

    private static string BuildDetail(
        bool running,
        int port,
        PortOwner? owner,
        bool wslDetected,
        GatewayResult<GatewayHealth> health)
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
