using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can drop rows from the front and append new ones
/// as one change, for a bound list that is a sliding window over a stream (the Logs panel).
/// <para>
/// <b>Why a reset, not a range notification.</b> WPF's collection views throw
/// <see cref="NotSupportedException"/> for a <c>CollectionChanged</c> that carries more than one
/// item, so the only way to announce a batch as a single event is
/// <see cref="NotifyCollectionChangedAction.Reset"/>. On a virtualizing <c>ListBox</c> that costs
/// no more than the per-item form (only the visible containers are rebuilt), keeps the surviving
/// selection selected, and for a pure front removal leaves the scroll offset exactly where per-item
/// removal leaves it; the layout tests beside this type's tests pin that down. (When a burst is also
/// appended, per-item removal partly compensates the offset and a reset keeps the same row index -
/// WPF's panel does not anchor on an item either way, so a reader scrolled back on a full list
/// drifts toward newer lines in both forms.)
/// </para>
/// </summary>
/// <typeparam name="T">Row type.</typeparam>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Appends <paramref name="items"/>, then drops the oldest rows so <see cref="Collection{T}.Count"/>
    /// does not exceed <paramref name="maxCount"/>.
    /// <para>
    /// When nothing has to be dropped this is exactly one <c>Add</c> per item, as before, so a
    /// growing list keeps its incremental notifications. When rows do have to go it is one array
    /// shift and one <see cref="NotifyCollectionChangedAction.Reset"/> for the whole batch,
    /// however many rows that is - the old form was one <c>RemoveAt(0)</c> per excess row, each
    /// an O(count) shift with its own notification. Incoming rows that would be dropped straight
    /// away are never inserted at all.
    /// </para>
    /// </summary>
    public void AppendCapped(IReadOnlyList<T> items, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        var excess = Count + items.Count - maxCount;
        if (excess <= 0 || Items is not List<T> list)
        {
            foreach (var item in items)
            {
                Add(item);
            }

            TrimFront(Count - maxCount);
            return;
        }

        CheckReentrancy();

        var dropExisting = Math.Min(excess, list.Count);
        if (dropExisting > 0)
        {
            list.RemoveRange(0, dropExisting);
        }

        // Whatever excess the existing rows could not absorb is the oldest of the incoming ones.
        for (var i = excess - dropExisting; i < items.Count; i++)
        {
            list.Add(items[i]);
        }

        RaiseReset();
    }

    /// <summary>
    /// Removes the first <paramref name="count"/> rows with one array shift and one reset
    /// notification. A count of zero or less does nothing.
    /// </summary>
    public void RemoveFirst(int count)
    {
        if (count <= 0)
        {
            return;
        }

        count = Math.Min(count, Count);
        if (Items is not List<T> list)
        {
            for (var i = 0; i < count; i++)
            {
                RemoveAt(0);
            }

            return;
        }

        CheckReentrancy();
        list.RemoveRange(0, count);
        RaiseReset();
    }

    private void TrimFront(int excess)
    {
        if (excess > 0)
        {
            RemoveFirst(excess);
        }
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
