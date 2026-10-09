using System.Diagnostics;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// An <see cref="IDockerProbe"/> that always gives one answer: the stand-in an isolated composition uses (nothing in it may start a real
/// Docker) and the fake a test hands in.
/// </summary>
internal sealed class FixedDockerProbe : IDockerProbe
{
    private readonly DockerStatus _status;

    public FixedDockerProbe(DockerStatus status)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(_status);
}

/// <summary>
/// Whether the bundled local observability stack (<c>setup local-observability</c>) may be offered right now: the last answer of
/// <b>one</b> read-only <see cref="IDockerProbe"/> (Compose v2 there, and an engine that answers), shared by the Setup hub's card and
/// the command palette's rows so they cannot disagree and Docker is looked at once, not once per surface.
/// <para>
/// <b>Not a poll.</b> Nothing runs on a timer. A surface asks when it needs the answer — the hub when it comes on screen or is
/// refreshed, the palette when it opens (it lists the stack's rows unless DefenseClaw is not installed) — through <see cref="EnsureFreshAsync"/>, which starts a probe only
/// when there is no answer yet or the one held is old (<see cref="FreshWhenAvailable"/>, and the much shorter
/// <see cref="FreshWhenNot"/> for a "no", which is the answer someone who has just started Docker Desktop is about to contradict).
/// Concurrent callers join the probe already running. The look itself never starts, pulls or runs anything (see
/// <see cref="IDockerProbe"/>).
/// </para>
/// <para>
/// <b>Stale while it revalidates.</b> Once there is an answer, <see cref="Decision"/> keeps giving it while a newer one is being
/// fetched, so a card does not flicker into "Checking…" every time the hub is reopened; only before the very first answer is the
/// feature closed with that sentence. The CLI is still the authority: it runs its own Docker preflight and refuses before it changes
/// anything, so what this holds only decides whether the feature is <i>offered</i>.
/// </para>
/// </summary>
internal sealed class LocalStackAvailability : IDisposable
{
    /// <summary>How long a "Docker is ready" answer is trusted before the next surface that asks looks again.</summary>
    public static readonly TimeSpan FreshWhenAvailable = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a "not here / not running / no Compose" answer is trusted: much shorter, because the operator is about to fix it, but not
    /// shorter than a look takes (Docker's CLI needs several seconds when its engine is stopped), or every return to the page would start another.
    /// </summary>
    public static readonly TimeSpan FreshWhenNot = TimeSpan.FromSeconds(15);

    /// <summary>What the feature is closed with before the first answer has landed.</summary>
    public const string CheckingReason = "Checking for Docker…";

    private const string HowToCheckAgain =
        "The local observability stack runs in Docker Compose on this machine; once Docker is ready, check again " +
        "(the Setup page's Refresh does, and so does opening the command palette again).";

    private readonly IDockerProbe _probe;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // Cancelled by Dispose and never disposed: it owns no timer and no wait handle, and a probe still running when the composition
    // goes away must be able to read its token without racing a disposed source.
    private readonly CancellationTokenSource _lifetime = new();

    private DockerStatus? _status;
    private long _answeredAt;
    private Task? _inFlight;
    private bool _disposed;

    /// <param name="probe">The one look at Docker. <see cref="DockerProbe.CreateDefault"/> in the app; a fake in a test.</param>
    /// <param name="time">Clock for the freshness windows; the system one when null.</param>
    public LocalStackAvailability(IDockerProbe probe, TimeProvider? time = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The last answer; null until the first probe has landed.</summary>
    public DockerStatus? Status
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
    /// What a surface applies: open when Compose v2 is there and the engine answered (or the look itself could not run, which the CLI
    /// then re-checks), otherwise closed with the probe's own sentence for why — or <see cref="CheckingReason"/> before the first answer.
    /// </summary>
    public GateDecision Decision => DecisionFor(Status);

    /// <summary>
    /// Raised after a probe changed the answer (the first one included), on the thread that finished it — <b>not</b> the UI thread, so a
    /// subscriber that touches bound state marshals first. A probe that repeats the answer already held raises nothing.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The decision for one answer; a null answer is "not looked yet".</summary>
    internal static GateDecision DecisionFor(DockerStatus? status)
    {
        if (status is null)
        {
            return new GateDecision(false, CheckingReason);
        }

        return status.AllowsLocalStack
            ? GateDecision.Open
            : new GateDecision(false, status.Summary + " " + HowToCheckAgain);
    }

    /// <summary>
    /// Looks at Docker now, or joins the look already running, and completes once the answer is held. Never faults: a probe that throws
    /// is "could not check", which does not close the feature. <paramref name="cancellationToken"/> ends this caller's wait only. A look
    /// that was already running began before this call, so its answer can predate a fix the operator has just made (Docker Desktop started a
    /// moment ago): asking again once it lands is how to get the newer one.
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

            // Task.Run so the synchronous head of the probe (looking docker up on PATH, starting a process) runs on the pool and not
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

    private static TimeSpan FreshFor(DockerStatus status) => status.AllowsLocalStack ? FreshWhenAvailable : FreshWhenNot;

    private async Task ProbeAsync()
    {
        DockerStatus? answer;
        try
        {
            answer = await _probe.ProbeAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only Dispose cancels this token; there is nobody left to tell.
            answer = null;
        }
#pragma warning disable CA1031 // A look that cannot run says nothing about Docker; the CLI checks it again itself, so it must not close the feature.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            answer = new DockerStatus(
                DockerState.Unknown,
                "Docker could not be checked from here (" + ex.Message + "). The command checks it again itself.",
                Array.Empty<string>());
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

    private static bool Same(DockerStatus a, DockerStatus b) =>
        a.State == b.State &&
        string.Equals(a.Summary, b.Summary, StringComparison.Ordinal) &&
        a.Warnings.SequenceEqual(b.Warnings, StringComparer.Ordinal);

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
                Trace.TraceWarning($"A subscriber to the Docker availability change failed: {ex.Message}");
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
