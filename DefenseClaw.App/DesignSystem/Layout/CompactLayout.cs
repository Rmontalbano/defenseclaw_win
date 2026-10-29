using System.Windows;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// Tells the views under a panel whether the panel is narrow, so a list-and-detail layout can drop the side-by-side split
/// exactly when the split stops being useful. Set <see cref="BelowProperty"/> on the panel's root (the width, in DIPs, under
/// which it is compact) and read <see cref="IsCompactProperty"/> from any style trigger below it: the value is inherited down
/// the tree, so a trigger binds to <c>(panels:CompactLayout.IsCompact)</c> on <c>RelativeSource Self</c>.
/// <para>
/// Why a width and not the window: at the window's 940 DIP minimum a panel has about 663 DIPs to lay out in, and a list with
/// a detail pane beside it is a 381 DIP list next to a 258 DIP pane that says "Select a row" - the list's columns truncate to
/// eight characters to make room for a pane with nothing in it. The panel's own width is what the split has to fit into.
/// </para>
/// </summary>
public static class CompactLayout
{
    /// <summary>The panel width, in DIPs, below which <see cref="IsCompactProperty"/> is true. Unset (NaN): never compact.</summary>
    public static readonly DependencyProperty BelowProperty = DependencyProperty.RegisterAttached(
        "Below",
        typeof(double),
        typeof(CompactLayout),
        new PropertyMetadata(double.NaN, OnBelowChanged));

    /// <summary>Whether the element that carries <see cref="BelowProperty"/> is narrower than it. Inherited by its descendants.</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact",
        typeof(bool),
        typeof(CompactLayout),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetBelow(DependencyObject element) => (double)element.GetValue(BelowProperty);

    public static void SetBelow(DependencyObject element, double value) => element.SetValue(BelowProperty, value);

    public static bool GetIsCompact(DependencyObject element) => (bool)element.GetValue(IsCompactProperty);

    public static void SetIsCompact(DependencyObject element, bool value) => element.SetValue(IsCompactProperty, value);

    private static void OnBelowChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement panel)
        {
            return;
        }

        panel.SizeChanged -= OnSizeChanged;
        if (e.NewValue is double below && !double.IsNaN(below))
        {
            panel.SizeChanged += OnSizeChanged;
            Update(panel);
        }
        else
        {
            SetIsCompact(panel, false);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement panel)
    {
        // Before the first layout pass there is no width to judge by; SizeChanged raises once there is.
        if (panel.ActualWidth <= 0)
        {
            return;
        }

        SetIsCompact(panel, panel.ActualWidth < GetBelow(panel));
    }
}
