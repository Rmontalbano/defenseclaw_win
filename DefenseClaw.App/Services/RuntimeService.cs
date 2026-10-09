using System.Windows;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services;

/// <summary>
/// What the app knows about the DefenseClaw runtime it is connected to, for every screen that shows something only newer
/// runtimes have (<see cref="Check"/>) and for About / Updates (<see cref="Current"/>).
/// <para>
/// A thin owner around <see cref="RuntimeDetector"/>: it supplies the paths-derived fingerprint, source and probe runner, keeps the
/// answer fresh (a check every <see cref="PollInterval"/> costs one file stamp while the CLI is unchanged, and re-probes by
/// itself after an upgrade or reinstall), and raises <see cref="Changed"/> on the UI thread. Nothing runs until <see cref="Start"/>
/// or an explicit <see cref="RefreshAsync"/>; before the first answer every capability is absent, so a gated feature is hidden,
/// never flashed.
/// </para>
/// </summary>
internal sealed class RuntimeService : IDisposable
{
    /// <summary>How often the background check looks at the CLI file. The probes themselves run only when it has changed.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly DefenseClawPaths _paths;
    private Timer? _timer;
    private int _disposed;

    /// <param name="paths">The runtime to drive.</param>
    /// <param name="runner">A test seam: answers the probes instead of starting the CLI. Null is the real thing.</param>
    /// <param name="time">Clock for the retry window; the system one when null.</param>
    public RuntimeService(DefenseClawPaths paths, RuntimeProbeRunner? runner = null, TimeProvider? time = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Detector = new RuntimeDetector(
            runner ?? RuntimeEnvironment.CreateProbeRunner(paths),
            // A supplied runner stands in for the CLI, so there is no CLI file to stamp: its answers are cached as one runtime.
            runner is null ? () => RuntimeEnvironment.Fingerprint(paths, time) : () => "supplied-runner",
            () => RuntimeEnvironment.Source(paths),
            time);
        Detector.Changed += OnDetectorChanged;
    }

    public RuntimeDetector Detector { get; }

    /// <summary>The runtime the app was started against: <see cref="RuntimeSelection.Installed"/> unless the developer selector chose otherwise.</summary>
    public RuntimeSelection Selection => _paths.Runtime;

    /// <summary>The last probe result; <see cref="RuntimeSnapshot.NotProbed"/> before the first.</summary>
    public RuntimeSnapshot Current => Detector.Current;

    /// <summary>What the connected runtime can do; nothing until it has been probed and shown.</summary>
    public RuntimeCapabilities Capabilities => Detector.Capabilities;

    /// <summary>
    /// Raised on the UI thread (when there is one) after the answer changed: first probe, upgrade, a runtime that came or went.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The one test a panel, palette entry or Setup tile applies: open when <paramref name="required"/> is null (0.8.10 already has
    /// the feature) or the runtime has it. See <see cref="RuntimeGate"/>.
    /// </summary>
    public GateDecision Check(RuntimeCapability? required) => RuntimeGate.Check(Capabilities, required);

    /// <summary>Probes now if the CLI changed (or <paramref name="force"/>). Never throws; a failed probe is an "unknown" snapshot.</summary>
    public Task<RuntimeSnapshot> RefreshAsync(bool force = false, CancellationToken cancellationToken = default) =>
        Detector.RefreshAsync(force, cancellationToken);

    /// <summary>True once <see cref="Start"/> has been called: the background check is running, so a first answer is on its way.</summary>
    public bool IsStarted => Volatile.Read(ref _timer) is not null;

    /// <summary>
    /// The answer for a screen that has to choose between two surfaces and so cannot show either until it knows what the runtime is (the Policies
    /// panel, CUST-293). Once the runtime has been probed, or when nothing is running that would probe it (the service was never started: a tool, a
    /// test), this is the current snapshot, already complete. In the app, before the first probe has answered, it joins that probe - the single
    /// flight <see cref="RefreshAsync"/> shares - and returns its answer. Never throws for a failed probe (that is an unknown snapshot); the token
    /// only stops the caller waiting.
    /// </summary>
    public Task<RuntimeSnapshot> WhenProbedAsync(CancellationToken cancellationToken = default) =>
        IsStarted && ReferenceEquals(Current, RuntimeSnapshot.NotProbed)
            ? RefreshAsync(cancellationToken: cancellationToken)
            : Task.FromResult(Current);

    /// <summary>Starts the first probe and the background check. Idempotent.</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _timer is not null)
        {
            return;
        }

        _timer = new Timer(_ => FireAndForget(), null, TimeSpan.Zero, PollInterval);
    }

    private void FireAndForget()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = RefreshQuietlyAsync();
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            _ = await Detector.RefreshAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A background refresh must never end the process; the detector already turns failures into "unknown".
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void OnDetectorChanged(object? sender, EventArgs e)
    {
        var handler = Changed;
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
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(() => handler(this, EventArgs.Empty));
        }
        catch (InvalidOperationException)
        {
            // Shutdown began between the check and the post; a dropped notification on the way out is fine.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Detector.Changed -= OnDetectorChanged;
        _timer?.Dispose();
    }
}
