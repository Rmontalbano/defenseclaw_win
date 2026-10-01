using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// A severity as a solid capsule (the Mac's <c>SeverityBadge</c>): the tone's own colour as the fill and a label in whichever of
/// black or white reads better on it, so the badge holds 4.5:1 in every style and mode without a per-style token. The severity
/// is always spelled out (<see cref="Text"/>); the colour is never the only carrier of the meaning. Info is the one quiet
/// badge: the neutral tint behind the ordinary text colour, as on the Mac.
/// <para>
/// <b>Tone.</b> The same keys the rest of the design system takes (<c>Critical|Bad, High|Warn, Medium, Low, Ok, Info|Neutral</c>),
/// so <c>Tone="{Binding SeverityKey}"</c> works. The fill is a dynamic resource reference to the tone's brush, so a live style
/// or mode switch recolours it, and the label colour follows the fill whenever the fill changes (a switch replaces the token
/// dictionary, and with it every brush; WPF freezes a brush it resolves from a resource, so a brush never changes in place).
/// </para>
/// </summary>
public sealed class DcSeverityBadge : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(DcSeverityBadge),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(string),
        typeof(DcSeverityBadge),
        new PropertyMetadata(null, (d, _) => ((DcSeverityBadge)d).ApplyTone()));

    /// <summary>The capsule's fill, resolved from the tone (a template binding target; set by <see cref="Tone"/>, not by hand).</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(DcSeverityBadge),
        new PropertyMetadata(null, (d, _) => ((DcSeverityBadge)d).UpdateForeground()));

    /// <summary>Label colours, frozen once: the pair a fill is judged against (black and white are the two extremes, so the better one is always at least 4.58:1).</summary>
    private static readonly Brush Dark = Freeze(Colors.Black);
    private static readonly Brush Light = Freeze(Colors.White);

    static DcSeverityBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcSeverityBadge), new FrameworkPropertyMetadata(typeof(DcSeverityBadge)));
    }

    public DcSeverityBadge()
    {
        IsTabStop = false;
        Focusable = false;
    }

    /// <summary>The severity as words: <c>HIGH</c>, <c>Critical</c>.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The tone key: Critical|Bad, High|Warn, Medium, Low, Ok, or anything else for the quiet Info look.</summary>
    public string? Tone
    {
        get => (string?)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        private set => SetValue(FillProperty, value);
    }

    /// <summary>The token a tone's solid fill comes from, or <c>null</c> for the quiet Info badge (which has its own pair).</summary>
    public static string? FillKeyFor(string? tone) => tone switch
    {
        "Critical" or "Bad" => "DcToneCriticalBrush",
        "High" or "Warn" => "DcToneHighBrush",
        "Medium" => "DcToneMediumBrush",
        "Low" => "DcToneLowBrush",
        "Ok" => "DcToneOkBrush",
        _ => null,
    };

    /// <summary>Black or white, whichever has the higher WCAG contrast ratio against <paramref name="fill"/> (the alpha is ignored: tone fills are opaque).</summary>
    public static Color ForegroundFor(Color fill) =>
        Ratio(Colors.Black, fill) >= Ratio(Colors.White, fill) ? Colors.Black : Colors.White;

    /// <summary>The WCAG 2.x contrast ratio of two opaque colours.</summary>
    public static double Ratio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ApplyTone()
    {
        var key = FillKeyFor(Tone);
        SetResourceReference(FillProperty, key ?? "DcToneNeutralSubtleBrush");
        UpdateForeground();
    }

    private void UpdateForeground()
    {
        if (FillKeyFor(Tone) is null)
        {
            SetResourceReference(ForegroundProperty, "DcTextPrimaryBrush");
            return;
        }

        if (Fill is SolidColorBrush solid)
        {
            Foreground = ForegroundFor(solid.Color) == Colors.Black ? Dark : Light;
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(DcSeverityBadge)} {Text} ({Tone})");
}

/// <summary>
/// A state as a dot and a lowercase word on a faint tint of the tone (the Mac's <c>StatePill</c>): "enabled", "clean", "blocked".
/// The word is the meaning; the dot and tint only echo it. The label stays the ordinary text colour, so it reads at 4.5:1 on the
/// tint in every look (the tints are 12% alpha).
/// </summary>
public sealed class DcStatePill : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(DcStatePill),
        new PropertyMetadata(string.Empty, (d, e) => ((DcStatePill)d).Word = ((string?)e.NewValue ?? string.Empty).ToLowerInvariant()));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(string),
        typeof(DcStatePill),
        new PropertyMetadata(null));

    private static readonly DependencyPropertyKey WordPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Word),
        typeof(string),
        typeof(DcStatePill),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty WordProperty = WordPropertyKey.DependencyProperty;

    static DcStatePill()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcStatePill), new FrameworkPropertyMetadata(typeof(DcStatePill)));
    }

    public DcStatePill()
    {
        IsTabStop = false;
        Focusable = false;
    }

    /// <summary>The state as the view-model spells it ("Enabled", "clean"); shown lowercased.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The tone key (Ok, Warn|High, Bad|Critical, Medium, Low, anything else neutral).</summary>
    public string? Tone
    {
        get => (string?)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>The lowercase word that is drawn.</summary>
    public string Word
    {
        get => (string)GetValue(WordProperty);
        private set => SetValue(WordPropertyKey, value);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(DcStatePill)} {Word} ({Tone})");
}

/// <summary>
/// A status cell: the state's mark (<see cref="DcStatusGlyph"/>: a dot in Default, a status circle in Linear, a flat shape in
/// TUI) and the state in words, in the ordinary text colour ("Active", "19 CRITICAL findings"). For the table columns where a
/// pill would be heavy (a scan result, an install state); a column of states that should read as chips takes <see cref="DcStatePill"/>.
/// </summary>
public sealed class DcStatusLabel : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(DcStatusLabel),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(string),
        typeof(DcStatusLabel),
        new PropertyMetadata(null));

    static DcStatusLabel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DcStatusLabel), new FrameworkPropertyMetadata(typeof(DcStatusLabel)));
    }

    public DcStatusLabel()
    {
        IsTabStop = false;
        Focusable = false;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Tone
    {
        get => (string?)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(DcStatusLabel)} {Text} ({Tone})");
}
