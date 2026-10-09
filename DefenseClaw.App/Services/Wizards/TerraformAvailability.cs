using System.Diagnostics;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// An <see cref="ITerraformProbe"/> that always gives one answer: the stand-in an isolated composition uses (nothing in it may start a real
/// Terraform) and the fake a test hands in.
/// </summary>
internal sealed class FixedTerraformProbe : ITerraformProbe
{
    private readonly TerraformStatus _status;

    public FixedTerraformProbe(TerraformStatus status)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public Task<TerraformStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(_status);
}

/// <summary>
/// Whether the Splunk dashboards (<c>setup splunk dashboards plan | apply | destroy</c>) may be offered right now: the last answer of
/// <b>one</b> read-only <see cref="ITerraformProbe"/> (Terraform there, and new enough for the module the CLI ships), shared by the Setup
/// hub's card and the command palette's rows so they cannot disagree and Terraform is looked at once, not once per surface. The same
/// arrangement as <see cref="LocalStackAvailability"/> is for Docker, and for the same reasons:
/// <para>
/// <b>Not a poll.</b> Nothing runs on a timer. A surface asks when it needs the answer — the hub when it comes on screen or is
/// refreshed, the palette when it opens — through <see cref="EnsureFreshAsync"/>, which starts a probe only when there is no answer yet or
/// the one held is old (<see cref="FreshWhenAvailable"/>, and the much shorter <see cref="FreshWhenNot"/> for a "no", which is the answer
/// someone who has just installed Terraform is about to contradict). Concurrent callers join the probe already running. The look itself
/// never runs a plan, an init or anything in a working directory (see <see cref="ITerraformProbe"/>).
/// </para>
/// <para>
/// <b>Stale while it revalidates.</b> Once there is an answer, <see cref="Decision"/> keeps giving it while a newer one is being fetched,
/// so a card does not flicker into "Checking…" every time the hub is reopened; only before the very first answer is the feature closed
/// with that sentence.
/// </para>
/// <para>
/// <b>Fail open on "could not look".</b> A probe that throws or cannot finish is <see cref="TerraformState.Unknown"/>, which does not close
/// the feature: the CLI runs Terraform itself and says what is wrong in its own words. Only a definite no (not installed, too old, not
/// working) does.
/// </para>
/// </summary>
internal sealed class TerraformAvailability : IDisposable
{
    /// <summary>How long a "Terraform is ready" answer is trusted before the next surface that asks looks again.</summary>
    public static readonly TimeSpan FreshWhenAvailable = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a "not here / too old / not working" answer is trusted: much shorter, because the operator is about to fix it, but not
    /// shorter than a look takes, or every return to the page would start another.
    /// </summary>
    public static readonly TimeSpan FreshWhenNot = TimeSpan.FromSeconds(15);

    /// <summary>What the feature is closed with before the first answer has landed.</summary>
    public const string CheckingReason = "Checking for Terraform…";

    private static readonly string HowToFix =
        $"The Splunk dashboards are created by Terraform {TerraformProbe.MinimumVersion} or later. Install or update it " +
        $"(hashicorp.com/terraform), or set {TerraformProbe.BinaryVariable} to its full path, then check again " +
        "(the Setup page's Refresh does, and so does opening the command palette again).";

    private readonly ITerraformProbe _probe;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // Cancelled by Dispose and never disposed: it owns no timer and no wait handle, and a probe still running when the composition
    // goes away must be able to read its token without racing a disposed source.
    private readonly CancellationTokenSource _lifetime = new();

    private TerraformStatus? _status;
    private long _answeredAt;
    private Task? _inFlight;
    private bool _disposed;

    /// <param name="probe">The one look at Terraform. <see cref="TerraformProbe.CreateDefault"/> in the app; a fake in a test.</param>
    /// <param name="time">Clock for the freshness windows; the system one when null.</param>
    public TerraformAvailability(ITerraformProbe probe, TimeProvider? time = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The last answer; null until the first probe has landed.</summary>
    public TerraformStatus? Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>True while a probe is running (an answer may already be held; see the type remarks).</summary>
    public bool IsChecking
    {
        get
        {
            lock (_gate)
            {
                return _inFlight is not null;
            }
        }
    }

    /// <summary>
    /// What a surface applies: open when Terraform is there and new enough (or the look itself could not run, which the CLI then
    /// re-checks), otherwise closed with the probe's own sentence for why and how to put it right — or <see cref="CheckingReason"/>
    /// before the first answer.
    /// </summary>
    public GateDecision Decision => DecisionFor(Status);

    /// <summary>
    /// Raised after a probe changed the answer (the first one included), on the thread that finished it — <b>not</b> the UI thread, so a
    /// subscriber that touches bound state marshals first. A probe that repeats the answer already held raises nothing.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The decision for one answer; a null answer is "not looked yet".</summary>
    internal static GateDecision DecisionFor(TerraformStatus? status)
    {
        if (status is null)
        {
            return new GateDecision(false, CheckingReason);
        }

        return status.AllowsDashboards
            ? GateDecision.Open
            : new GateDecision(false, status.Summary + " " + HowToFix);
    }

    /// <summary>
    /// Looks at Terraform now, or joins the look already running, and completes once the answer is held. Never faults: a probe that
    /// throws is "could not check", which does not close the feature. <paramref name="cancellationToken"/> ends this caller's wait only.
    /// A look that was already running began before this call, so its answer can predate a fix the operator has just made: asking again
    /// once it lands is how to get the newer one.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            // Task.Run so the synchronous head of the probe (looking terraform up on PATH, starting a process) runs on the pool and not
            // on the UI thread that asked.
            load = _inFlight ??= Task.Run(ProbeAsync);
        }

        return cancellationToken.CanBeCanceled ? load.WaitAsync(cancellationToken) : load;
    }

    /// <summary>
    /// <see cref="RefreshAsync"/> unless the answer held is still fresh (<see cref="FreshWhenAvailable"/> / <see cref="FreshWhenNot"/>)
    /// and nothing is running — then it costs nothing and completes at once.
    /// </summary>
    public Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_disposed && _inFlight is null && _status is { } held && _time.GetElapsedTime(_answeredAt) < FreshFor(held))
            {
                return Task.CompletedTask;
            }
        }

        return RefreshAsync(cancellationToken);
    }

    private static TimeSpan FreshFor(TerraformStatus status) => status.AllowsDashboards ? FreshWhenAvailable : FreshWhenNot;

    private async Task ProbeAsync()
    {
        TerraformStatus? answer;
        try
        {
            answer = await _probe.ProbeAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only Dispose cancels this token; there is nobody left to tell.
            answer = null;
        }
#pragma warning disable CA1031 // A look that cannot run says nothing about Terraform; the CLI runs it itself, so it must not close the feature.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            answer = new TerraformStatus(
                TerraformState.Unknown,
                "Terraform could not be checked from here (" + ex.Message + "). The command checks it again itself.",
                string.Empty,
                string.Empty);
        }

        bool changed;
        lock (_gate)
        {
            changed = answer is not null && (_status is null || !Same(_status, answer));
            if (answer is not null)
            {
                _status = answer;
                _answeredAt = _time.GetTimestamp();
            }

            _inFlight = null;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private static bool Same(TerraformStatus a, TerraformStatus b) =>
        a.State == b.State &&
        string.Equals(a.Summary, b.Summary, StringComparison.Ordinal) &&
        string.Equals(a.Version, b.Version, StringComparison.Ordinal) &&
        string.Equals(a.Path, b.Path, StringComparison.Ordinal);

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // One subscriber's bug must not stop the others hearing the answer, or fault the probe.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceWarning($"A subscriber to the Terraform availability change failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
    }
}
