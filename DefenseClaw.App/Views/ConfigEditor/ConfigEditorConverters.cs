using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>True when the bound string is non-null and non-empty — drives an <c>InfoBar.IsOpen</c> from a nullable message string.</summary>
public sealed class StringHasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The inverse of the app-level <c>BoolToVisibility</c> converter: true collapses, false shows.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True (an error) maps to <see cref="InfoBarSeverity.Error"/>; false to <see cref="InfoBarSeverity.Success"/>.</summary>
public sealed class BoolToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True (revealed) shows "Hide"; false shows "Reveal" — the secret-field toggle button's label.</summary>
public sealed class BoolToRevealLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Hide" : "Reveal";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a bool — used where a control's <c>IsEnabled</c> should follow the opposite of a "read-only" flag.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}
