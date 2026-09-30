using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace DefenseClaw.App.Views.Panels.Govern;

/// <summary>
/// The height of a virtualizing list that is the last thing on a scrolling page: what is left of the viewport under the
/// chrome above it, and never less than a floor.
/// <para>
/// A <see cref="ScrollViewer"/> measures its content with unlimited height, and a list measured that way realizes every
/// row. So the list is not star-sized inside the page: it gets a definite height from this converter (bindings: the
/// page scroller's <c>ViewportHeight</c> and the chrome stack's <c>ActualHeight</c>; parameter: the floor, in DIPs), and
/// keeps its own scroller and its own virtualization. On a tall window the list fills the page; on a short one it keeps
/// the floor and the page scrolls to it. The chrome's height does not depend on the list's, so nothing loops.
/// </para>
/// </summary>
public sealed class FillHeightConverter : IMultiValueConverter
{
    public static readonly FillHeightConverter Instance = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var viewport = ToDouble(values, 0);
        var above = ToDouble(values, 1);
        var floor = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        return Math.Max(floor, Math.Floor(viewport - above));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    // Before the first layout a binding hands over DependencyProperty.UnsetValue: no room known yet, so the floor.
    private static double ToDouble(object[] values, int index) =>
        index < values.Length && values[index] is double d && double.IsFinite(d) ? d : 0;
}
