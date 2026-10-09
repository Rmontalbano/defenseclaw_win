using System.Diagnostics.CodeAnalysis;
using DefenseClaw.Core.Time;

namespace DefenseClaw.Core.Audit;

/// <summary>Settings shared by every <see cref="SnapshotMemo{TKey, TValue}"/>.</summary>
internal static class SnapshotMemo
{
    /// <summary>
    /// How long a remembered result is trusted while the probe keeps saying nothing changed. The probe is exact, so this is only the
    /// backstop for a failure nobody foresaw: the worst a wrong "unchanged" can do is leave a screen stale for this long.
    /// </summary>
    internal static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);
}

/// <summary>
/// What a reader remembers of its last answers - a few, by key (the query, the page size, the stream) - each with the
/// <see cref="AuditStamp"/> taken before it was read. A later call that takes the same stamp for the same key gets the remembered
/// object back: no connection, no statement, no row decoded, and the <em>same instance</em>, which is what lets a view-model tell
/// "nothing changed" from a reference comparison and skip rebuilding its list.
/// <para>
/// <b>The stamp is taken before the read.</b> A commit that lands between the stamp and the statement is in the result but not in
/// the stamp, so the next sample differs and the read repeats once - one redundant read, never a stale one. An unknown stamp is
/// never stored and never matches (no probe, a database that could not be sampled): the reader then does what it always did.
/// Failed and cancelled reads store nothing.
/// </para>
/// </summary>
internal sealed class SnapshotMemo<TKey, TValue>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private readonly int _capacity;
    private readonly TimeSpan _maxAge;
    private readonly TimeProvider _time;

    /// <param name="capacity">How many keys are remembered; the least recently used is dropped for a new one.</param>
    /// <param name="maxAge">How long a result may be reused; <see cref="SnapshotMemo.DefaultMaxAge"/> when null.</param>
    /// <param name="time">The clock the age is measured on (monotonic); the system's when null.</param>
    internal SnapshotMemo(int capacity = 1, TimeSpan? maxAge = null, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _maxAge = maxAge ?? SnapshotMemo.DefaultMaxAge;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The remembered result for <paramref name="key"/>, if it was read under a stamp that <see cref="AuditStamp.Matches"/> <paramref name="stamp"/> and is not too old.</summary>
    internal bool TryGet(TKey key, AuditStamp stamp, [MaybeNullWhen(false)] out TValue value)
    {
        value = default;
        if (!stamp.IsKnown)
        {
            return false;
        }

        lock (_gate)
        {
            var index = _entries.FindIndex(entry => EqualityComparer<TKey>.Default.Equals(entry.Key, key));
            if (index < 0)
            {
                return false;
            }

            var entry = _entries[index];
            if (!entry.Stamp.Matches(stamp) || entry.StoredAt.HasElapsed(_maxAge, _time))
            {
                return false;
            }

            if (index > 0)
            {
                _entries.RemoveAt(index);
                _entries.Insert(0, entry);
            }

            value = entry.Value;
            return true;
        }
    }

    /// <summary>Remembers <paramref name="value"/> as what <paramref name="key"/> read under <paramref name="stamp"/>. An unknown stamp remembers nothing.</summary>
    internal void Store(TKey key, AuditStamp stamp, TValue value)
    {
        if (!stamp.IsKnown)
        {
            return;
        }

        lock (_gate)
        {
            _ = _entries.RemoveAll(entry => EqualityComparer<TKey>.Default.Equals(entry.Key, key));
            _entries.Insert(0, new Entry(key, stamp, value, MonotonicStamp.Now(_time)));
            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(_capacity, _entries.Count - _capacity);
            }
        }
    }

    /// <summary>Forgets everything.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private sealed record Entry(TKey Key, AuditStamp Stamp, TValue Value, MonotonicStamp StoredAt);
}
