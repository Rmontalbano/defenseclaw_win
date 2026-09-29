using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// Lets the mouse wheel pass through a scrollable control that sits inside a scrolling page.
/// <para>
/// A <see cref="ScrollViewer"/> marks every wheel notch handled - even one that cannot move it, because it is
/// already at the top or the bottom or has nothing to scroll at all. So a virtualized list or grid with its
/// own scrollbar, placed inside a page that also scrolls, becomes a trap: with the pointer over it the page
/// no longer scrolls, and there is no way to reach the rest of the page except the scrollbar or the keyboard.
/// Setting <see cref="ForwardWheelProperty"/> on the inner control fixes that: a notch it could scroll is left
/// alone, and one it could not is re-raised on its parent so the page scrolls instead.
/// </para>
/// </summary>
public static class NestedScroll
{
    public static readonly DependencyProperty ForwardWheelProperty = DependencyProperty.RegisterAttached(
        "ForwardWheel",
        typeof(bool),
        typeof(NestedScroll),
        new PropertyMetadata(false, OnForwardWheelChanged));

    public static bool GetForwardWheel(DependencyObject element) => (bool)element.GetValue(ForwardWheelProperty);

    public static void SetForwardWheel(DependencyObject element, bool value) => element.SetValue(ForwardWheelProperty, value);

    private static void OnForwardWheelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject element || FindScrollViewer(element) is not { } inner)
        {
            return;
        }

        var canScroll = e.Delta > 0
            ? inner.VerticalOffset > 0
            : inner.VerticalOffset < inner.ScrollableHeight;
        if (canScroll)
        {
            return;
        }

        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };
        (VisualTreeHelper.GetParent(element) as UIElement)?.RaiseEvent(forwarded);
    }

    /// <summary>The control's own <see cref="ScrollViewer"/>: itself, or the first one in its template.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self)
        {
            return self;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
