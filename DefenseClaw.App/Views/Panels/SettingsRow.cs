using System.Windows;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The two attached properties of a Settings row (a <c>HeaderedContentControl</c> in the <c>SettingRow</c> style): the note that sits under the
/// label and the control, and whether the row is the first in its card (it has no rule above it).
/// </summary>
public static class SettingsRow
{
    /// <summary>A sentence under the row in the secondary text colour; null or empty hides it. May be a binding.</summary>
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.RegisterAttached(
        "Caption",
        typeof(string),
        typeof(SettingsRow),
        new PropertyMetadata(null));

    /// <summary>True for the first row of a card: no rule above it, since the card's header is already there.</summary>
    public static readonly DependencyProperty IsFirstProperty = DependencyProperty.RegisterAttached(
        "IsFirst",
        typeof(bool),
        typeof(SettingsRow),
        new PropertyMetadata(false));

    public static string? GetCaption(DependencyObject element) => (string?)element.GetValue(CaptionProperty);

    public static void SetCaption(DependencyObject element, string? value) => element.SetValue(CaptionProperty, value);

    public static bool GetIsFirst(DependencyObject element) => (bool)element.GetValue(IsFirstProperty);

    public static void SetIsFirst(DependencyObject element, bool value) => element.SetValue(IsFirstProperty, value);
}
