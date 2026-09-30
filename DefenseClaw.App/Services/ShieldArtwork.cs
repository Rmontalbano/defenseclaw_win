using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DefenseClaw.App.Services;

/// <summary>
/// The DefenseClaw mark, from one source: a Cisco-blue heater shield carrying a lobster claw (a chela — the
/// big fixed finger and the hinged movable finger, open like a pincer). Every icon the app shows is drawn
/// here: the tray shield and the window icons at runtime, through <see cref="ShieldIconFactory"/>, and the
/// compiled exe icon, <c>Assets\DefenseClaw.ico</c>, which the test project's generator writes with
/// <see cref="EncodeIco"/> (a normal test holds the committed file to this artwork, so the two cannot drift).
/// <para>
/// <b>Three size buckets</b>, because one drawing cannot serve both 16 px and 256 px:
/// </para>
/// <list type="bullet">
/// <item><description><b>16, 20 and 24 px</b> (the tray at 100 %, 125 % and 150 %): hand-placed pixel grids
/// (<see cref="ShieldGrids"/>). Anti-aliased vectors smear a claw into a blob at these sizes; on the grids every
/// pixel is either fully on or fully off, the fingers are two or three pixels wide, and the gape between them
/// and the fixed finger's point are exaggerated so the pincer still reads.</description></item>
/// <item><description><b>25 to 47 px</b>: the vector artwork with the fine detail dropped, hinted so the shield's
/// one-pixel rim lands on whole pixels (see <see cref="DesignToPixels"/>).</description></item>
/// <item><description><b>48 px and up</b>: everything — the gradient, a darker rim, a sheen, the claw's shadow,
/// the serrated inner edges of both fingers and the joints at the hinge and the wrist.</description></item>
/// </list>
/// <para>
/// <b>States.</b> Healthy (<see cref="ShieldState.Running"/>) is the full-blue mark with nothing on it. Every
/// other state is told apart by shape as well as colour, so it survives a colour-blind glance at 16 px: a
/// stopped gateway greys the whole shield, dims the claw and adds a round badge with a stop square; a warning
/// adds an amber triangle with an exclamation mark; a critical alert adds a red disc with one. Badges sit in
/// the bottom-right corner behind a transparent knockout ring, so they separate from the shield on any
/// taskbar colour.
/// </para>
/// <para>
/// Vector coordinates are on a 256-unit design square. Everything built here is frozen, so it is safe to use
/// from any thread.
/// </para>
/// </summary>
internal static class ShieldArtwork
{
    /// <summary>Cisco blue: the top of the shield's face, and the colour of a healthy DefenseClaw.</summary>
    internal static readonly Color CiscoBlue = Color.FromRgb(0x04, 0x9F, 0xD9);

    /// <summary>The warning badge (Degraded, or a WSL gateway standing in for the native one).</summary>
    internal static readonly Color WarningAmber = Color.FromRgb(0xF2, 0xAE, 0x0A);

    /// <summary>The critical badge (a CRITICAL alert in the last poll).</summary>
    internal static readonly Color CriticalRed = Color.FromRgb(0xD1, 0x34, 0x38);

    /// <summary>The stopped badge's disc: dark enough for its white square, light enough to show on a dark taskbar.</summary>
    internal static readonly Color StoppedSlate = Color.FromRgb(0x56, 0x62, 0x6E);

    /// <summary>The sizes compiled into <c>Assets\DefenseClaw.ico</c> (Explorer, Start menu, taskbar pins, shortcuts).</summary>
    internal static readonly IReadOnlyList<int> AppIconSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };

    /// <summary>
    /// The sizes in a tray icon: the notification area's small-icon size at 100 % to 300 % display scaling, so
    /// the tray always gets a frame drawn for its exact size instead of a downscaled bigger one.
    /// </summary>
    internal static readonly IReadOnlyList<int> TrayIconSizes = new[] { 16, 20, 24, 28, 32, 36, 40, 48 };

    /// <summary>
    /// The sizes in a window icon: the small (title bar) and large (taskbar, alt-tab) icon sizes WPF asks for at
    /// 100 % to 300 % display scaling, plus 128 and 256 for Task View and large alt-tab thumbnails.
    /// </summary>
    internal static readonly IReadOnlyList<int> WindowIconSizes = new[] { 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80, 96, 128, 256 };

    /// <summary>From this size up the icon carries the full detail (serrations, joints, shadow, sheen).</summary>
    private const int DetailFrom = 48;

    /// <summary>The claw's lean: the axis it is drawn along points right, and this turns it up towards the shield's top-right.</summary>
    private const double ClawTilt = -28;

    /// <summary>
    /// A heater shield on the 256-unit design square: a raised centre on the top edge, square shoulders,
    /// straight flanks curving into a point. The rim is stroked on this outline.
    /// </summary>
    private static readonly Geometry Shield = Frozen(Geometry.Parse(
        "M 128,14 C 160,28 196,34 228,34 L 228,112 C 228,178 186,222 128,246 C 70,222 28,178 28,112 L 28,34 C 60,34 96,28 128,14 Z"));

    /// <summary>The claw silhouette alone (25-47 px).</summary>
    private static readonly Geometry Claw;

    /// <summary>The claw with its serrations and joints cut in (48 px and up).</summary>
    private static readonly Geometry ClawDetailed;

    /// <summary>A soft light falling from the top left, over the face only (48 px and up).</summary>
    private static readonly Brush Sheen = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55),
        },
        new Point(0, 0),
        new Point(1, 1)));

    /// <summary>A faint light edge just inside the rim, half of it clipped away by the face (48 px and up).</summary>
    private static readonly Pen Bevel = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF))), 6));

    private static readonly ShieldPalette Live = new(
        Top: CiscoBlue,
        Middle: Color.FromRgb(0x0A, 0x6F, 0xB4),
        Bottom: Color.FromRgb(0x00, 0x50, 0x73),
        Rim: Color.FromRgb(0x0D, 0x27, 0x4D),
        Claw: Color.FromRgb(0xF5, 0xFB, 0xFF),
        ClawShade: Color.FromRgb(0xD2, 0xE8, 0xF6),
        Shadow: Color.FromArgb(0x5A, 0x00, 0x2B, 0x45));

    /// <summary>Stopped: the same mark, desaturated, with the claw dimmed so it no longer pops.</summary>
    private static readonly ShieldPalette Idle = new(
        Top: Color.FromRgb(0xA9, 0xB2, 0xBA),
        Middle: Color.FromRgb(0x80, 0x8A, 0x93),
        Bottom: Color.FromRgb(0x5C, 0x65, 0x6D),
        Rim: Color.FromRgb(0x2B, 0x31, 0x37),
        Claw: Color.FromRgb(0xD0, 0xD6, 0xDB),
        ClawShade: Color.FromRgb(0xB4, 0xBC, 0xC3),
        Shadow: Color.FromArgb(0x4D, 0x1E, 0x23, 0x28));

    /// <summary>
    /// The 16, 20 and 24 px icons, pixel by pixel. <c>o</c> rim, <c>b</c> shield face, <c>W</c> claw,
    /// <c>.</c> transparent. The claw is the vector one, simplified: the movable
    /// finger sweeps up to the top right and hooks down at its tip, the fixed finger runs along the bottom and
    /// rises to a point, the gape between them is a wide notch, and the palm and wrist fill the lower left.
    /// </summary>
    private static readonly Dictionary<int, string[]> ShieldGrids = new()
    {
        [16] = new[]
        {
            "..oooooooooooo..",
            ".obbbbbbbbbbbbo.",
            ".obbbbbbbbWWbbo.",
            ".obbbbbbbWWWWbo.",
            ".obbbbbbWWWbWbo.",
            ".obbbbbWWWbbbbo.",
            ".obbbbWWWbbbbbo.",
            ".obbbWWWWbbbWbo.",
            ".obbWWWWWWbbWbo.",
            ".obbWWWWWWWWWbo.",
            "..obWWWWWWWWbo..",
            "..obWWWWWWbbbo..",
            "...oWWWbbbbbo...",
            "....obbbbbbo....",
            ".....oobboo.....",
            ".......oo.......",
        },
        [20] = new[]
        {
            "...oooooooooooooo...",
            "..obbbbbbbbbbbbbbo..",
            ".obbbbbbbbbbbWWbbbo.",
            ".obbbbbbbbbbWWWWbbo.",
            ".obbbbbbbbbWWWbbWbo.",
            ".obbbbbbbbWWWbbbbbo.",
            ".obbbbbbbWWWbbbbbbo.",
            ".obbbbbbWWWbbbbbbbo.",
            ".obbbbbWWWWbbbbWbbo.",
            ".obbbbWWWWWbbbbWbbo.",
            ".obbbWWWWWWWbbWWbbo.",
            ".obbWWWWWWWWWWWWbbo.",
            "..obWWWWWWWWWWWbbo..",
            "..obWWWWWWWWWWbbbo..",
            "...obWWWWWWWWbbbo...",
            "....obWWWWWbbbbo....",
            ".....oWWWbbbbbo.....",
            "......obbbbbbo......",
            ".......oobboo.......",
            ".........oo.........",
        },
        [24] = new[]
        {
            "...oooooooooooooooooo...",
            "..obbbbbbbbbbbbbbbbbbo..",
            ".obbbbbbbbbbbbbWWWbbbbo.",
            ".obbbbbbbbbbbbWWWWWbbbo.",
            ".obbbbbbbbbbbWWWbbbWbbo.",
            ".obbbbbbbbbbWWWbbbbbWbo.",
            ".obbbbbbbbbWWWbbbbbbbbo.",
            ".obbbbbbbbWWWbbbbbbbbbo.",
            ".obbbbbbbWWWWbbbbbbbbbo.",
            ".obbbbbbWWWWWbbbbbWbbbo.",
            ".obbbbbWWWWWWbbbbbWbbbo.",
            ".obbbbWWWWWWWWbbbWWbbbo.",
            ".obbbWWWWWWWWWWbWWWbbbo.",
            ".obbbWWWWWWWWWWWWWWbbbo.",
            "..obbWWWWWWWWWWWWWbbbo..",
            "..obbWWWWWWWWWWWWbbbbo..",
            "...obWWWWWWWWWWWbbbbo...",
            "....obWWWWWWWWbbbbbo....",
            ".....oWWWWbbbbbbbbo.....",
            "......oWWbbbbbbbbo......",
            ".......obbbbbbbbo.......",
            "........oobbbboo........",
            "..........obbo..........",
            "...........oo...........",
        },
    };

    /// <summary>
    /// The state badges for 16 and 20 px, drawn into the bottom-right corner. <c>x</c> badge, <c>g</c> its glyph,
    /// <c>.</c> nothing (the shield shows through, apart from the one-pixel knockout ring). From 24 px up the
    /// vector badge is sharp enough.
    /// </summary>
    private static readonly Dictionary<(Badge Badge, int Size), string[]> BadgeGrids = new()
    {
        [(Badge.Stopped, 16)] = new[]
        {
            "..xxx..",
            ".xxxxx.",
            "xxgggxx",
            "xxgggxx",
            "xxgggxx",
            ".xxxxx.",
            "..xxx..",
        },
        [(Badge.Warning, 16)] = new[]
        {
            "...x...",
            "..xxx..",
            "..xgx..",
            ".xxgxx.",
            ".xxxxx.",
            "xxxgxxx",
            "xxxxxxx",
        },
        [(Badge.Critical, 16)] = new[]
        {
            "..xxx..",
            ".xxgxx.",
            "xxxgxxx",
            "xxxgxxx",
            "xxxxxxx",
            ".xxgxx.",
            "..xxx..",
        },
        [(Badge.Stopped, 20)] = new[]
        {
            "...xxx...",
            ".xxxxxxx.",
            ".xxxxxxx.",
            "xxxgggxxx",
            "xxxgggxxx",
            "xxxgggxxx",
            ".xxxxxxx.",
            ".xxxxxxx.",
            "...xxx...",
        },
        [(Badge.Warning, 20)] = new[]
        {
            "....x....",
            "...xxx...",
            "...xgx...",
            "..xxgxx..",
            "..xxgxx..",
            ".xxxxxxx.",
            ".xxxgxxx.",
            "xxxxxxxxx",
            "xxxxxxxxx",
        },
        [(Badge.Critical, 20)] = new[]
        {
            "...xxx...",
            ".xxxgxxx.",
            ".xxxgxxx.",
            "xxxxgxxxx",
            "xxxxgxxxx",
            "xxxxxxxxx",
            ".xxxgxxx.",
            ".xxxxxxx.",
            "...xxx...",
        },
    };

    static ShieldArtwork()
    {
        // A grid that is not square draws a shifted, broken icon: refuse it outright (every test that draws trips on it).
        foreach (var (size, rows) in ShieldGrids.Select(grid => (grid.Key, grid.Value)).Concat(BadgeGrids.Select(grid => (grid.Value.Length, grid.Value))))
        {
            if (rows.Length != size || rows.Any(row => row.Length != size))
            {
                throw new InvalidOperationException($"A {size} px icon grid is not {size} × {size}.");
            }
        }

        // The claw is designed lying along +x — palm and wrist on the left, the fingers opening to the right —
        // then leaned by ClawTilt and fitted, centred, into the shield's face.
        var palm = new EllipseGeometry(new Point(100, 140), 56, 50);
        var wrist = Geometry.Parse("M 60,118 L 24,132 Q 16,150 24,168 L 64,164 Z");
        var dactyl = Geometry.Parse("M 104,96 C 150,48 212,40 238,78 C 206,68 176,86 150,126 Z");      // the movable finger, above
        var pollex = Geometry.Parse("M 106,188 C 160,196 214,184 240,148 C 214,150 184,142 150,134 Z"); // the fixed finger, below
        var gape = Geometry.Parse("M 150,126 C 176,86 206,68 238,78 L 240,148 C 214,150 184,142 150,134 Q 144,130 150,126 Z");

        var silhouette = Combine(Combine(Combine(palm, wrist, GeometryCombineMode.Union), dactyl, GeometryCombineMode.Union), pollex, GeometryCombineMode.Union);
        silhouette = Combine(silhouette, gape, GeometryCombineMode.Exclude);

        // Serrations along both inner edges (the same curves the gape is cut with), pointing into the gape,
        // then the two joints: where the movable finger hinges on the palm, and where the palm meets the wrist.
        var detailed = Combine(silhouette, Teeth(new(150, 126), new(176, 86), new(206, 68), new(238, 78), 0.20, 0.75), GeometryCombineMode.Union);
        detailed = Combine(detailed, Teeth(new(240, 148), new(214, 150), new(184, 142), new(150, 134), 0.25, 0.80), GeometryCombineMode.Union);
        var joint = new Pen(Brushes.Black, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        detailed = Combine(detailed, Geometry.Parse("M 112,88 Q 132,108 146,124").GetWidenedPathGeometry(joint), GeometryCombineMode.Exclude);
        detailed = Combine(detailed, Geometry.Parse("M 64,116 Q 56,142 66,166").GetWidenedPathGeometry(joint), GeometryCombineMode.Exclude);

        var placement = ClawPlacement(silhouette);
        Claw = Placed(silhouette, placement);
        ClawDetailed = Placed(detailed, placement);
    }

    private enum Badge
    {
        None,
        Stopped,
        Warning,
        Critical,
    }

    /// <summary>Draws the mark for <paramref name="state"/> at <paramref name="size"/> × <paramref name="size"/> pixels and returns it frozen.</summary>
    internal static BitmapSource Render(ShieldState state, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            Draw(context, state, size);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Encodes the mark for <paramref name="state"/> as a multi-size <c>.ico</c>: one image per entry of
    /// <paramref name="sizes"/>, each drawn for its size. Images below 256 px are 32-bit DIBs with an AND mask
    /// (what every icon consumer understands); the 256 px image is PNG-compressed, as Windows expects.
    /// </summary>
    internal static byte[] EncodeIco(ShieldState state, IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        var images = new List<(int Size, byte[] Data)>(sizes.Count);
        foreach (var size in sizes)
        {
            var bitmap = Render(state, size);
            images.Add((size, size >= 256 ? EncodePng(bitmap) : EncodeDib(bitmap)));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // ICONDIR, then one 16-byte ICONDIRENTRY per image, then the images.
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);

        var offset = 6 + (16 * images.Count);
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);   // palette entries
            writer.Write((byte)0);   // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images)
        {
            writer.Write(data);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Draws the mark for <paramref name="state"/> into a <paramref name="size"/>-pixel square at the origin.</summary>
    internal static void Draw(DrawingContext context, ShieldState state, int size)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);

        var palette = state == ShieldState.Stopped ? Idle : Live;
        var badge = state switch
        {
            ShieldState.Stopped => Badge.Stopped,
            ShieldState.Warning => Badge.Warning,
            ShieldState.Critical => Badge.Critical,
            _ => Badge.None,
        };

        if (ShieldGrids.TryGetValue(size, out var grid))
        {
            DrawGrid(context, grid, palette, badge, size);
        }
        else
        {
            DrawVector(context, palette, badge, size);
        }
    }

    private static void DrawGrid(DrawingContext context, string[] grid, ShieldPalette palette, Badge badge, int size)
    {
        var cells = grid.Select(row => row.ToCharArray()).ToArray();
        var mark = BadgeGrids.GetValueOrDefault((badge, size));
        var origin = size - (mark?.Length ?? 0);

        // A size with a grid shield but no grid badge (24 px) takes the vector badge.
        var vectorBadge = badge != Badge.None && mark is null ? PushBadgeKnockout(context, badge, size) : null;

        // The knockout: every shield pixel touching the badge (including diagonally) is cleared.
        if (mark is not null)
        {
            for (var y = 0; y < mark.Length; y++)
            {
                for (var x = 0; x < mark[y].Length; x++)
                {
                    if (mark[y][x] == '.')
                    {
                        continue;
                    }

                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var (cx, cy) = (origin + x + dx, origin + y + dy);
                            if (cx >= 0 && cy >= 0 && cx < size && cy < size)
                            {
                                cells[cy][cx] = '.';
                            }
                        }
                    }
                }
            }
        }

        context.DrawGeometry(Solid(palette.Rim), null, Cells(cells, "o", 0));
        context.DrawGeometry(FaceBrush(palette, 0, size), null, Cells(cells, "b", 0));
        context.DrawGeometry(Solid(palette.Claw), null, Cells(cells, "W", 0));

        if (vectorBadge is not null)
        {
            context.Pop();
            DrawBadge(context, badge, size, vectorBadge);
        }

        if (mark is not null)
        {
            var (fill, glyph) = BadgeColors(badge);
            var markCells = mark.Select(row => row.ToCharArray()).ToArray();
            context.DrawGeometry(Solid(fill), null, Cells(markCells, "xg", origin));
            context.DrawGeometry(Solid(glyph), null, Cells(markCells, "g", origin));
        }
    }

    private static void DrawVector(DrawingContext context, ShieldPalette palette, Badge badge, int size)
    {
        var detailed = size >= DetailFrom;
        var toPixels = DesignToPixels(size);
        var unitsPerPixel = 1 / toPixels.Value.M11;

        var badgeShape = badge == Badge.None ? null : PushBadgeKnockout(context, badge, size);

        context.PushTransform(toPixels);
        context.DrawGeometry(FaceBrush(palette, 14, 246), null, Shield);

        if (detailed)
        {
            context.PushClip(Shield);
            context.DrawGeometry(Sheen, null, Shield);
            context.PushTransform(new TranslateTransform(3, 6));
            context.DrawGeometry(Solid(palette.Shadow), null, ClawDetailed);
            context.Pop();
            context.DrawGeometry(null, Bevel, Shield);
            context.Pop();
        }

        // 1 px below the detail sizes (hinted onto whole pixels), size/32 above: 1.5 px at 48, 8 px at 256.
        var rim = new Pen(Solid(palette.Rim), detailed ? 8 : unitsPerPixel) { LineJoin = PenLineJoin.Round };
        rim.Freeze();
        context.DrawGeometry(null, rim, Shield);

        context.DrawGeometry(detailed ? ClawBrush(palette) : Solid(palette.Claw), null, detailed ? ClawDetailed : Claw);
        context.Pop();

        if (badgeShape is not null)
        {
            context.Pop();
            DrawBadge(context, badge, size, badgeShape);
        }
    }

    /// <summary>
    /// Clips everything drawn next away from the badge and a ring around it (the knockout), and returns the badge
    /// outline for <see cref="DrawBadge"/>; the caller pops the clip before drawing the badge.
    /// </summary>
    private static Geometry PushBadgeKnockout(DrawingContext context, Badge badge, int size)
    {
        var shape = BadgeShape(badge, size);
        var ring = new Pen(Brushes.Black, 2 * Math.Max(1, size / 24.0));
        var knockout = Combine(shape, shape.GetWidenedPathGeometry(ring), GeometryCombineMode.Union);
        context.PushClip(Frozen(Combine(new RectangleGeometry(new Rect(0, 0, size, size)), knockout, GeometryCombineMode.Exclude)));
        return shape;
    }

    private static void DrawBadge(DrawingContext context, Badge badge, int size, Geometry shape)
    {
        var (fill, glyph) = BadgeColors(badge);
        context.DrawGeometry(Solid(fill), null, shape);
        context.DrawGeometry(Solid(glyph), null, BadgeGlyph(badge, size));
    }

    /// <summary>
    /// Design units to pixels. From <see cref="DetailFrom"/> up that is a plain scale. Below it the scale and
    /// offset are nudged so the shield's straight flanks and shoulders fall on pixel centres, where a one-pixel
    /// rim is one crisp pixel instead of two half-lit ones: the small-size equivalent of font hinting.
    /// </summary>
    private static Transform DesignToPixels(int size)
    {
        var scale = size / 256.0;
        if (size >= DetailFrom)
        {
            return Frozen(new ScaleTransform(scale, scale));
        }

        var left = Math.Round((28 * scale) - 0.5) + 0.5;
        var right = Math.Round((228 * scale) - 0.5) + 0.5;
        var fitted = (right - left) / 200;

        var centred = (size / 2.0) - (130 * fitted);
        var shoulder = Math.Round((34 * fitted) + centred - 0.5) + 0.5;

        return Frozen(new MatrixTransform(fitted, 0, 0, fitted, left - (28 * fitted), shoulder - (34 * fitted)));
    }

    /// <summary>The shield's face: Cisco blue at the top deepening to the bottom, over the vertical span <paramref name="top"/>..<paramref name="bottom"/>.</summary>
    private static Brush FaceBrush(ShieldPalette palette, double top, double bottom)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, top),
            EndPoint = new Point(0, bottom),
            GradientStops =
            {
                new GradientStop(palette.Top, 0),
                new GradientStop(palette.Middle, 0.72),
                new GradientStop(palette.Bottom, 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    private static Brush ClawBrush(ShieldPalette palette)
    {
        var brush = new LinearGradientBrush(palette.Claw, palette.ClawShade, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static (Color Fill, Color Glyph) BadgeColors(Badge badge) => badge switch
    {
        Badge.Warning => (WarningAmber, Color.FromRgb(0x1B, 0x1B, 0x1B)),
        Badge.Critical => (CriticalRed, Colors.White),
        _ => (StoppedSlate, Colors.White),
    };

    /// <summary>The badge outline in pixels: a disc (stopped, critical) or a rounded triangle (warning), bottom right.</summary>
    private static Geometry BadgeShape(Badge badge, int size)
    {
        var box = BadgeBox(size);
        if (badge != Badge.Warning)
        {
            return Frozen(new EllipseGeometry(box));
        }

        var round = box.Width * 0.14;
        var triangle = new PathGeometry(new[]
        {
            new PathFigure(
                new Point(box.X + (box.Width / 2), box.Y + (round / 2)),
                new PathSegment[]
                {
                    new LineSegment(new Point(box.Right - (round / 2), box.Bottom - (round / 2)), true),
                    new LineSegment(new Point(box.X + (round / 2), box.Bottom - (round / 2)), true),
                },
                closed: true),
        });
        var corners = new Pen(Brushes.Black, round) { LineJoin = PenLineJoin.Round };
        return Frozen(Combine(triangle, triangle.GetWidenedPathGeometry(corners), GeometryCombineMode.Union));
    }

    /// <summary>The badge's glyph in pixels: a stop square, or an exclamation mark.</summary>
    private static Geometry BadgeGlyph(Badge badge, int size)
    {
        var box = BadgeBox(size);
        var centre = box.X + (box.Width / 2);

        if (badge == Badge.Stopped)
        {
            var side = box.Width * 0.38;
            return Frozen(new RectangleGeometry(
                new Rect(centre - (side / 2), box.Y + ((box.Height - side) / 2), side, side), side * 0.12, side * 0.12));
        }

        // The triangle's mark sits lower, where the triangle is wide enough for it.
        var (top, barBottom, dot, width) = badge == Badge.Warning
            ? (0.34, 0.66, 0.80, 0.13)
            : (0.20, 0.58, 0.76, 0.15);
        var stroke = box.Width * width;
        var mark = new GeometryGroup();
        mark.Children.Add(new RectangleGeometry(
            new Rect(centre - (stroke / 2), box.Y + (box.Height * top), stroke, box.Height * (barBottom - top)), stroke / 2, stroke / 2));
        mark.Children.Add(new EllipseGeometry(new Point(centre, box.Y + (box.Height * dot)), stroke * 0.62, stroke * 0.62));
        return Frozen(mark);
    }

    /// <summary>Just under half the icon, flush with its bottom-right corner.</summary>
    private static Rect BadgeBox(int size)
    {
        var side = Math.Round(size * 0.46);
        return new Rect(size - side, size - side, side, side);
    }

    /// <summary>One rectangle per horizontal run of <paramref name="chars"/> in a pixel grid, offset by <paramref name="origin"/>.</summary>
    private static Geometry Cells(char[][] rows, string chars, int origin)
    {
        var group = new GeometryGroup();
        for (var y = 0; y < rows.Length; y++)
        {
            var row = rows[y];
            var x = 0;
            while (x < row.Length)
            {
                if (!chars.Contains(row[x], StringComparison.Ordinal))
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < row.Length && chars.Contains(row[x], StringComparison.Ordinal))
                {
                    x++;
                }

                group.Children.Add(new RectangleGeometry(new Rect(origin + start, origin + y, x - start, 1)));
            }
        }

        return Frozen(group);
    }

    /// <summary>
    /// Small triangular teeth along the cubic Bézier <paramref name="a"/>..<paramref name="d"/> between the curve
    /// parameters <paramref name="from"/> and <paramref name="to"/>, each pointing to the curve's left (into the gape).
    /// </summary>
    private static Geometry Teeth(Point a, Point b, Point c, Point d, double from, double to)
    {
        const int Count = 4;
        const double Height = 8;
        const double Width = 9;

        var teeth = new GeometryGroup();
        for (var i = 0; i < Count; i++)
        {
            var t = from + ((to - from) * i / (Count - 1));
            var at = Bezier(a, b, c, d, t);
            var along = Bezier(a, b, c, d, Math.Min(1, t + 0.01)) - Bezier(a, b, c, d, Math.Max(0, t - 0.01));
            along.Normalize();
            var into = new Vector(-along.Y, along.X);

            // Based slightly inside the finger so each tooth fuses with it.
            teeth.Children.Add(new PathGeometry(new[]
            {
                new PathFigure(
                    at - (along * Width / 2) - (into * 2),
                    new PathSegment[]
                    {
                        new LineSegment(at + (into * Height), true),
                        new LineSegment(at + (along * Width / 2) - (into * 2), true),
                    },
                    closed: true),
            }));
        }

        return teeth;
    }

    private static Point Bezier(Point a, Point b, Point c, Point d, double t)
    {
        var u = 1 - t;
        return new Point(
            (u * u * u * a.X) + (3 * u * u * t * b.X) + (3 * u * t * t * c.X) + (t * t * t * d.X),
            (u * u * u * a.Y) + (3 * u * u * t * b.Y) + (3 * u * t * t * c.Y) + (t * t * t * d.Y));
    }

    /// <summary>Leans the claw by <see cref="ClawTilt"/>, then scales and centres it in the shield's face.</summary>
    private static Transform ClawPlacement(Geometry claw)
    {
        const double CentreX = 128;
        const double CentreY = 128;
        const double MaxWidth = 158;
        const double MaxHeight = 150;

        var lean = new RotateTransform(ClawTilt, 128, 128);
        var leaned = claw.Clone();
        leaned.Transform = lean;
        var bounds = leaned.Bounds;
        var fit = Math.Min(MaxWidth / bounds.Width, MaxHeight / bounds.Height);

        var placement = new TransformGroup();
        placement.Children.Add(lean);
        placement.Children.Add(new TranslateTransform(-(bounds.X + (bounds.Width / 2)), -(bounds.Y + (bounds.Height / 2))));
        placement.Children.Add(new ScaleTransform(fit, fit));
        placement.Children.Add(new TranslateTransform(CentreX, CentreY));
        return Frozen(placement);
    }

    private static Geometry Placed(Geometry claw, Transform placement)
    {
        var placed = claw.Clone();
        placed.Transform = placement;
        return Frozen(placed);
    }

    private static Geometry Combine(Geometry first, Geometry second, GeometryCombineMode mode) =>
        Geometry.Combine(first, second, mode, null);

    private static SolidColorBrush Solid(Color color) => Frozen(new SolidColorBrush(color));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }

        return freezable;
    }

    /// <summary>The 32-bit DIB of an icon image: BITMAPINFOHEADER (double height), straight-alpha BGRA rows bottom-up, then the AND mask.</summary>
    private static byte[] EncodeDib(BitmapSource bitmap)
    {
        var size = bitmap.PixelWidth;
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);

        var maskStride = (size + 31) / 32 * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write((size * size * 4) + (maskStride * size));
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        for (var y = size - 1; y >= 0; y--)
        {
            for (var x = 0; x < size; x++)
            {
                var i = ((y * size) + x) * 4;
                var alpha = pixels[i + 3];
                writer.Write(Unpremultiply(pixels[i], alpha));
                writer.Write(Unpremultiply(pixels[i + 1], alpha));
                writer.Write(Unpremultiply(pixels[i + 2], alpha));
                writer.Write(alpha);
            }
        }

        var mask = new byte[maskStride];
        for (var y = size - 1; y >= 0; y--)
        {
            Array.Clear(mask);
            for (var x = 0; x < size; x++)
            {
                if (pixels[(((y * size) + x) * 4) + 3] == 0)
                {
                    mask[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
            }

            writer.Write(mask);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static byte Unpremultiply(byte channel, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, ((channel * 255) + (alpha / 2)) / alpha);

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed record ShieldPalette(Color Top, Color Middle, Color Bottom, Color Rim, Color Claw, Color ClawShade, Color Shadow);
}
