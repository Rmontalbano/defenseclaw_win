using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Activity panel. Paired with
/// <see cref="ViewModels.ActivityPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>The card list scrolls by the pixel and only builds the cards in view</b> (see the <c>CardList</c> style in the
/// XAML), which leaves one thing for this file: keeping the operator's place. A scrolling list remembers a number, its
/// offset, not what was under it, so when the height above the viewport changes - a new run arrives at the top of a
/// newest-first list, a running entry's output grows a card that is just above the view - the cards slide out from under
/// whatever was being read. So this holds the top-most visible card at the same place in the viewport: after a layout in
/// which cards came or went, or a card above it changed height, the offset is moved by however far that card moved, before
/// anything is drawn. Scrolled to the very top there is nothing to hold - that is where the newest entry goes.
/// </para>
/// <para>
/// It only ever answers to a <i>known cause</i>. The list revises its own offset as it builds cards it had estimated, and
/// the operator scrolls (wheel, keys, scroll bar, focus moving to a card, a screen reader's scroll pattern), and none of
/// that may be undone: a card that moves with no such cause is simply the operator's new place.
/// </para>
/// </summary>
public sealed partial class ActivityPanel : UserControl
{
    /// <summary>Layout passes one shift may take to settle: the list estimates the height of the cards it has not built, so a correction can need a second look.</summary>
    private const int MaxCorrections = 6;

    /// <summary>Movement smaller than this many DIPs is not movement (layout rounding).</summary>
    private const double Tolerance = 0.5;

    /// <summary>The heights the built cards above the anchor had when it was taken, by item: a change in one of them moves the anchor.</summary>
    private readonly Dictionary<object, double> _heightsAbove = new();

    private ScrollViewer? _scroll;
    private VirtualizingStackPanel? _cardPanel;

    /// <summary>The card held in place: its item, its place in the list, and where its top was (relative to the scroll viewer).</summary>
    private object? _anchorItem;
    private int _anchorIndex;
    private double _anchorTop;

    /// <summary>Cards were added, removed or moved since the last layout.</summary>
    private bool _itemsChanged;

    /// <summary>A shift is being corrected and has not settled yet.</summary>
    private bool _holding;
    private int _corrections;

    public ActivityPanel()
    {
        InitializeComponent();

        ((INotifyCollectionChanged)Cards.Items).CollectionChanged += (_, _) => _itemsChanged = true;

        // The layout hook runs for every layout pass in the window, so it is only attached while this page is on screen.
        Loaded += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            LayoutUpdated += OnLayoutUpdated;
        };
        Unloaded += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            ForgetAnchor();
        };
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsVisible || FindCardScroll() is not { } scroll)
        {
            return;
        }

        var offset = scroll.VerticalOffset;
        if (offset <= Tolerance)
        {
            // At the top there is nothing to hold: a new entry appears there.
            ForgetAnchor();
            return;
        }

        if (_anchorItem is null)
        {
            TakeAnchor(scroll);
            return;
        }

        var container = Cards.ItemContainerGenerator.ContainerFromItem(_anchorItem) as FrameworkElement;
        if (!_holding && !_itemsChanged && !(container is not null && CardsAboveChangedHeight(container)))
        {
            // Nothing that moves the anchor has happened, so wherever it is now is where the operator has put it.
            TakeAnchor(scroll);
            return;
        }

        _itemsChanged = false;
        _holding = true;

        if (container is not null)
        {
            var delta = container.TranslatePoint(default, scroll).Y - _anchorTop;
            if (Math.Abs(delta) <= Tolerance)
            {
                TakeAnchor(scroll);
                return;
            }

            if (_corrections++ < MaxCorrections)
            {
                scroll.ScrollToVerticalOffset(offset + delta);
                return;
            }
        }
        else if (Cards.Items.IndexOf(_anchorItem) is var index and >= 0 && index != _anchorIndex && _corrections++ < MaxCorrections)
        {
            // Cards were added or removed above it, and the shift pushed it past the cards that are built, so it cannot be
            // measured. Move by that many cards' worth (an estimate: the built cards' average height), which brings it back
            // within reach, and let the next pass do the exact correction.
            scroll.ScrollToVerticalOffset(offset + ((index - _anchorIndex) * AverageCardHeight()));
            _anchorIndex = index;
            return;
        }

        // The card is gone, or the shift would not settle: hold whatever is at the top now.
        TakeAnchor(scroll);
    }

    /// <summary>Whether a card that is built above the anchor is not the height it was when the anchor was taken.</summary>
    private bool CardsAboveChangedHeight(FrameworkElement anchor)
    {
        if (_cardPanel is null)
        {
            return false;
        }

        foreach (var child in _cardPanel.Children)
        {
            if (ReferenceEquals(child, anchor))
            {
                break;
            }

            if (child is FrameworkElement card
                && Cards.ItemContainerGenerator.ItemFromContainer(card) is { } item
                && _heightsAbove.TryGetValue(item, out var height)
                && Math.Abs(height - card.ActualHeight) > Tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Holds the top-most card that shows in the viewport, wherever it is now.</summary>
    private void TakeAnchor(ScrollViewer scroll)
    {
        ForgetAnchor();

        if (_cardPanel is null || VisualTreeHelper.GetParent(_cardPanel) is null)
        {
            _cardPanel = FindDescendant<VirtualizingStackPanel>(scroll);
        }

        if (_cardPanel is null)
        {
            return;
        }

        // The panel keeps its cards in order, so the first one whose bottom edge is inside the viewport is the top-most;
        // the built cards before it are the ones whose height can move it.
        foreach (var child in _cardPanel.Children)
        {
            if (child is not FrameworkElement card)
            {
                continue;
            }

            var item = Cards.ItemContainerGenerator.ItemFromContainer(card);
            if (item == DependencyProperty.UnsetValue)
            {
                continue;
            }

            var top = card.TranslatePoint(default, scroll).Y;
            if (top + card.ActualHeight > Tolerance)
            {
                _anchorItem = item;
                _anchorIndex = Cards.Items.IndexOf(item);
                _anchorTop = top;
                return;
            }

            _heightsAbove[item] = card.ActualHeight;
        }

        // Nothing in view (an empty list): nothing to hold.
        _heightsAbove.Clear();
    }

    /// <summary>The average height of the cards that are built, for moving past cards that are not.</summary>
    private double AverageCardHeight() =>
        _cardPanel is { Children.Count: > 0 } panel ? panel.Children.OfType<FrameworkElement>().Average(card => card.ActualHeight) : 0;

    private void ForgetAnchor()
    {
        _anchorItem = null;
        _heightsAbove.Clear();
        _itemsChanged = false;
        _holding = false;
        _corrections = 0;
    }

    /// <summary>The card list's own scroll viewer (it is part of the list's template, so it exists once the template has been applied).</summary>
    private ScrollViewer? FindCardScroll()
    {
        if (_scroll is null)
        {
            _ = Cards.ApplyTemplate();
            _scroll = Cards.Template?.FindName("CardScroll", Cards) as ScrollViewer;
        }

        return _scroll;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
