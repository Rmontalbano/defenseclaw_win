using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// A busy spinner that costs nothing while it is not on screen.
/// <para>
/// WPF-UI's <c>ProgressRing</c> starts its rotation from a template <c>Loaded</c> trigger and then only ever pauses it. A ring
/// that has been shown once therefore keeps an animation clock rooted in WPF's timing tree for the life of the process: hidden,
/// collapsed, removed from the tree or with its window hidden to the tray, the UI thread still pays for a 60 fps frame
/// pump (measured 50-70 Mcycles/s per shown-once ring; a resident tray app idled at 2-3 % of a core instead of under 1 %).
/// Nothing short of removing the clock stops it. This control owns exactly one <see cref="AnimationClock"/>, creates it when
/// it becomes visible and loaded, and removes it from the timing tree the moment it is hidden, unloaded or told to stop
/// (<see cref="Stop"/> is <c>Controller.Remove()</c>, which is what actually frees it).
/// </para>
/// <para>
/// Themes\DefenseClaw.xaml re-templates every <c>ui:ProgressRing</c> to host one of these, so the views keep declaring a
/// ring and none of them needs to know. The colours are tokens read as dynamic resources (the accent step that clears
/// contrast for the arc, the hairline colour for the track), and the SHAPE follows <c>DcGlyphFamily</c> like the status glyphs
/// do: a round arc under Default and Linear, a square-ended segmented ring that turns in steps under TUI. A live style
/// switch redraws it, and restarts the animation if the shape's motion changed.
/// </para>
/// <para>
/// It is decorative to UI Automation (no peer): the ring that hosts it carries the <c>AutomationProperties.Name</c>.
/// </para>
/// </summary>
public sealed class DcSpinner : FrameworkElement
{
    /// <summary>True (the default): an indeterminate spinner that turns while visible. False: a static ring showing <see cref="Progress"/>.</summary>
    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.Register(
        nameof(IsSpinning),
        typeof(bool),
        typeof(DcSpinner),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnIsSpinningChanged));

    /// <summary>The filled fraction of a determinate ring, 0 to 100. Ignored while <see cref="IsSpinning"/>.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress),
        typeof(double),
        typeof(DcSpinner),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        "ArcBrush",
        typeof(Brush),
        typeof(DcSpinner),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        "TrackBrush",
        typeof(Brush),
        typeof(DcSpinner),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty FamilyProperty = DependencyProperty.Register(
        "Family",
        typeof(string),
        typeof(DcSpinner),
        new FrameworkPropertyMetadata(DcGlyphFamilies.Dot, FrameworkPropertyMetadataOptions.AffectsRender, OnFamilyChanged));

    /// <summary>The side a spinner takes when nothing sizes it.</summary>
    private const double DefaultSide = 16;

    /// <summary>The Terminal shape: this many ticks, and the trailing ones that are lit (head first).</summary>
    private const int Ticks = 12;

    private static readonly double[] TailOpacity = { 1.0, 0.72, 0.5, 0.32, 0.18 };

    private readonly RotateTransform _rotate = new();
    private AnimationClock? _clock;
    private bool _loaded;

    static DcSpinner()
    {
        // Never a target: clicks and hovers belong to whatever it sits beside.
        IsHitTestVisibleProperty.OverrideMetadata(typeof(DcSpinner), new UIPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(DcSpinner), new UIPropertyMetadata(false));
    }

    public DcSpinner()
    {
        SetResourceReference(ArcBrushProperty, "DcAccentHoverBrush");
        SetResourceReference(TrackBrushProperty, "DcBorderBrush");
        SetResourceReference(FamilyProperty, "DcGlyphFamily");

        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);

        // IsVisible is false while an ancestor is collapsed, once the element leaves the tree, and while its window is
        // hidden: one signal covers a busy flag turning off, a panel swapped out and the dashboard closed to the tray.
        // Loaded / Unloaded back it up (and Unloaded also fires for a theme reload, which restarts the clock harmlessly).
        IsVisibleChanged += (_, _) => Sync();
        Loaded += (_, _) =>
        {
            _loaded = true;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            Sync();
        };
    }

    public bool IsSpinning
    {
        get => (bool)GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>True while the spinner owns a live animation clock. It must be false whenever the spinner is not on screen.</summary>
    internal bool HasActiveClock => _clock is not null;

    /// <summary>True while anything is still animating the spinner's rotation (the second half of "no clock left behind").</summary>
    internal bool IsAnimated => _rotate.HasAnimatedProperties;

    private string EffectiveFamily => Family == DcGlyphFamilies.Terminal ? DcGlyphFamilies.Terminal : DcGlyphFamilies.Dot;

    private string Family => (string)GetValue(FamilyProperty);

    private static void OnIsSpinningChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((DcSpinner)sender).Sync();

    private static void OnFamilyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var spinner = (DcSpinner)sender;
        if (spinner._clock is not null)
        {
            // The two shapes turn differently (smooth, in steps): a running clock is replaced by the right one.
            spinner.Stop();
            spinner.Sync();
        }
    }

    // ------------------------------------------------------------------ the clock

    private void Sync()
    {
        var run = _loaded && IsVisible && IsSpinning;
        if (run)
        {
            if (_clock is null)
            {
                Start();
            }
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        AnimationTimeline animation = EffectiveFamily == DcGlyphFamilies.Terminal ? SteppedTurn() : SmoothTurn();
        _clock = animation.CreateClock();
        _rotate.ApplyAnimationClock(RotateTransform.AngleProperty, _clock);
    }

    /// <summary>
    /// Stops the spinner for good: detaches the clock from the transform and REMOVES it from the timing tree. Detaching alone
    /// leaves the clock ticking until a gen-2 collection finds it, which is the leak this control exists to avoid.
    /// </summary>
    private void Stop()
    {
        if (_clock is null)
        {
            return;
        }

        var clock = _clock;
        _clock = null;
        _rotate.ApplyAnimationClock(RotateTransform.AngleProperty, null);
        clock.Controller?.Remove();
    }

    /// <summary>One full turn a second. 30 fps is plenty for a 16-60 px ring and halves what the UI thread pays while it shows.</summary>
    private static AnimationTimeline SmoothTurn()
    {
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(turn, 30);
        return turn;
    }

    /// <summary>A terminal spinner does not glide: one tick per step, twelve steps a turn.</summary>
    private static AnimationTimeline SteppedTurn()
    {
        var turn = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(1200),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        for (var i = 0; i < Ticks; i++)
        {
            _ = turn.KeyFrames.Add(new DiscreteDoubleKeyFrame(i * (360.0 / Ticks), KeyTime.FromPercent(i / (double)Ticks)));
        }

        Timeline.SetDesiredFrameRate(turn, 20);
        return turn;
    }

    // ------------------------------------------------------------------ drawing

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, DefaultSide), Math.Min(availableSize.Height, DefaultSide));

    protected override void OnRender(DrawingContext drawingContext)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var arc = (Brush?)GetValue(ArcBrushProperty);
        var track = (Brush?)GetValue(TrackBrushProperty);
        if (EffectiveFamily == DcGlyphFamilies.Terminal)
        {
            DrawSegments(drawingContext, centre, side, arc, track);
        }
        else
        {
            DrawRing(drawingContext, centre, side, arc, track);
        }
    }

    private void DrawRing(DrawingContext dc, Point centre, double side, Brush? arc, Brush? track)
    {
        var thickness = Math.Clamp(side / 10, 1.5, 3.5);
        var radius = (side - thickness) / 2;
        if (radius <= 0)
        {
            return;
        }

        if (track is not null)
        {
            dc.DrawEllipse(null, new Pen(track, thickness), centre, radius, radius);
        }

        if (arc is null)
        {
            return;
        }

        var sweep = IsSpinning ? 100.0 : Math.Clamp(Progress, 0, 100) * 3.6;
        if (sweep <= 0)
        {
            return;
        }

        var pen = new Pen(arc, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (sweep >= 359.9)
        {
            dc.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        // From twelve o'clock, clockwise.
        Point At(double degrees)
        {
            var radians = (degrees - 90) * Math.PI / 180;
            return new Point(centre.X + (radius * Math.Cos(radians)), centre.Y + (radius * Math.Sin(radians)));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(0), false, false);
            context.ArcTo(At(sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>
    /// The TUI shape: twelve square-ended ticks round a circle, the head one and a fading tail lit. Which tick is the head
    /// is decided by the rotation (a step of the animation moves it one tick), so the drawing itself never changes.
    /// </summary>
    private void DrawSegments(DrawingContext dc, Point centre, double side, Brush? lit, Brush? unlit)
    {
        var outer = side / 2;
        var thickness = Math.Clamp(side / 8, 1.5, 4);
        var inner = outer - Math.Max(3, side * 0.26);
        if (inner <= 0)
        {
            return;
        }

        // Determinate: the first Progress % of the ticks lit, from twelve o'clock (a static gauge).
        var litCount = IsSpinning ? TailOpacity.Length : (int)Math.Round(Math.Clamp(Progress, 0, 100) / 100 * Ticks);
        for (var i = 0; i < Ticks; i++)
        {
            Brush? brush;
            double opacity;
            if (IsSpinning)
            {
                // Tick 0 is the head; the tail trails anticlockwise (the animation turns clockwise).
                var behind = (Ticks - i) % Ticks;
                var isLit = behind < litCount;
                brush = isLit ? lit : unlit;
                opacity = isLit ? TailOpacity[behind] : 1.0;
            }
            else
            {
                var isLit = i < litCount;
                brush = isLit ? lit : unlit;
                opacity = 1.0;
            }

            if (brush is null)
            {
                continue;
            }

            dc.PushTransform(new RotateTransform(i * (360.0 / Ticks), centre.X, centre.Y));
            dc.PushOpacity(opacity);
            dc.DrawRectangle(brush, null, new Rect(centre.X - (thickness / 2), centre.Y - outer, thickness, outer - inner));
            dc.Pop();
            dc.Pop();
        }
    }
}
