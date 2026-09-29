namespace DefenseClaw.Core.Audit;

/// <summary>
/// Runs a reader's query on the thread pool so the caller gets its thread back at once.
/// <para>
/// Microsoft.Data.Sqlite's <c>async</c> methods are not asynchronous: SQLite has no asynchronous
/// I/O, so <c>OpenAsync</c>, <c>ExecuteReaderAsync</c>, <c>ReadAsync</c> and the rest execute
/// synchronously and hand back an already-completed task. An <c>async</c> method that only awaits
/// those therefore never yields — every <c>ConfigureAwait(false)</c> in it is moot because there
/// is no suspension to resume from — and runs start to finish on the thread that called it. The
/// callers of <see cref="AuditReader"/> and <see cref="DefenseClaw.Core.Inventory.InventoryReader"/>
/// are view-models awaiting from the dispatcher thread, so an "async" query froze the window for
/// as long as SQLite took: 0.3-5 s on the live audit.db and 15 s for the inventory view.
/// </para>
/// <para>
/// Every public query method of both readers is therefore <c>ReaderOffload.Run(() =&gt; core(...))</c>:
/// the body starts on a pool thread and the returned task is genuinely pending. The token is
/// handed to <see cref="Task.Run(Func{Task{TResult}}, CancellationToken)"/> (a token that is already
/// cancelled never starts the query) and on into the body, where it is observed between statements
/// and rows — never inside one, since SQLite cannot be interrupted mid-<c>sqlite3_step</c> through
/// this library. Exceptions, including argument validation, still arrive through the returned task
/// exactly as they did when the methods were <c>async</c>.
/// </para>
/// </summary>
internal static class ReaderOffload
{
    internal static Task<T> Run<T>(Func<Task<T>> body, CancellationToken cancellationToken) =>
        Task.Run(body, cancellationToken);
}
