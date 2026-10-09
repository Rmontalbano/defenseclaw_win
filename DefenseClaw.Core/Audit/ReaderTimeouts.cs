namespace DefenseClaw.Core.Audit;

/// <summary>
/// How long the app lets one read of <c>audit.db</c> run before it interrupts the statement and gives up with a
/// <see cref="TimeoutException"/>, by kind of read. <see cref="Production"/> is what the app runs with; nothing else sets it.
/// A composition built for a test (<c>AppServices.CreateIsolated</c>) may be handed longer ones.
/// <para>
/// <b>Why a test needs them.</b> A read's clock starts when it is queued, not when its first statement runs: the read waits for a thread
/// pool thread first. On a machine that is far slower than any real one - a CI runner, or a laptop that is building and running several test
/// suites at once - that wait alone can pass ten seconds, and a view-model that is told "the queue could not be read" shows nothing and carries
/// on, which a test reads as "there were no findings". The bounds are right for a panel, which wants an answer or an apology promptly; a test
/// wants the answer, however long it takes, and has its own ceiling for a hang.
/// </para>
/// <para>
/// <b>What each one bounds.</b> <see cref="AlertQueue"/>: the unacknowledged-queue read (the badge, the tray, the Alerts list, the toasts).
/// <see cref="Audit"/>: the small lookups behind an inspector (an alert's findings and target history, an audit event's run and related events).
/// <see cref="HookTotals"/>: the counts and feeds that sit beside them on the Overview, the tray flyout and the Alerts list (hook calls and blocks,
/// the silent-bypass count, the egress feed). <see cref="Hourly"/>: the Overview's hourly decisions chart. <see cref="Mutation"/>: the Activity
/// panel's Mutations tab. <see cref="EventStream"/>: the Logs panel's Events and Verdicts streams. <see cref="RecentAuditMetrics"/>: the tray
/// flyout's counts. <see cref="JudgeHistory"/>: the Judge history window, per database.
/// </para>
/// </summary>
/// <param name="AlertQueue">The alert queue read; <see cref="AlertQueueReader.DefaultTimeout"/> in the app.</param>
/// <param name="Audit">The inspector lookups; 8 s in the app.</param>
/// <param name="HookTotals">The Overview, tray and egress counts; <see cref="ConnectorHookTotalsReader.DefaultTimeout"/> in the app.</param>
/// <param name="Hourly">The Overview's hourly chart; <see cref="HourlyActivityReader.DefaultTimeout"/> in the app.</param>
/// <param name="Mutation">The Mutations tab; <see cref="MutationReader.DefaultTimeout"/> in the app.</param>
/// <param name="EventStream">The Logs panel's streams; <see cref="EventStreamReader.DefaultTimeout"/> in the app.</param>
/// <param name="RecentAuditMetrics">
/// The tray flyout's counts; 5 s in the app, which is what the flyout has always passed (it shared <paramref name="HookTotals"/>). The reader's
/// own fallback, <see cref="RecentAuditMetricsReader.DefaultTimeout"/>, is never the one in effect.
/// </param>
/// <param name="JudgeHistory">One database of the Judge history; <see cref="ReadOnlyQuery.DefaultTimeout"/> in the app.</param>
public sealed record ReaderTimeouts(
    TimeSpan AlertQueue,
    TimeSpan Audit,
    TimeSpan HookTotals,
    TimeSpan Hourly,
    TimeSpan Mutation,
    TimeSpan EventStream,
    TimeSpan RecentAuditMetrics,
    TimeSpan JudgeHistory)
{
    /// <summary>
    /// The limits the app runs with: 10 s for the alert queue, 8 s for the inspector lookups, 5 s for the counts and feeds, 10 s for the hourly
    /// chart, the Mutations tab and the Logs streams, 5 s for the tray's counts, 8 s for the Judge history.
    /// </summary>
    public static ReaderTimeouts Production { get; } = new(
        AlertQueueReader.DefaultTimeout,
        ReadOnlyQuery.DefaultTimeout,
        ConnectorHookTotalsReader.DefaultTimeout,
        HourlyActivityReader.DefaultTimeout,
        MutationReader.DefaultTimeout,
        EventStreamReader.DefaultTimeout,
        ConnectorHookTotalsReader.DefaultTimeout,
        ReadOnlyQuery.DefaultTimeout);

    /// <summary>One limit for every kind of read. What a test composition uses: its suite's ceiling for a thing that happens on its own.</summary>
    public static ReaderTimeouts Uniform(TimeSpan limit) => new(limit, limit, limit, limit, limit, limit, limit, limit);
}
