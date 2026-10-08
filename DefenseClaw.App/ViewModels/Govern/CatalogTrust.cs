using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Whether the list a panel is showing may be used to <i>change</i> anything. A panel that lists things the operator can then block,
/// quarantine, remove or approve only offers those actions while the list is a complete, recent read; a partial read, a failed refresh
/// that left old rows on screen, a read still in flight and a read that has simply gone old all leave the rows visible (they are still
/// information) and the actions off, with a reason.
/// <para>
/// One small state machine shared by every panel that has such a list: <see cref="GovernPanelViewModelBase"/> (Skills, MCPs, Plugins,
/// Tools), <see cref="RegistriesPanelViewModel"/> and the Policies panel. A panel feeds it the outcome of each read
/// (<see cref="MarkComplete"/>, <see cref="MarkPartial"/>, <see cref="MarkFailed"/>, <see cref="MarkPending"/>) and binds its buttons to
/// <see cref="IsTrusted"/> with <see cref="Reason"/> as the tooltip; the command that would run the change asks <see cref="IsTrusted"/>
/// again at the moment it runs (a button that was enabled a minute ago must not authorize anything now).
/// </para>
/// <para>
/// <b>Age.</b> Rows older than <see cref="FreshnessWindow"/> stop authorizing changes. The panel is not re-read on a timer, so the
/// bound value only updates when the panel next raises <c>PropertyChanged</c> for it; the check at the moment of the action is the one
/// that cannot be stale.
/// </para>
/// </summary>
public sealed class CatalogTrust
{
    /// <summary>How long a complete read keeps authorizing changes.</summary>
    public static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private MonotonicStamp _readAt;
    private Kind _kind = Kind.Never;
    private IReadOnlyList<string> _partial = Array.Empty<string>();
    private string _failure = string.Empty;

    public CatalogTrust(TimeSpan? freshnessWindow = null, TimeProvider? time = null)
    {
        FreshnessWindow = freshnessWindow ?? DefaultFreshnessWindow;
        _time = time ?? TimeProvider.System;
    }

    private enum Kind
    {
        /// <summary>No read has been asked for.</summary>
        Never,

        /// <summary>The first read is in flight; nothing has been read yet.</summary>
        Pending,

        /// <summary>The last read was complete.</summary>
        Complete,

        /// <summary>The last read printed rows but said a source could not be read.</summary>
        Partial,

        /// <summary>The last read failed; whatever rows the panel still shows are from an earlier read, if any.</summary>
        Failed,
    }

    public TimeSpan FreshnessWindow { get; }

    /// <summary>True when the last read listed rows but not every source (the panel shows a warning and the diagnostics).</summary>
    public bool IsPartial => _kind == Kind.Partial;

    /// <summary>The source diagnostics of a partial read; empty otherwise.</summary>
    public IReadOnlyList<string> PartialDiagnostics => _kind == Kind.Partial ? _partial : Array.Empty<string>();

    /// <summary>True when the last read failed outright (rows on screen, if any, are older).</summary>
    public bool LastReadFailed => _kind == Kind.Failed;

    /// <summary>
    /// True when the list is a complete read no older than <see cref="FreshnessWindow"/>, or when no read was ever asked for (a view
    /// model nobody has activated has nothing on screen to be wrong about).
    /// </summary>
    public bool IsTrusted => Reason is null;

    /// <summary>Why changes are off, in a sentence for a tooltip or banner; null when <see cref="IsTrusted"/>.</summary>
    public string? Reason
    {
        get
        {
            switch (_kind)
            {
                case Kind.Never:
                case Kind.Complete when !_readAt.HasElapsed(FreshnessWindow, _time):
                    return null;

                case Kind.Pending:
                    return "Changes are off until the first read finishes.";

                case Kind.Partial:
                    return "Changes are off: discovery was incomplete, so this list may be missing entries. Fix the source named in the warning and refresh.";

                case Kind.Failed:
                    return "Changes are off: the last read failed, so these rows may be out of date. Refresh to read them again.";

                default:
                    var minutes = Math.Max(1, (int)Math.Round(_readAt.Elapsed(_time).TotalMinutes));
                    return $"Changes are off: this list was read about {minutes} minute{(minutes == 1 ? string.Empty : "s")} ago. Refresh to act on current data.";
            }
        }
    }

    /// <summary>A read has been asked for and nothing has been read yet.</summary>
    public void MarkPending()
    {
        if (_kind == Kind.Never)
        {
            _kind = Kind.Pending;
        }
    }

    /// <summary>The read listed every source.</summary>
    public void MarkComplete()
    {
        _kind = Kind.Complete;
        _readAt = MonotonicStamp.Now(_time);
        _partial = Array.Empty<string>();
        _failure = string.Empty;
    }

    /// <summary>The read printed rows and named sources it could not read.</summary>
    public void MarkPartial(IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _kind = Kind.Partial;
        _readAt = MonotonicStamp.Now(_time);
        _partial = diagnostics.ToArray();
        _failure = string.Empty;
    }

    /// <summary>The read failed; rows the panel kept from before no longer authorize anything.</summary>
    public void MarkFailed(string message)
    {
        _kind = Kind.Failed;
        _failure = message ?? string.Empty;
        _partial = Array.Empty<string>();
    }

    /// <summary>What the last failed read said; empty when the last read did not fail.</summary>
    public string FailureMessage => _kind == Kind.Failed ? _failure : string.Empty;
}
