using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

// The Core suite drives the interrupt helper and reads the reader's statements directly: see
// AuditSeverityTests (an endless recursive query stopped by a token, EXPLAIN of the distinct-values scan).
[assembly: InternalsVisibleTo("DefenseClaw.Tests")]

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
/// and rows. A reader that wants a token to end a <em>running</em> statement as well registers
/// <see cref="InterruptOnCancel"/> on its connection: the audit reader does, because a text search
/// is a scan of the whole window and the panel abandons one for every keystroke; the interrupt
/// surfaces as <c>SQLITE_INTERRUPT</c>, which <see cref="Run"/> turns back into an
/// <see cref="OperationCanceledException"/>. Exceptions, including argument validation, still
/// arrive through the returned task exactly as they did when the methods were <c>async</c>.
/// </para>
/// </summary>
internal static class ReaderOffload
{
    /// <summary>SQLite's primary result code for a statement stopped by <c>sqlite3_interrupt</c>.</summary>
    private const int SqliteInterrupt = 9;

    internal static async Task<T> Run<T>(Func<Task<T>> body, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(body, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (cancellationToken.IsCancellationRequested && ex.SqliteErrorCode == SqliteInterrupt)
        {
            throw new OperationCanceledException("The query was interrupted because its token was cancelled.", ex, cancellationToken);
        }
    }

    /// <summary>
    /// Makes cancelling <paramref name="cancellationToken"/> stop the statement that is running on
    /// <paramref name="connection"/> (<c>sqlite3_interrupt</c>, which SQLite documents as safe to call from any
    /// thread). Dispose the returned registration <em>before</em> the connection: disposing waits for a callback
    /// that is already running, so no interrupt can reach a handle that has been closed. A token that cannot be
    /// cancelled costs nothing.
    /// </summary>
    internal static CancellationTokenRegistration InterruptOnCancel(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || connection.Handle is not { } handle)
        {
            return default;
        }

        return cancellationToken.Register(
            static state =>
            {
                var database = (sqlite3)state!;
                if (!database.IsInvalid && !database.IsClosed)
                {
                    raw.sqlite3_interrupt(database);
                }
            },
            handle);
    }
}
