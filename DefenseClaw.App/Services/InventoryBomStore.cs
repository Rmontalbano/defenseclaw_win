using DefenseClaw.Core.Inventory;

namespace DefenseClaw.App.Services;

/// <summary>
/// The AI BOM the operator last generated on the Inventory page (CUST-275), held for the Overview's coverage rows (CUST-329). Holds, never
/// reads and never runs: <c>aibom scan</c> is a state-changing command that only the Inventory page's review starts, so the Overview shows what
/// that run left and says when it was made, or that there is none. Process-lifetime memory only; nothing is written to disk.
/// </summary>
internal sealed class InventoryBomStore
{
    private readonly object _lock = new();
    private InventoryBomSnapshot? _snapshot;
    private DateTimeOffset? _capturedAt;
    private IReadOnlyList<InventoryBomKind> _scanned = Array.Empty<InventoryBomKind>();

    /// <summary>Raised after a snapshot was published, on the thread that published it.</summary>
    public event EventHandler? Changed;

    /// <summary>The snapshot of the last good run; null before the first one.</summary>
    public InventoryBomSnapshot? Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>When that run finished.</summary>
    public DateTimeOffset? CapturedAt
    {
        get
        {
            lock (_lock)
            {
                return _capturedAt;
            }
        }
    }

    /// <summary>The kinds that run covered (<c>--only</c>); empty means all of them.</summary>
    public IReadOnlyList<InventoryBomKind> Scanned
    {
        get
        {
            lock (_lock)
            {
                return _scanned;
            }
        }
    }

    /// <summary>A run produced <paramref name="snapshot"/>; it replaces the one held.</summary>
    public void Publish(InventoryBomSnapshot snapshot, IReadOnlyList<InventoryBomKind> scanned, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scanned);

        lock (_lock)
        {
            _snapshot = snapshot;
            _scanned = scanned.ToArray();
            _capturedAt = capturedAt;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
