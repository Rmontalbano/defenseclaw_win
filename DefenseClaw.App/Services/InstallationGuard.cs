using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services;

/// <summary>
/// The app's one answer to "may this installation be changed?", reached as <c>Services.Installation</c>. It holds the
/// <see cref="InstallationContext"/> the app resolved at start, and every surface that offers a change consults it instead of working the
/// rule out again: a panel's existing can-act check adds <see cref="IsMutable"/> (or lets <see cref="BlockedReason"/> be its tooltip), a review
/// carries <see cref="ReasonFor(string, IReadOnlyList{string})"/>, the palette greys a row with it, the Overview banner and the Settings block
/// show <see cref="BlockedReason"/>. <b>The disabled controls are courtesy; the guard is <see cref="CliRunner"/></b>, which refuses every
/// state-changing run on a read-only installation whatever drew the button (see <see cref="InstallationGate"/>), and reads the live context from
/// here, so the two can never disagree.
/// <para>
/// <b>When it changes.</b> The selection (the environment, the developer selector, the roots) is fixed for the life of the process, so the
/// context is resolved once at start. What it says can still move while the app runs, because it depends on what is in config.yaml and on the
/// disk: a file edited to <c>deployment_mode: managed_enterprise</c> turns it read-only without a restart, and a broken file that is fixed
/// turns it writable again. <see cref="Refresh"/> (called when config.yaml reloads) re-resolves and, when the verdict differs,
/// raises <see cref="Changed"/> on the UI thread. A guard built without a resolver (a test, a harness) never changes.
/// </para>
/// </summary>
internal sealed class InstallationGuard
{
    private readonly RuntimeSelection _runtime;
    private readonly Func<InstallationContext>? _resolve;
    private readonly object _gate = new();
    private volatile InstallationContext _context;
    private bool _changePending;

    /// <param name="context">What the app resolved at start.</param>
    /// <param name="runtime">The developer runtime selection the app started with (a non-default one is not the Setup-installed runtime).</param>
    /// <param name="resolve">Resolves the installation again, for <see cref="Refresh"/>; null means it never changes.</param>
    public InstallationGuard(InstallationContext context, RuntimeSelection? runtime = null, Func<InstallationContext>? resolve = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _runtime = runtime ?? RuntimeSelection.Installed;
        _resolve = resolve;
    }

    /// <summary>The installation the app is driving now, and what it may do to it.</summary>
    public InstallationContext Context => _context;

    /// <summary>True when state-changing actions are allowed: a user-owned installation.</summary>
    public bool IsMutable => _context.IsMutable;

    /// <summary>
    /// Why state-changing actions are off, as one sentence for a banner or a tooltip; null while they are allowed. This is the text every
    /// disabled control shows.
    /// </summary>
    public string? BlockedReason => _context.BlockedReason;

    /// <summary>"State-changing actions disabled: &lt;reason&gt;", the Overview banner's text; null while they are allowed.</summary>
    public string? BannerText => BlockedReason is { } reason ? "State-changing actions disabled: " + reason : null;

    /// <summary>
    /// Null when running <paramref name="argv"/> on <paramref name="executable"/> is allowed here; otherwise the reason it is not
    /// (<see cref="InstallationGate.RefusalFor"/>, the rule the runner enforces). A read is allowed whatever the installation.
    /// </summary>
    public string? ReasonFor(string executable, IReadOnlyList<string> argv) => InstallationGate.RefusalFor(_context, executable, argv);

    /// <summary><see cref="ReasonFor(string, IReadOnlyList{string})"/> for the <c>defenseclaw</c> CLI.</summary>
    public string? ReasonFor(IReadOnlyList<string> argv) => ReasonFor(CommandReview.DefaultExecutable, argv);

    /// <summary>The first reason any of a review's steps cannot run here; null when every one can (a review of reads, or a writable installation).</summary>
    public string? ReasonFor(IEnumerable<CommandReviewStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Select(step => ReasonFor(step.Executable, step.Argv)).FirstOrDefault(reason => reason is not null);
    }

    /// <summary>
    /// Null for a read; the installation's reason for anything else. For a control that knows its tier but has no argv (a toggle that runs a
    /// reviewed command later).
    /// </summary>
    public string? ReasonFor(CommandTier tier) => tier == CommandTier.ReadOnly ? null : BlockedReason;

    /// <summary>
    /// Why the runtime cannot be upgraded from here, or null. An upgrade is the Setup installer replacing the runtime Setup installed; it is
    /// not offered for a managed installation, an invalid selection, or a developer runtime (a side-by-side CLI or a container, which that
    /// installer would not touch and which is upgraded where it was built).
    /// </summary>
    public string? UpgradeBlockedReason =>
        BlockedReason is { } reason
            ? "The runtime is not upgraded from here. " + reason
            : !_runtime.IsDefault || _context.UsesDeveloperRuntime
                ? "This app is driving a developer runtime (Settings → Advanced), not the one Setup installed, so a Setup upgrade would not touch it. Upgrade that runtime where it was built, or turn the selector off."
                : null;

    /// <summary>True when the runtime upgrade is offered.</summary>
    public bool CanUpgrade => UpgradeBlockedReason is null;

    /// <summary>
    /// Raised, on the UI thread, when <see cref="Refresh"/> found a different verdict than the one every control was drawn with: the controls
    /// that read the guard when they are drawn want to be drawn again. Never raised for a guard without a resolver.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Resolves the installation again (this reads config.yaml: call it off the UI thread) and keeps the answer. Returns true when it
    /// differs from the one in force. The change is announced by <see cref="PublishPendingChange"/>, which the caller runs on the UI thread, so
    /// a handler can touch controls.
    /// </summary>
    public bool Refresh()
    {
        if (_resolve is null)
        {
            return false;
        }

        InstallationContext next;
        try
        {
            next = _resolve();
        }
#pragma warning disable CA1031 // A resolution that fails must leave the answer in force, not take down the thread that asked.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"installation: could not resolve it again; keeping the current answer: {ex.GetType().Name}");
            return false;
        }

        lock (_gate)
        {
            if (next == _context)
            {
                return false;
            }

            _context = next;
            _changePending = true;
            return true;
        }
    }

    /// <summary>Raises <see cref="Changed"/> once for a change <see cref="Refresh"/> found. UI thread.</summary>
    public void PublishPendingChange()
    {
        lock (_gate)
        {
            if (!_changePending)
            {
                return;
            }

            _changePending = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Swaps in a different context and announces it at once. For tests and harnesses that stand in for a config.yaml that changed; the
    /// application itself only ever calls <see cref="Refresh"/>.
    /// </summary>
    internal void Replace(InstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            _context = context;
            _changePending = true;
        }

        PublishPendingChange();
    }
}
