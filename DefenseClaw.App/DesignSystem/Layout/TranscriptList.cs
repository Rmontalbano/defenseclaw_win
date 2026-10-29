using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The view half of an Activity entry's output list: attached to a virtualizing <see cref="ListBox"/> by
/// <see cref="EnableProperty"/>, it keeps the list on its newest line while it is following, works out when the
/// operator has scrolled away (or back), and gives it Ctrl+C for the selected lines. It exists as an attached
/// behavior, not code-behind, because the list lives in a <c>DataTemplate</c> - one per entry - and a template has
/// nowhere to put event handlers.
/// <para>
/// <b>Following.</b> <see cref="FollowProperty"/> is bound two-way to the entry's <c>IsFollowing</c>. While it is
/// true, a change to the collection (or the list becoming visible, or the flag being turned on) schedules one scroll to
/// the last item at <see cref="DispatcherPriority.Background"/> - the coalescing the Logs panel and the Updates console
/// use, so a tick that appends a hundred lines costs one scroll, not a hundred layout passes.
/// </para>
/// <para>
/// <b>Un-following.</b> Only the operator's own input can turn it off: a mouse-wheel notch, a key, or the scrollbar. The
/// scroll those cause is looked at once it has been laid out (again at background priority), and following is set to
/// "is the last line in view". A scroll the list makes itself, or one that the collection's growth or a trim causes, is
/// never taken for the operator scrolling up, so a burst of output cannot switch following off on its own, and scrolling
/// back to the end switches it on again without touching the checkbox.
/// </para>
/// <para>
/// <b>Recycling.</b> The Activity panel's card list reuses a card's elements for another invocation as it scrolls, so this
/// list is handed a different entry (its <c>DataContext</c>, and with it the collection it shows) many times in its life
/// and nothing about an entry may stay behind in it. Everything the operator did to a list is therefore also kept on the
/// entry's <see cref="ActivityTranscript"/> - where they were reading, and what they selected - and this behavior puts it
/// back when a list starts showing the entry (<c>CompleteRestore</c>): the selection, then the end when the
/// entry follows or the reading position when it does not. The restore waits for the first layout that has the new lines
/// in it, and runs inside that layout pass, so the frame that is drawn is already in the right place. Until it has run the
/// list is not the operator's (its selection and scroll are the swap's, not theirs) and is not listened to. Work that was
/// queued for the previous entry (a scroll to the end, a follow check) is dropped when the entry changes, not run against
/// the new one.
/// </para>
/// <para>
/// <b>Views vs. templates.</b> Listeners are attached on Loaded and dropped on Unloaded, both idempotent: the panel's view
/// is cached and the navigation frame swaps it out and back, which raises Unloaded and Loaded each time.
/// </para>
/// </summary>
public static class TranscriptList
{
    /// <summary>Set to true on the <see cref="ListBox"/> to turn the behavior on.</summary>
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable",
        typeof(bool),
        typeof(TranscriptList),
        new PropertyMetadata(false, OnEnableChanged));

    /// <summary>Whether the list keeps its newest line in view. Bind two-way.</summary>
    public static readonly DependencyProperty FollowProperty = DependencyProperty.RegisterAttached(
        "Follow",
        typeof(bool),
        typeof(TranscriptList),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnFollowChanged));

    private static readonly DependencyProperty WatcherProperty = DependencyProperty.RegisterAttached(
        "Watcher",
        typeof(Watcher),
        typeof(TranscriptList));

    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);

    public static bool GetFollow(DependencyObject element) => (bool)element.GetValue(FollowProperty);

    public static void SetFollow(DependencyObject element, bool value) => element.SetValue(FollowProperty, value);

    private static void OnEnableChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        if (e.NewValue is true)
        {
            if (sender.GetValue(WatcherProperty) is null)
            {
                var watcher = new Watcher(list);
                sender.SetValue(WatcherProperty, watcher);
                watcher.Start();
            }
        }
        else if (sender.GetValue(WatcherProperty) is Watcher existing)
        {
            existing.Stop();
            sender.ClearValue(WatcherProperty);
        }
    }

    private static void OnFollowChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender.GetValue(WatcherProperty) is Watcher watcher)
        {
            watcher.FollowChanged((bool)e.NewValue);
        }
    }

    private sealed class Watcher
    {
        /// <summary>
        /// Layout passes a restore waits for a list that is visible but whose scroll viewer does not yet agree with its items
        /// before it goes ahead anyway: better a restore that is a pass early than a list that is never handed back to the
        /// operator.
        /// </summary>
        private const int MaxRestorePasses = 30;

        private readonly ListBox _list;
        private INotifyCollectionChanged? _items;
        private TranscriptCollection? _transcript;

        /// <summary>The entry the list shows, as far as this watcher has caught up (its <c>DataContext</c> is the truth).</summary>
        private ActivityRow? _row;

        /// <summary>Counts entries shown; work queued for one entry checks it still is the one before it acts.</summary>
        private int _generation;

        /// <summary>True from the moment the list is handed an entry until <see cref="CompleteRestore"/> has put that entry's state back.</summary>
        private bool _restoring;

        private bool _headSubscribed;
        private bool _layoutHooked;
        private int _restorePasses;
        private bool _scrollScheduled;
        private bool _checkScheduled;

        /// <summary>The line the operator was reading when lines began leaving the head; null when there is nothing to restore.</summary>
        private ActivityOutputLine? _anchor;

        public Watcher(ListBox list) => _list = list;

        public void Start()
        {
            _list.Loaded += OnLoaded;
            _list.Unloaded += OnUnloaded;
            _list.IsVisibleChanged += OnIsVisibleChanged;
            _list.DataContextChanged += OnDataContextChanged;
            _list.SelectionChanged += OnSelectionChanged;

            // The operator's own scrolling - and only that - can change whether the list follows.
            _list.PreviewMouseWheel += OnUserInput;
            _list.PreviewKeyDown += OnUserInput;
            _list.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(OnUserScrollBar));
            _list.RequestBringIntoView += OnRequestBringIntoView;

            _ = _list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopy, OnCanCopy));
            _ = _list.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, OnSelectAll, OnCanSelectAll));

            NestedScroll.SetForwardWheel(_list, true);

            if (_list.IsLoaded)
            {
                Attach();
            }
        }

        public void Stop()
        {
            Detach();
            _list.Loaded -= OnLoaded;
            _list.Unloaded -= OnUnloaded;
            _list.IsVisibleChanged -= OnIsVisibleChanged;
            _list.DataContextChanged -= OnDataContextChanged;
            _list.SelectionChanged -= OnSelectionChanged;
            _list.PreviewMouseWheel -= OnUserInput;
            _list.PreviewKeyDown -= OnUserInput;
            _list.RemoveHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(OnUserScrollBar));
            _list.RequestBringIntoView -= OnRequestBringIntoView;
        }

        /// <summary>
        /// Keeps the list's own scrolling inside the list. Scrolling an item into view asks every scroll viewer above it to
        /// make it visible too, so following a running transcript would drag the whole Activity page to the bottom of that
        /// entry on every tick. The list's own scroll viewer has already dealt with the request by the time it reaches here.
        /// A list with keyboard focus is the exception: an operator tabbing into a list below the fold, or arrowing through
        /// it, is owed a page that scrolls to where the focus is.
        /// </summary>
        private void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e) =>
            e.Handled = !_list.IsKeyboardFocusWithin;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Attach();
            ScheduleScrollToEnd();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => EnsureBound();

        private void Attach()
        {
            if (_items is not null)
            {
                return;
            }

            _items = _list.Items;
            _items.CollectionChanged += OnItemsChanged;

            // The entry may have changed while the list was out of the tree (that is what recycling does).
            EnsureBound();
            SubscribeHead();

            if (_restoring)
            {
                HookLayout();
            }
        }

        private void Detach()
        {
            if (_items is null)
            {
                return;
            }

            _items.CollectionChanged -= OnItemsChanged;
            _items = null;
            UnhookLayout();
            UnsubscribeHead();
        }

        private void SubscribeHead()
        {
            if (!_headSubscribed && _transcript is not null)
            {
                _headSubscribed = true;
                _transcript.HeadRemoving += OnHeadRemoving;
            }
        }

        private void UnsubscribeHead()
        {
            if (_headSubscribed && _transcript is not null)
            {
                _transcript.HeadRemoving -= OnHeadRemoving;
            }

            _headSubscribed = false;
        }

        /// <summary>
        /// Catches the watcher up with the entry the list is showing now. Called first by everything that can run after the
        /// list was handed another entry - the <c>DataContext</c> change itself and the binding updates it causes (the
        /// items, the follow flag, the selection the swap clears) come in no promised order, and the <c>DataContext</c> is
        /// already the new entry for all of them.
        /// </summary>
        private void EnsureBound()
        {
            var row = _list.DataContext as ActivityRow;
            if (ReferenceEquals(row, _row))
            {
                return;
            }

            UnsubscribeHead();
            _transcript = null;

            // Whatever was queued for the last entry is not for this one.
            _generation++;
            _scrollScheduled = false;
            _checkScheduled = false;
            _anchor = null;
            UnhookLayout();

            _row = row;
            _restoring = row is not null;
            _restorePasses = 0;
            if (row is null)
            {
                return;
            }

            _transcript = row.Output;
            if (_items is not null)
            {
                SubscribeHead();
                HookLayout();
            }
        }

        private void HookLayout()
        {
            if (!_layoutHooked)
            {
                _layoutHooked = true;
                _list.LayoutUpdated += OnLayoutUpdated;
            }
        }

        private void UnhookLayout()
        {
            if (_layoutHooked)
            {
                _layoutHooked = false;
                _list.LayoutUpdated -= OnLayoutUpdated;
            }
        }

        /// <summary>
        /// Puts the entry's state back into the list once a layout has had the entry's lines in it. Runs inside that layout
        /// pass: the scroll it makes is laid out by the pass that follows, before anything is drawn.
        /// </summary>
        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (!_restoring)
            {
                UnhookLayout();
                return;
            }

            EnsureBound();
            if (_row is null || !ReferenceEquals(_list.ItemsSource, _transcript))
            {
                // The items have not been swapped in yet (or there is no entry): a later pass.
                return;
            }

            ScrollViewer? scroll = null;
            var count = _list.Items.Count;
            if (count > 0)
            {
                // Not laid out (a shut card's list never is): wait, cheaply - this runs for every layout pass in the window.
                if (!_list.IsVisible || (scroll = FindScrollViewer(_list)) is null || scroll.ViewportHeight <= 0)
                {
                    return;
                }

                // The list scrolls by item, so a scroll viewer that has measured these lines has an extent of exactly that many.
                if (Math.Abs(scroll.ExtentHeight - count) >= 1 && ++_restorePasses < MaxRestorePasses)
                {
                    return;
                }
            }

            CompleteRestore(scroll);
        }

        private void CompleteRestore(ScrollViewer? scroll)
        {
            UnhookLayout();

            try
            {
                Restore(_row!, scroll);
            }
            finally
            {
                // Only now is the list the operator's: until here the selection changes below are ours, not theirs.
                _restoring = false;
            }
        }

        private void Restore(ActivityRow row, ScrollViewer? scroll)
        {
            var transcript = row.Transcript;

            // Selection: what the swap left (nothing, or the previous entry's lines) is not this entry's.
            if (_list.SelectedItems.Count > 0)
            {
                _list.UnselectAll();
            }

            if (transcript.IsEverythingSelected)
            {
                _list.SelectAll();
            }
            else
            {
                foreach (var line in transcript.LinesToReselect())
                {
                    _ = _list.SelectedItems.Add(line);
                }
            }

            // Scroll: the end for an entry that follows, else the line the operator was reading.
            if (scroll is null || _list.Items.Count == 0)
            {
                return;
            }

            if (row.IsFollowing)
            {
                scroll.ScrollToEnd();
            }
            else
            {
                scroll.ScrollToVerticalOffset(transcript.ReadingIndex);
            }
        }

        /// <summary>The follow flag changed: the operator (or the checkbox) did it, or the list was handed an entry with another value.</summary>
        public void FollowChanged(bool follow)
        {
            EnsureBound();
            if (_restoring || _row is null)
            {
                return;
            }

            if (follow)
            {
                _row.Transcript.ReadingSequence = null;
                ScheduleScrollToEnd();
            }
            else if (FindScrollViewer(_list) is { } scroll)
            {
                // The list stays where it is when following stops: that is where the operator is reading.
                NoteReading(scroll);
            }
        }

        /// <summary>Records the first line in view on the entry, so the next list that shows it can start there.</summary>
        private void NoteReading(ScrollViewer scroll)
        {
            var index = (int)scroll.VerticalOffset;
            if (_row is not null && index >= 0 && index < _list.Items.Count && _list.Items[index] is ActivityOutputLine line)
            {
                _row.Transcript.ReadingSequence = line.Sequence;
            }
        }

        /// <summary>Mirrors what the operator selected onto the entry (a swap that clears the list is not the operator's doing).</summary>
        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EnsureBound();
            if (_restoring || _row is null || !ReferenceEquals(_list.ItemsSource, _transcript))
            {
                return;
            }

            _row.Transcript.NoteSelectionChanged(e.AddedItems.OfType<ActivityOutputLine>(), e.RemovedItems.OfType<ActivityOutputLine>());
        }

        /// <summary>
        /// Lines are about to leave the head of the transcript (the invocation trimmed itself). A list that is following
        /// goes to its end anyway; one the operator scrolled up in would keep its numeric offset and so slide down by the
        /// lines removed, out from under what is being read. So note the first line in view now, and put it back at the
        /// top of the view once the removal has been laid out (one restore per burst, at background priority).
        /// </summary>
        private void OnHeadRemoving(object? sender, EventArgs e)
        {
            if (_restoring || _anchor is not null || GetFollow(_list) || FindScrollViewer(_list) is not { } scroll)
            {
                return;
            }

            var index = (int)scroll.VerticalOffset;
            if (index < 0 || index >= _list.Items.Count)
            {
                return;
            }

            var generation = _generation;
            _anchor = _list.Items[index] as ActivityOutputLine;
            _ = _list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => RestoreAnchor(generation)));
        }

        private void RestoreAnchor(int generation)
        {
            var anchor = _anchor;
            _anchor = null;

            if (generation != _generation || anchor is null || GetFollow(_list) || FindScrollViewer(_list) is not { } scroll)
            {
                return;
            }

            // The anchor may itself have been trimmed away (or be the marker, which is replaced on every trim): then the
            // reader was at the head of the transcript, and the head is where they stay.
            var index = _list.Items.IndexOf(anchor);
            scroll.ScrollToVerticalOffset(Math.Max(0, index));
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            EnsureBound();

            // A bulk change arrives as a Reset, which a list may answer by dropping its selection: say what it kept.
            if (e.Action == NotifyCollectionChangedAction.Reset
                && !_restoring
                && _row is not null
                && ReferenceEquals(_list.ItemsSource, _transcript))
            {
                _row.Transcript.ReplaceSelection(_list.SelectedItems.OfType<ActivityOutputLine>());
            }

            ScheduleScrollToEnd();
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Lines that arrived while the entry was collapsed scheduled nothing worth doing (a collapsed list is not laid
            // out), so a list that has just been opened goes to its end here.
            if (e.NewValue is true)
            {
                ScheduleScrollToEnd();
            }
        }

        public void ScheduleScrollToEnd()
        {
            if (_scrollScheduled || !GetFollow(_list) || !_list.IsVisible)
            {
                return;
            }

            _scrollScheduled = true;
            var generation = _generation;
            _ = _list.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    _scrollScheduled = false;

                    // Re-checked here: the operator may have scrolled away between scheduling and running.
                    if (!_restoring && GetFollow(_list) && _list.Items.Count > 0)
                    {
                        _list.ScrollIntoView(_list.Items[^1]);
                    }
                }));
        }

        private void OnUserInput(object sender, InputEventArgs e)
        {
            if (e is KeyEventArgs key && !IsScrollingKey(key.Key))
            {
                return;
            }

            ScheduleFollowCheck();
        }

        private void OnUserScrollBar(object sender, ScrollEventArgs e) => ScheduleFollowCheck();

        /// <summary>
        /// Looks at where the list ended up once the operator's input has been laid out, and follows exactly when the last
        /// line is in view. Coalesced: a scrollbar drag raises a scroll event per pixel. When it is not the last line, the
        /// first one in view is recorded on the entry.
        /// </summary>
        private void ScheduleFollowCheck()
        {
            if (_checkScheduled)
            {
                return;
            }

            _checkScheduled = true;
            var generation = _generation;
            _ = _list.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    _checkScheduled = false;
                    if (_restoring || FindScrollViewer(_list) is not { } scroll)
                    {
                        return;
                    }

                    var atEnd = IsAtEnd(scroll);
                    SetFollow(_list, atEnd);
                    if (_row is not null)
                    {
                        if (atEnd)
                        {
                            _row.Transcript.ReadingSequence = null;
                        }
                        else
                        {
                            NoteReading(scroll);
                        }
                    }
                }));
        }

        private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _list.SelectedItems.Count > 0;

        private void OnCopy(object sender, ExecutedRoutedEventArgs e)
        {
            if (_list.DataContext is ActivityRow row)
            {
                row.CopyLines(_list.SelectedItems.OfType<ActivityOutputLine>());
                e.Handled = true;
            }
        }

        private void OnCanSelectAll(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _list.Items.Count > 0;

        private void OnSelectAll(object sender, ExecutedRoutedEventArgs e)
        {
            _list.SelectAll();
            e.Handled = true;
        }

        /// <summary>The keys that move the list's view (the arrows, paging and Home/End); typing a letter never does.</summary>
        private static bool IsScrollingKey(Key key) =>
            key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End;

        /// <summary>
        /// The last item is in view. The list scrolls by item, so the offset counts items and one item of slack is exact
        /// enough (a partly visible last row still counts as the end).
        /// </summary>
        private static bool IsAtEnd(ScrollViewer scroll) => scroll.VerticalOffset >= scroll.ScrollableHeight - 1.0;

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer self)
            {
                return self;
            }

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
