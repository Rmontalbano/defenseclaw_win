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
        if (e.NewValue is true && sender.GetValue(WatcherProperty) is Watcher watcher)
        {
            watcher.ScheduleScrollToEnd();
        }
    }

    private sealed class Watcher
    {
        private readonly ListBox _list;
        private INotifyCollectionChanged? _items;
        private TranscriptCollection? _transcript;
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

        private void Attach()
        {
            if (_items is not null)
            {
                return;
            }

            _items = _list.Items;
            _items.CollectionChanged += OnItemsChanged;

            _transcript = _list.ItemsSource as TranscriptCollection;
            if (_transcript is not null)
            {
                _transcript.HeadRemoving += OnHeadRemoving;
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

            if (_transcript is not null)
            {
                _transcript.HeadRemoving -= OnHeadRemoving;
                _transcript = null;
            }
        }

        /// <summary>
        /// Lines are about to leave the head of the transcript (the invocation trimmed itself). A list that is following
        /// goes to its end anyway; one the operator scrolled up in would keep its numeric offset and so slide down by the
        /// lines removed, out from under what is being read. So note the first line in view now, and put it back at the
        /// top of the view once the removal has been laid out (one restore per burst, at background priority).
        /// </summary>
        private void OnHeadRemoving(object? sender, EventArgs e)
        {
            if (_anchor is not null || GetFollow(_list) || FindScrollViewer(_list) is not { } scroll)
            {
                return;
            }

            var index = (int)scroll.VerticalOffset;
            if (index < 0 || index >= _list.Items.Count)
            {
                return;
            }

            _anchor = _list.Items[index] as ActivityOutputLine;
            _ = _list.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RestoreAnchor));
        }

        private void RestoreAnchor()
        {
            var anchor = _anchor;
            _anchor = null;

            if (anchor is null || GetFollow(_list) || FindScrollViewer(_list) is not { } scroll)
            {
                return;
            }

            // The anchor may itself have been trimmed away (or be the marker, which is replaced on every trim): then the
            // reader was at the head of the transcript, and the head is where they stay.
            var index = _list.Items.IndexOf(anchor);
            scroll.ScrollToVerticalOffset(Math.Max(0, index));
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleScrollToEnd();

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
            _ = _list.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _scrollScheduled = false;

                    // Re-checked here: the operator may have scrolled away between scheduling and running.
                    if (GetFollow(_list) && _list.Items.Count > 0)
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
        /// line is in view. Coalesced: a scrollbar drag raises a scroll event per pixel.
        /// </summary>
        private void ScheduleFollowCheck()
        {
            if (_checkScheduled)
            {
                return;
            }

            _checkScheduled = true;
            _ = _list.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    _checkScheduled = false;
                    if (FindScrollViewer(_list) is { } scroll)
                    {
                        SetFollow(_list, IsAtEnd(scroll));
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
