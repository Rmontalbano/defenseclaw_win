using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The heading of one card: a section-tinted glyph, the title, and optional trailing content (a badge, a count).
/// <para>
/// It replaces a bare <c>DcCardTitle</c> text block where the card has an identity worth a glyph. <see cref="Section"/>
/// picks the colour through the style's tokens (Linear tints it, Default tints it faintly, TUI uses the accent), and
/// <see cref="Tint"/> names a palette colour for a card whose meaning is not a section (attention orange, health green).
/// The glyph is decorative: the control has no automation peer, so a screen reader reads the title once, as text.
/// </para>
/// </summary>
public sealed class DcCardHeader : ContentControl
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol),
        typeof(SymbolRegular),
        typeof(DcCardHeader),
        new PropertyMetadata(SymbolRegular.Empty));

    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(
        nameof(Section),
        typeof(string),
        typeof(DcCardHeader),
        new PropertyMetadata(null));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint),
        typeof(string),
        typeof(DcCardHeader),
        new PropertyMetadata(null));

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing),
        typeof(object),
        typeof(DcCardHeader),
        new PropertyMetadata(null));

    static DcCardHeader()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcCardHeader), new FrameworkPropertyMetadata(typeof(DcCardHeader)));
    }

    /// <summary>The glyph. Empty: the header is just a title.</summary>
    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>One of <see cref="DcSections"/>: which section's tint the glyph wears.</summary>
    public string? Section
    {
        get => (string?)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    /// <summary>A palette colour by name (Indigo, Blue, Violet, Teal, Green, Amber, Orange, Red, Pink, Gray); wins over <see cref="Section"/>.</summary>
    public string? Tint
    {
        get => (string?)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <summary>Content at the right-hand end of the header row.</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }
}
