using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DefenseClaw.App.Views.Wizards;

/// <summary>
/// <see langword="true"/> collapses, <see langword="false"/> shows.
/// <para>
/// The shell registers a plain <c>BooleanToVisibilityConverter</c> at application scope, and
/// panels are expected to expose the inverse as a second boolean property rather than reach
/// for a converter. The wizard window is one screen driving several mutually exclusive
/// regions off a single flag (<c>IsReview</c>, <c>HasRun</c>), so one small converter is
/// cheaper here than four paired properties that could disagree.
/// </para>
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}
