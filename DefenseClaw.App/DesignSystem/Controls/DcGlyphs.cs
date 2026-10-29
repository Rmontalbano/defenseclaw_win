using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DefenseClaw.App.Views.Controls;

/// <summary>The shape sets a status or severity glyph can draw, chosen by the style's <c>DcGlyphFamily</c> token.</summary>
public static class DcGlyphFamilies
{
    /// <summary>Default: a plain tone dot for a status, nothing for a severity (the badge already says it).</summary>
    public const string Dot = "Dot";

    /// <summary>Linear: status circles (check, half, cross, empty) and priority glyphs (an exclamation square, three bars, dots).</summary>
    public const string Linear = "Linear";

    /// <summary>TUI: flat terminal shapes - a filled circle, a triangle, a square, an "!" square - in the tone colour.</summary>
    public const string Terminal = "Terminal";
}

/// <summary>The tone vocabulary of the design system (<c>Tag</c> on <c>DcBadge</c> and friends), folded to what a glyph tells apart.</summary>
public enum GlyphTone
{
    Neutral,
    Low,
    Medium,
    High,
    Critical,
    Ok,
}

/// <summary>
/// Base of the two glyph controls: a small vector mark drawn in the colour of a tone, whose SHAPE follows the style.
/// <para>
/// The colours are the tone tokens (<c>DcTone*Brush</c>) and the shape set is the <c>DcGlyphFamily</c> token, both bound
/// as dynamic resource references, so a live style or light/dark switch redraws every glyph with no restart. A glyph
/// is decorative: it has no automation peer, and every place that shows one also says the same thing in words (the
/// state label, the severity text), so a colour or a shape is never the only carrier of the meaning.
/// </para>
/// </summary>
public abstract class DcGlyph : FrameworkElement
{
    /// <summary>The tone key, exactly as the view-models hand it out: Critical|Bad, High|Warn, Medium, Ok, Low, Info|Neutral.</summary>
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(string),
        typeof(DcGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The box the Linear and Terminal shapes are drawn in, in DIPs.</summary>
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize),
        typeof(double),
        typeof(DcGlyph),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The diameter of the Default style's dot (the <c>DcToneDot</c> it replaces is 10, <c>DcToneDotLarge</c> 12).</summary>
    public static readonly DependencyProperty DotSizeProperty = DependencyProperty.Register(
        nameof(DotSize),
        typeof(double),
        typeof(DcGlyph),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The height of the line of text the glyph sits beside, in DIPs (0: none). The glyph is centred in a box that tall, so a
    /// row that puts it in its own column (top-aligned) lines it up with the first line of text in every style, whatever the
    /// size of the shape the style draws.
    /// </summary>
    public static readonly DependencyProperty SlotHeightProperty = DependencyProperty.Register(
        nameof(SlotHeight),
        typeof(double),
        typeof(DcGlyph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Space after the glyph, in DIPs, that exists only while the glyph draws something: a severity glyph under Default draws
    /// nothing and so leaves no gap before the text it stands beside (a margin would stay).
    /// </summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap),
        typeof(double),
        typeof(DcGlyph),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The style's shape set (<see cref="DcGlyphFamilies"/>); bound to the <c>DcGlyphFamily</c> token by the constructor.</summary>
    public static readonly DependencyProperty FamilyProperty = DependencyProperty.Register(
        nameof(Family),
        typeof(string),
        typeof(DcGlyph),
        new FrameworkPropertyMetadata(DcGlyphFamilies.Dot, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnFamilyChanged));

    private static readonly DependencyProperty CriticalBrushProperty = BrushProperty("CriticalBrush");
    private static readonly DependencyProperty HighBrushProperty = BrushProperty("HighBrush");
    private static readonly DependencyProperty MediumBrushProperty = BrushProperty("MediumBrush");
    private static readonly DependencyProperty LowBrushProperty = BrushProperty("LowBrush");
    private static readonly DependencyProperty OkBrushProperty = BrushProperty("OkBrush");
    private static readonly DependencyProperty NeutralBrushProperty = BrushProperty("NeutralBrush");

    static DcGlyph()
    {
        // A mark beside text, never a target: clicks and hovers belong to the row it sits in. These are the property
        // DEFAULTS (metadata), not values set in the constructor: a value a template or a style gives an element has lower
        // precedence than one its constructor set, so a glyph declared in a DataTemplate with VerticalAlignment="Top" would
        // have stayed centred.
        IsHitTestVisibleProperty.OverrideMetadata(typeof(DcGlyph), new UIPropertyMetadata(false));
        VerticalAlignmentProperty.OverrideMetadata(typeof(DcGlyph), new FrameworkPropertyMetadata(VerticalAlignment.Center));
        HorizontalAlignmentProperty.OverrideMetadata(typeof(DcGlyph), new FrameworkPropertyMetadata(HorizontalAlignment.Left));
    }

    protected DcGlyph()
    {
        SetResourceReference(FamilyProperty, "DcGlyphFamily");
        SetResourceReference(CriticalBrushProperty, "DcToneCriticalBrush");
        SetResourceReference(HighBrushProperty, "DcToneHighBrush");
        SetResourceReference(MediumBrushProperty, "DcToneMediumBrush");
        SetResourceReference(LowBrushProperty, "DcToneLowBrush");
        SetResourceReference(OkBrushProperty, "DcToneOkBrush");
        SetResourceReference(NeutralBrushProperty, "DcToneNeutralBrush");
    }

    public string? Tone
    {
        get => (string?)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public double DotSize
    {
        get => (double)GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double SlotHeight
    {
        get => (double)GetValue(SlotHeightProperty);
        set => SetValue(SlotHeightProperty, value);
    }

    public string Family
    {
        get => (string)GetValue(FamilyProperty);
        set => SetValue(FamilyProperty, value);
    }

    /// <summary>The shape set actually drawn: the token's value, or the plain dot for a name this build does not know.</summary>
    internal string EffectiveFamily => Family is DcGlyphFamilies.Linear or DcGlyphFamilies.Terminal ? Family : DcGlyphFamilies.Dot;

    internal GlyphTone ToneKind => ParseTone(Tone);

    /// <summary>How big the glyph is in the current family; 0 for a family that draws nothing (see <see cref="DcSeverityGlyph"/>).</summary>
    protected abstract double SideFor(string family);

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = SideFor(EffectiveFamily);
        return side <= 0 ? default : new Size(side + Math.Max(0, Gap), Math.Max(side, SlotHeight));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var family = EffectiveFamily;
        var side = SideFor(family);
        if (side <= 0)
        {
            return;
        }

        var tone = ToneKind;
        var brush = BrushOf(tone);
        if (brush is null)
        {
            return;
        }

        // Centred in the slot: a glyph beside a line of text sits on that line's middle.
        var top = Math.Max(0, (Math.Max(side, SlotHeight) - side) / 2);
        drawingContext.PushTransform(new TranslateTransform(0, top));
        if (family == DcGlyphFamilies.Dot)
        {
            var radius = side / 2;
            drawingContext.DrawEllipse(brush, null, new Point(radius, radius), radius, radius);
        }
        else
        {
            // The shapes are authored in a 16 x 16 box and scaled into the glyph's.
            var scale = side / 16.0;
            drawingContext.PushTransform(new ScaleTransform(scale, scale));
            Draw(drawingContext, family, tone, brush);
            drawingContext.Pop();
        }

        drawingContext.Pop();
    }

    /// <summary>Draws the Linear or Terminal shape for <paramref name="tone"/> in a 16 x 16 box.</summary>
    protected abstract void Draw(DrawingContext dc, string family, GlyphTone tone, Brush brush);

    internal Brush? BrushOf(GlyphTone tone) => tone switch
    {
        GlyphTone.Critical => (Brush?)GetValue(CriticalBrushProperty),
        GlyphTone.High => (Brush?)GetValue(HighBrushProperty),
        GlyphTone.Medium => (Brush?)GetValue(MediumBrushProperty),
        GlyphTone.Low => (Brush?)GetValue(LowBrushProperty),
        GlyphTone.Ok => (Brush?)GetValue(OkBrushProperty),
        _ => (Brush?)GetValue(NeutralBrushProperty),
    };

    /// <summary>Folds the design system's tone keys (case-insensitive) to what a glyph tells apart. Unknown keys are neutral.</summary>
    internal static GlyphTone ParseTone(string? key) => key?.Trim().ToUpperInvariant() switch
    {
        "CRITICAL" or "BAD" or "FATAL" or "ERROR" => GlyphTone.Critical,
        "HIGH" or "WARN" or "WARNING" => GlyphTone.High,
        "MEDIUM" => GlyphTone.Medium,
        "OK" => GlyphTone.Ok,
        "LOW" => GlyphTone.Low,
        _ => GlyphTone.Neutral,
    };

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name,
        typeof(Brush),
        typeof(DcGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnFamilyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        sender.CoerceValue(VisibilityProperty);

    // ------------------------------------------------------------------ shared drawing helpers

    /// <summary>A round-capped stroke for cut-outs (the check, the cross, the "!").</summary>
    protected static Pen CutPen(double width)
    {
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    protected static Geometry Polyline(params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, points[0].Y), false, false);
            for (var i = 1; i < points.Length; i++)
            {
                context.LineTo(new Point(points[i].X, points[i].Y), true, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    protected static Geometry Polygon(params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, points[0].Y), true, true);
            for (var i = 1; i < points.Length; i++)
            {
                context.LineTo(new Point(points[i].X, points[i].Y), true, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary><paramref name="shape"/> with <paramref name="mark"/> (stroked at <paramref name="width"/>) cut out of it, so the page shows through.</summary>
    protected static Geometry Cutout(Geometry shape, Geometry mark, double width)
    {
        var cut = mark.GetWidenedPathGeometry(CutPen(width));
        var result = new CombinedGeometry(GeometryCombineMode.Exclude, shape, cut);
        result.Freeze();
        return result;
    }

    protected static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{GetType().Name} {Tone} ({Family})");
}

/// <summary>
/// A gateway, service or check state as a mark. Default: the tone dot. Linear: a status circle - a green disc with a check
/// (running, ok), an amber ring half filled (starting, degraded, warn), a red disc with a cross (stopped or failed), a blue
/// ring with a dot (info) and an empty grey ring (unknown, off). TUI: a filled circle, a triangle, a square, a diamond and a
/// ring. Bind <see cref="DcGlyph.Tone"/> to the same key the badge next to it takes.
/// </summary>
public sealed class DcStatusGlyph : DcGlyph
{
    /// <summary>
    /// Whether the Default style draws its dot. True for a state that has always had one (the strip, a service row); false for
    /// a glyph added inside a badge that says the same in words, so Default's badge stays as it was.
    /// </summary>
    public static readonly DependencyProperty ShowDotProperty = DependencyProperty.Register(
        nameof(ShowDot),
        typeof(bool),
        typeof(DcStatusGlyph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ShowDot
    {
        get => (bool)GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }

    private static readonly Geometry OkDisc = Cutout(
        Frozen(new EllipseGeometry(new Point(8, 8), 7, 7)),
        Polyline((4.7, 8.4), (7.2, 10.8), (11.5, 5.6)),
        1.7);

    private static readonly Geometry CrossDisc = Cutout(
        Frozen(new EllipseGeometry(new Point(8, 8), 7, 7)),
        Frozen(new GeometryGroup
        {
            Children =
            {
                Frozen(new LineGeometry(new Point(5.5, 5.5), new Point(10.5, 10.5))),
                Frozen(new LineGeometry(new Point(10.5, 5.5), new Point(5.5, 10.5))),
            },
        }),
        1.7);

    /// <summary>The right half of a disc: what a status circle fills while the thing is on its way (Linear's "in progress").</summary>
    private static readonly Geometry HalfPie = Frozen(new PathGeometry(new[]
    {
        new PathFigure(new Point(8, 4), new PathSegment[]
        {
            new ArcSegment(new Point(8, 12), new Size(4, 4), 0, false, SweepDirection.Clockwise, true),
        }, true),
    }));

    private static readonly Geometry Triangle = Polygon((8, 2.8), (13.6, 12.6), (2.4, 12.6));
    private static readonly Geometry Diamond = Polygon((8, 2.4), (13.6, 8), (8, 13.6), (2.4, 8));

    protected override double SideFor(string family) => family == DcGlyphFamilies.Dot ? (ShowDot ? DotSize : 0) : GlyphSize;

    protected override void Draw(DrawingContext dc, string family, GlyphTone tone, Brush brush) => DrawStatus(dc, family, tone, brush);

    /// <summary>The status shapes, shared with <see cref="DcSeverityGlyph"/> (a passed check is drawn as a status there too).</summary>
    internal static void DrawStatus(DrawingContext dc, string family, GlyphTone tone, Brush brush)
    {
        if (family == DcGlyphFamilies.Terminal)
        {
            DrawTerminal(dc, tone, brush);
            return;
        }

        var ring = new Pen(brush, 1.5);
        switch (tone)
        {
            case GlyphTone.Ok:
                dc.DrawGeometry(brush, null, OkDisc);
                break;
            case GlyphTone.Critical:
                dc.DrawGeometry(brush, null, CrossDisc);
                break;
            case GlyphTone.High:
                dc.DrawEllipse(null, ring, new Point(8, 8), 6.25, 6.25);
                dc.DrawGeometry(brush, null, HalfPie);
                break;
            case GlyphTone.Medium:
                dc.DrawEllipse(null, ring, new Point(8, 8), 6.25, 6.25);
                dc.DrawEllipse(brush, null, new Point(8, 8), 2.4, 2.4);
                break;
            default:
                dc.DrawEllipse(null, ring, new Point(8, 8), 6.25, 6.25);
                break;
        }
    }

    private static void DrawTerminal(DrawingContext dc, GlyphTone tone, Brush brush)
    {
        switch (tone)
        {
            case GlyphTone.Ok:
                dc.DrawEllipse(brush, null, new Point(8, 8), 4.6, 4.6);
                break;
            case GlyphTone.High:
                dc.DrawGeometry(brush, null, Triangle);
                break;
            case GlyphTone.Critical:
                dc.DrawRectangle(brush, null, new Rect(3.2, 3.2, 9.6, 9.6));
                break;
            case GlyphTone.Medium:
                dc.DrawGeometry(brush, null, Diamond);
                break;
            default:
                dc.DrawEllipse(null, new Pen(brush, 1.5), new Point(8, 8), 4, 4);
                break;
        }
    }
}

/// <summary>
/// A severity as a mark. Default: nothing (collapsed; the badge is the whole story). Linear: Linear's priority glyphs - an
/// exclamation square (critical), three, two or one lit bar (high, medium, low) and three dots (info). TUI: an
/// exclamation square, a triangle, a square, a circle and a ring. Bind <see cref="DcGlyph.Tone"/> to the severity key.
/// </summary>
public sealed class DcSeverityGlyph : DcGlyph
{
    private static readonly Geometry ExclamationSquare = Cutout(
        Frozen(new RectangleGeometry(new Rect(1.5, 1.5, 13, 13), 3.4, 3.4)),
        Frozen(new GeometryGroup
        {
            Children =
            {
                Frozen(new LineGeometry(new Point(8, 4.9), new Point(8, 8.3))),
                Frozen(new LineGeometry(new Point(8, 11.3), new Point(8, 11.3))),
            },
        }),
        1.8);

    private static readonly Geometry TerminalSquare = Cutout(
        Frozen(new RectangleGeometry(new Rect(2, 2, 12, 12))),
        Frozen(new GeometryGroup
        {
            Children =
            {
                Frozen(new LineGeometry(new Point(8, 4.8), new Point(8, 8.4))),
                Frozen(new LineGeometry(new Point(8, 11.2), new Point(8, 11.2))),
            },
        }),
        1.8);

    private static readonly Geometry Triangle = Polygon((8, 2.2), (14, 13.2), (2, 13.2));

    // Three bars, left to right, rising: x, height. All end at y = 14.
    private static readonly (double X, double Height)[] Bars = { (2.4, 4.6), (6.8, 8.2), (11.2, 11.8) };

    protected override double SideFor(string family) => family == DcGlyphFamilies.Dot ? 0 : GlyphSize;

    protected override void Draw(DrawingContext dc, string family, GlyphTone tone, Brush brush)
    {
        if (tone == GlyphTone.Ok)
        {
            // "Passed" is not a severity, but a doctor or scan row keys its tiles the same way: it reads as a status.
            DcStatusGlyph.DrawStatus(dc, family, tone, brush);
            return;
        }

        if (family == DcGlyphFamilies.Terminal)
        {
            DrawTerminal(dc, tone, brush);
            return;
        }

        switch (tone)
        {
            case GlyphTone.Critical:
                dc.DrawGeometry(brush, null, ExclamationSquare);
                break;
            case GlyphTone.High:
                DrawBars(dc, brush, 3);
                break;
            case GlyphTone.Medium:
                DrawBars(dc, brush, 2);
                break;
            case GlyphTone.Low:
                DrawBars(dc, brush, 1);
                break;
            default:
                dc.DrawEllipse(brush, null, new Point(3.6, 8), 1.25, 1.25);
                dc.DrawEllipse(brush, null, new Point(8, 8), 1.25, 1.25);
                dc.DrawEllipse(brush, null, new Point(12.4, 8), 1.25, 1.25);
                break;
        }
    }

    private static void DrawBars(DrawingContext dc, Brush brush, int lit)
    {
        for (var i = 0; i < Bars.Length; i++)
        {
            var (x, height) = Bars[i];
            if (i >= lit)
            {
                dc.PushOpacity(0.3);
            }

            dc.DrawRoundedRectangle(brush, null, new Rect(x, 14 - height, 2.6, height), 0.9, 0.9);
            if (i >= lit)
            {
                dc.Pop();
            }
        }
    }

    private static void DrawTerminal(DrawingContext dc, GlyphTone tone, Brush brush)
    {
        switch (tone)
        {
            case GlyphTone.Critical:
                dc.DrawGeometry(brush, null, TerminalSquare);
                break;
            case GlyphTone.High:
                dc.DrawGeometry(brush, null, Triangle);
                break;
            case GlyphTone.Medium:
                dc.DrawRectangle(brush, null, new Rect(4, 4, 8, 8));
                break;
            case GlyphTone.Low:
                dc.DrawEllipse(brush, null, new Point(8, 8), 3.4, 3.4);
                break;
            default:
                dc.DrawEllipse(null, new Pen(brush, 1.5), new Point(8, 8), 3.6, 3.6);
                break;
        }
    }

    public DcSeverityGlyph()
    {
        // Until the style's family arrives (a resource reference resolves when the glyph joins a tree) it is the plain dot.
        CoerceValue(VisibilityProperty);
    }

    /// <summary>Collapses the severity glyph under a family that draws nothing, so it leaves no gap behind it either.</summary>
    static DcSeverityGlyph()
    {
        VisibilityProperty.OverrideMetadata(
            typeof(DcSeverityGlyph),
            new FrameworkPropertyMetadata(
                Visibility.Visible,
                null,
                (d, value) => d is DcSeverityGlyph { EffectiveFamily: DcGlyphFamilies.Dot } ? Visibility.Collapsed : value));
    }
}
