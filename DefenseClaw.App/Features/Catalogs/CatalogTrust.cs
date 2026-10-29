using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Time;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Whether the list a panel is showing may be used to <i>change</i> anything. A panel that lists things the operator can then block,
/// quarantine, remove or approve only offers those actions while the list is a complete, recent read of the configuration as it is now; a
/// partial read, a failed refresh that left old rows on screen, a read still in flight, a read that has simply gone old and a read made
/// before <c>config.yaml</c> or <c>.env</c> changed all leave the rows visible (they are still information) and the actions off, with a reason.
/// <para>
/// One small state machine shared by every panel that has such a list: <see cref="GovernPanelViewModelBase"/> (Skills, MCPs, Plugins,
/// Tools), <see cref="RegistriesPanelViewModel"/>, the Policies panel and its model surface (<see cref="PolicyModelViewModel"/>). A panel
/// feeds it the outcome of each read (<see cref="BeginRead"/>, then <see cref="MarkComplete"/>, <see cref="MarkPartial"/> or
/// <see cref="MarkFailed"/>) and binds its buttons to <see cref="IsTrusted"/> with <see cref="Reason"/> as the tooltip; the command that would
/// run the change asks <see cref="ReasonNow"/> at the moment it runs (a button that was enabled a minute ago must not authorize anything now).
/// </para>
/// <para>
/// <b>Age.</b> Rows older than <see cref="FreshnessWindow"/> stop authorizing changes. The panel is not re-read on a timer, so the
/// bound value only updates when the panel next raises <c>PropertyChanged</c> for it; the check at the moment of the action is the one
/// that cannot be stale.
/// </para>
/// <para>
/// <b>Config (CUST-312).</b> The Mac's Policies counts as current only while the <c>config.yaml</c> / <c>.env</c> disk signature is the one
/// it was read under (<c>PoliciesView.swift:25-29</c>). Here <see cref="BeginRead"/> takes that signature
/// (<see cref="ConfigDiskSignature"/>: path, length and last-write time of each file, never their content) when a read starts, and it is kept
/// with the rows. Three things then compare it with the files as they are now, each only when something happens, never on a timer: the panel
/// hearing <c>AppServices.ConfigReloaded</c> (the existing watcher raises it for both files), the panel coming back on screen, and a change
/// being requested or confirmed (<see cref="ReasonNow"/>), which is what catches an edit the watcher has not reported yet. A difference
/// makes the list <see cref="IsStale"/>: the rows stay, changes are off with <see cref="ConfigChangedReason"/>, and the next complete read
/// restores them. A read that began after the change is not stale (the watcher is a few hundred milliseconds slower than a panel that
/// re-reads straight after its own change); one that was running while the files moved is, because nothing says which side of the edit it saw.
/// </para>
/// <para>
/// <see cref="IsTrusted"/> and <see cref="Reason"/> never touch the disk - they are bound, and read for every row - so they only know
/// what has been noticed. A trust built without a signature source (<c>new CatalogTrust()</c>) has no config rule at all. Not thread-safe: a
/// panel drives it from the UI thread, where its reads end and <c>ConfigReloaded</c> is delivered.
/// </para>
/// </summary>
public sealed class CatalogTrust
{
    /// <summary>How long a complete read keeps authorizing changes.</summary>
    public static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromMinutes(10);

    /// <summary>What <see cref="ConfigChangedReason"/> says was read, unless the panel says otherwise.</summary>
    public const string ListRead = "this list was read";

    private readonly TimeProvider _time;
    private readonly Func<ConfigDiskSignature>? _configSignature;
    private readonly string _readClause;
    private MonotonicStamp _readAt;
    private Kind _kind = Kind.Never;
    private IReadOnlyList<string> _partial = Array.Empty<string>();
    private string _failure = string.Empty;

    /// <summary>The files as they were when the read in flight began; null when none is, or when there is no signature source.</summary>
    private ConfigDiskSignature? _pendingSignature;

    /// <summary>The files as they were when the read on screen began.</summary>
    private ConfigDiskSignature? _readSignature;

    /// <summary>Why the read on screen is out of date although it was complete; null while it is not.</summary>
    private string? _staleReason;

    /// <param name="freshnessWindow">How long a complete read keeps authorizing changes; <see cref="DefaultFreshnessWindow"/> when null.</param>
    /// <param name="time">The clock; the system's when null.</param>
    /// <param name="configSignature">
    /// Takes the signature of <c>config.yaml</c> and <c>.env</c> (a stat of each, no content). Null: the list is not tied to the files at
    /// all, which is what a trust that nothing reads from the configuration wants. Panels use <see cref="Watching"/>.
    /// </param>
    /// <param name="readClause">What <see cref="ConfigChangedReason"/> says was read: "this list was read", "these settings were read".</param>
    public CatalogTrust(TimeSpan? freshnessWindow = null, TimeProvider? time = null, Func<ConfigDiskSignature>? configSignature = null, string readClause = ListRead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readClause);

        FreshnessWindow = freshnessWindow ?? DefaultFreshnessWindow;
        _time = time ?? TimeProvider.System;
        _configSignature = configSignature;
        _readClause = readClause;
    }

    /// <summary>A trust for the installation <paramref name="paths"/> describes: its lists go stale when that installation's config.yaml or .env change.</summary>
    public static CatalogTrust Watching(DefenseClawPaths paths, string readClause = ListRead)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new CatalogTrust(configSignature: () => ConfigDiskSignature.Capture(paths), readClause: readClause);
    }

    /// <summary>
    /// The sentence a list gives for being stale because the configuration moved: <c>Changes are off: config.yaml or .env changed after this
    /// list was read. Refresh to act on current data.</c> (the Policies model panel's wording, in the same form as the other reasons).
    /// </summary>
    public static string ConfigChangedReason(string readClause = ListRead) =>
        $"Changes are off: config.yaml or .env changed after {readClause}. Refresh to act on current data.";

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
    /// True when the rows on screen were read before something they depend on changed (<see cref="MarkStale"/>, or <see cref="CheckConfig"/>
    /// finding config.yaml or .env different). They are kept; a fresh read clears it. Whatever else is wrong with the list (a failed or partial
    /// read) is said by <see cref="Reason"/> first.
    /// </summary>
    public bool IsStale => _staleReason is not null;

    /// <summary>
    /// True when the list is a complete read, no older than <see cref="FreshnessWindow"/>, of a configuration that has not moved since, or
    /// when no read was ever asked for (a view model nobody has activated has nothing on screen to be wrong about).
    /// </summary>
    public bool IsTrusted => Reason is null;

    /// <summary>
    /// Why changes are off, in a sentence for a tooltip or banner; null when <see cref="IsTrusted"/>. A pure read of what is known: it does
    /// not look at the disk (see <see cref="ReasonNow"/> for that).
    /// </summary>
    public string? Reason
    {
        get
        {
            switch (_kind)
            {
                case Kind.Complete when _staleReason is { } stale:
                    return stale;

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

    /// <summary>
    /// A read begins now: <see cref="MarkPending"/>, and the signature of config.yaml and .env at this moment is noted as the one the rows
    /// that read produces were read under. Call it where the read really starts - not for a request that is dropped because a read is
    /// already running, which would replace the running read's signature with a later one.
    /// </summary>
    public void BeginRead()
    {
        MarkPending();
        _pendingSignature = _configSignature?.Invoke();
    }

    /// <summary>The read listed every source.</summary>
    public void MarkComplete()
    {
        _kind = Kind.Complete;
        _readAt = MonotonicStamp.Now(_time);
        _partial = Array.Empty<string>();
        _failure = string.Empty;
        TakeReadSignature();
    }

    /// <summary>The read printed rows and named sources it could not read.</summary>
    public void MarkPartial(IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _kind = Kind.Partial;
        _readAt = MonotonicStamp.Now(_time);
        _partial = diagnostics.ToArray();
        _failure = string.Empty;
        TakeReadSignature();
    }

    /// <summary>The read failed; rows the panel kept from before no longer authorize anything.</summary>
    public void MarkFailed(string message)
    {
        _kind = Kind.Failed;
        _failure = message ?? string.Empty;
        _partial = Array.Empty<string>();

        // The rows on screen are still the ones the last good read made, and its signature is still theirs.
        _pendingSignature = null;
    }

    /// <summary>
    /// The rows on screen were read, and something they depend on has changed since (config.yaml or .env: the panel heard
    /// <c>AppServices.ConfigReloaded</c>, or a command it ran has just rewritten settings). They are kept; they stop authorizing changes, with
    /// <paramref name="reason"/> as the tooltip, until a read replaces them (<see cref="MarkComplete"/> / <see cref="MarkPartial"/>). Does nothing
    /// before a read has delivered rows: with none asked for there is nothing on screen to be wrong about, and the first read is judged against
    /// the files when it ends.
    /// </summary>
    /// <param name="reason">The whole sentence (<see cref="ConfigChangedReason"/> for the configuration moving).</param>
    public void MarkStale(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (_kind is Kind.Never or Kind.Pending)
        {
            return;
        }

        _staleReason = reason;
    }

    /// <summary>
    /// Compares config.yaml and .env with the signature the rows on screen were read under (a stat of each file) and, when they differ,
    /// marks the list stale with <see cref="ConfigChangedReason"/>. Cheap and idempotent: it is meant for the moments the panel has reason
    /// to look - a reload notification, the panel coming back on screen - and never for a property that is bound.
    /// </summary>
    /// <returns>True when the list is stale (now or already): the caller tells its bindings.</returns>
    public bool CheckConfig()
    {
        if (_staleReason is null
            && _configSignature is not null
            && _readSignature is { } read
            && _kind is not (Kind.Never or Kind.Pending)
            && _configSignature() != read)
        {
            _staleReason = ConfigChangedReason(_readClause);
        }

        return _staleReason is not null;
    }

    /// <summary>
    /// <see cref="Reason"/> after looking at the files once (<see cref="CheckConfig"/>): the answer for the moment a change is requested or
    /// confirmed, when a notification that has not arrived yet must not be the reason an edit slips through.
    /// </summary>
    public string? ReasonNow()
    {
        _ = CheckConfig();
        return Reason;
    }

    /// <summary>What the last failed read said; empty when the last read did not fail.</summary>
    public string FailureMessage => _kind == Kind.Failed ? _failure : string.Empty;

    /// <summary>
    /// A read that delivered rows ends: the signature it began under becomes the rows' own, and a fresh read is not stale. If the files are
    /// already different from that signature they moved while the read was running, and nothing says which side of the edit it saw.
    /// </summary>
    private void TakeReadSignature()
    {
        _staleReason = null;
        if (_configSignature is null)
        {
            _readSignature = null;
            _pendingSignature = null;
            return;
        }

        var now = _configSignature();
        _readSignature = _pendingSignature ?? now;
        _pendingSignature = null;
        if (_readSignature != now)
        {
            _staleReason = ConfigChangedReason(_readClause);
        }
    }
}
