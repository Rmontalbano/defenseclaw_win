using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// <see cref="BatchObservableCollection{T}"/>: the sliding window behind the Logs list. Two things
/// are pinned - the collection's own contract (contents, notifications), and that announcing a
/// batch as one <c>Reset</c> leaves a virtualizing <c>ListBox</c>'s scroll offset and selection
/// exactly where the old one-<c>RemoveAt(0)</c>-per-row form left them, which is what the panel's
/// coalesced scroll and paused-tail behaviour depend on.
/// </summary>
public sealed class BatchObservableCollectionTests
{
    private readonly ITestOutputHelper _output;

    public BatchObservableCollectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static int[] Snapshot(BatchObservableCollection<int> collection) => collection.ToArray();

    private static List<NotifyCollectionChangedAction> Record(BatchObservableCollection<int> collection)
    {
        var seen = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => seen.Add(e.Action);
        return seen;
    }

    // ------------------------------------------------------------------ AppendCapped

    [Fact]
    public void Under_the_cap_it_adds_row_by_row_with_ordinary_add_notifications()
    {
        var collection = new BatchObservableCollection<int> { 1, 2 };
        var seen = Record(collection);

        collection.AppendCapped(new[] { 3, 4, 5 }, maxCount: 10);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Snapshot(collection));
        Assert.Equal(Enumerable.Repeat(NotifyCollectionChangedAction.Add, 3), seen);
    }

    [Fact]
    public void Exactly_at_the_cap_still_adds_row_by_row()
    {
        var collection = new BatchObservableCollection<int> { 1, 2 };
        var seen = Record(collection);

        collection.AppendCapped(new[] { 3, 4 }, maxCount: 4);

        Assert.Equal(new[] { 1, 2, 3, 4 }, Snapshot(collection));
        Assert.Equal(2, seen.Count);
        Assert.All(seen, action => Assert.Equal(NotifyCollectionChangedAction.Add, action));
    }

    [Fact]
    public void Past_the_cap_the_oldest_rows_go_and_the_whole_batch_is_one_reset()
    {
        var collection = new BatchObservableCollection<int> { 1, 2, 3, 4, 5 };
        var seen = Record(collection);

        collection.AppendCapped(new[] { 6, 7, 8 }, maxCount: 6);

        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8 }, Snapshot(collection));
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, seen);
    }

    [Fact]
    public void A_batch_larger_than_the_cap_keeps_only_its_own_newest_rows()
    {
        var collection = new BatchObservableCollection<int> { 1, 2, 3 };
        var seen = Record(collection);

        collection.AppendCapped(new[] { 10, 11, 12, 13, 14, 15, 16 }, maxCount: 4);

        Assert.Equal(new[] { 13, 14, 15, 16 }, Snapshot(collection));
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, seen);
    }

    [Fact]
    public void Count_and_indexer_property_changes_are_raised_with_the_reset()
    {
        var collection = new BatchObservableCollection<int> { 1, 2, 3 };
        var properties = new List<string?>();
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.AppendCapped(new[] { 4, 5 }, maxCount: 3);

        Assert.Contains("Count", properties);
        Assert.Contains("Item[]", properties);
    }

    [Fact]
    public void An_empty_batch_on_a_full_collection_changes_nothing_and_says_nothing()
    {
        var collection = new BatchObservableCollection<int> { 1, 2, 3 };
        var seen = Record(collection);

        collection.AppendCapped(Array.Empty<int>(), maxCount: 3);

        Assert.Equal(new[] { 1, 2, 3 }, Snapshot(collection));
        Assert.Empty(seen);
    }

    [Fact]
    public void RemoveFirst_drops_the_front_rows_with_one_reset_and_clamps_to_the_count()
    {
        var collection = new BatchObservableCollection<int> { 1, 2, 3, 4, 5 };
        var seen = Record(collection);

        collection.RemoveFirst(2);
        Assert.Equal(new[] { 3, 4, 5 }, Snapshot(collection));

        collection.RemoveFirst(0);
        collection.RemoveFirst(-3);
        Assert.Single(seen);

        collection.RemoveFirst(99);

        Assert.Empty(collection);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset }, seen);
    }

    [Fact]
    public void A_burst_on_a_full_window_is_one_notification_where_it_used_to_be_thousands()
    {
        // The Logs panel's worst case: 2,000 lines (LogTailerOptions.MaxLinesPerBatch) arriving on a
        // list already at its 5,000-row cap. Before: 2,000 Add + 2,000 RemoveAt(0) notifications and
        // 2,000 array shifts. After: one array shift and one notification.
        const int cap = 5000;
        const int burst = 2000;

        var legacy = new System.Collections.ObjectModel.ObservableCollection<int>(Enumerable.Range(0, cap));
        var legacyNotifications = 0;
        legacy.CollectionChanged += (_, _) => legacyNotifications++;
        foreach (var n in Enumerable.Range(cap, burst))
        {
            legacy.Add(n);
        }

        while (legacy.Count > cap)
        {
            legacy.RemoveAt(0);
        }

        var batched = new BatchObservableCollection<int>();
        batched.AppendCapped(Enumerable.Range(0, cap).ToArray(), cap);
        var notifications = 0;
        batched.CollectionChanged += (_, _) => notifications++;
        batched.AppendCapped(Enumerable.Range(cap, burst).ToArray(), cap);

        _output.WriteLine($"2,000-line burst on a full 5,000-row list: {legacyNotifications:N0} notifications before, {notifications} after.");

        Assert.Equal(burst * 2, legacyNotifications);
        Assert.Equal(1, notifications);
        Assert.Equal(legacy.ToArray(), Snapshot(batched));
    }

    // ------------------------------------------------------------------ what a ListBox does with it

    /// <summary>What a laid-out list looks like afterwards.</summary>
    private sealed record ListState(double VerticalOffset, int[] Selected, int SelectionChangedEvents, int CollectionChangedEvents);

    private sealed class Row
    {
        public Row(int n)
        {
            N = n;
        }

        public int N { get; }
    }

    /// <summary>
    /// A virtualizing, recycling, item-scrolling ListBox (the Logs panel's settings) with its templates
    /// supplied inline, so no application theme has to be loaded for the visual tree to exist.
    /// </summary>
    private static (ListBox List, ScrollViewer Scroller) BuildList(BatchObservableCollection<Row> rows)
    {
        var list = new ListBox
        {
            Width = 400,
            Height = 200,
            SelectionMode = SelectionMode.Extended,
            ItemsSource = rows,
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBox'>" +
                "<ScrollViewer Focusable='False' CanContentScroll='{TemplateBinding ScrollViewer.CanContentScroll}'><ItemsPresenter/></ScrollViewer>" +
                "</ControlTemplate>"),
            ItemContainerStyle = (Style)XamlReader.Parse(
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'>" +
                "<Setter Property='Height' Value='20'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'><Border><ContentPresenter/></Border></ControlTemplate></Setter.Value></Setter>" +
                "</Style>"),
        };

        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);

        var textBlock = new FrameworkElementFactory(typeof(TextBlock));
        textBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Row.N)));
        list.ItemTemplate = new DataTemplate { VisualTree = textBlock };

        Layout(list);
        return (list, FindScrollViewer(list)!);
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(400, 200));
        element.Arrange(new Rect(0, 0, 400, 200));
        element.UpdateLayout();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Fills a list with 5,000 rows, scrolls to row 2,000, selects rows 3 (about to be trimmed), 2,003
    /// and 2,010, runs <paramref name="mutate"/> on the collection, lays out again, and reports the result.
    /// </summary>
    private static ListState Scenario(Action<BatchObservableCollection<Row>> mutate)
    {
        var rows = new BatchObservableCollection<Row>();
        foreach (var n in Enumerable.Range(0, 5000))
        {
            rows.Add(new Row(n));
        }

        var (list, scroller) = BuildList(rows);

        scroller.ScrollToVerticalOffset(2000);
        Layout(list);
        list.SelectedItems.Add(rows[3]);
        list.SelectedItems.Add(rows[2003]);
        list.SelectedItems.Add(rows[2010]);
        Layout(list);

        var selectionChanges = 0;
        var collectionChanges = 0;
        list.SelectionChanged += (_, _) => selectionChanges++;
        rows.CollectionChanged += (_, _) => collectionChanges++;

        mutate(rows);
        Layout(list);

        return new ListState(
            scroller.VerticalOffset,
            list.SelectedItems.Cast<Row>().Select(r => r.N).OrderBy(n => n).ToArray(),
            selectionChanges,
            collectionChanges);
    }

    [Fact]
    public void Dropping_the_front_in_one_reset_scrolls_and_selects_exactly_like_dropping_row_by_row()
    {
        var (perRow, batched) = StaThread.Run(() =>
        {
            var perRowState = Scenario(rows =>
            {
                for (var i = 0; i < 100; i++)
                {
                    rows.RemoveAt(0);
                }
            });

            var batchedState = Scenario(rows => rows.RemoveFirst(100));
            return (perRowState, batchedState);
        });

        _output.WriteLine($"per row: offset {perRow.VerticalOffset}, selected [{string.Join(",", perRow.Selected)}], collection events {perRow.CollectionChangedEvents}");
        _output.WriteLine($"batched: offset {batched.VerticalOffset}, selected [{string.Join(",", batched.Selected)}], collection events {batched.CollectionChangedEvents}");

        Assert.Equal(100, perRow.CollectionChangedEvents);
        Assert.Equal(1, batched.CollectionChangedEvents);
        Assert.Equal(perRow.VerticalOffset, batched.VerticalOffset);

        // Row 3 was trimmed and so leaves the selection; the two rows that survive stay selected.
        Assert.Equal(new[] { 2003, 2010 }, perRow.Selected);
        Assert.Equal(perRow.Selected, batched.Selected);
        Assert.Equal(perRow.SelectionChangedEvents, batched.SelectionChangedEvents);
    }

    [Fact]
    public void A_burst_appended_to_a_scrolled_full_list_keeps_the_selection_and_a_valid_offset()
    {
        // The paused-tail case: the operator has scrolled back and selected lines while a burst arrives.
        // Selection is identical either way. The scroll offset is NOT: after 300 per-row removals the
        // ListBox has partly compensated (here 2,000 -> 1,886), after one reset it stays on the same
        // row index (2,000). Neither pins the lines being read - WPF's virtualizing panel does not
        // anchor on an item - so a paused reader on a full buffer drifts toward newer lines in both
        // forms, and this asserts what does carry over rather than that the two drift identically.
        var (legacy, batched) = StaThread.Run(() =>
        {
            var legacyState = Scenario(rows =>
            {
                for (var n = 5000; n < 5300; n++)
                {
                    rows.Add(new Row(n));
                }

                while (rows.Count > 5000)
                {
                    rows.RemoveAt(0);
                }
            });

            var batchedState = Scenario(rows =>
                rows.AppendCapped(Enumerable.Range(5000, 300).Select(n => new Row(n)).ToArray(), 5000));

            return (legacyState, batchedState);
        });

        _output.WriteLine($"add+trim: offset {legacy.VerticalOffset}, selected [{string.Join(",", legacy.Selected)}], collection events {legacy.CollectionChangedEvents}");
        _output.WriteLine($"batched:  offset {batched.VerticalOffset}, selected [{string.Join(",", batched.Selected)}], collection events {batched.CollectionChangedEvents}");

        Assert.Equal(600, legacy.CollectionChangedEvents);
        Assert.Equal(1, batched.CollectionChangedEvents);
        Assert.InRange(batched.VerticalOffset, 0, 4990);
        Assert.Equal(new[] { 2003, 2010 }, batched.Selected);
        Assert.Equal(legacy.Selected, batched.Selected);
        Assert.Equal(legacy.SelectionChangedEvents, batched.SelectionChangedEvents);
    }
}
