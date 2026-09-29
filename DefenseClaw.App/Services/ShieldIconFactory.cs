using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace DefenseClaw.App.Services;

/// <summary>Tray colour states, in ascending order of "the operator should look at this".</summary>
public enum ShieldState
{
    /// <summary>Gateway stopped, not installed, or not initialized.</summary>
    Stopped = 0,

    /// <summary>Everything healthy and native.</summary>
    Running,

    /// <summary>Degraded reads, or a WSL gateway masquerading as the native one.</summary>
    Warning,

    /// <summary>A CRITICAL alert in the last poll.</summary>
    Critical,
}

/// <summary>
/// Draws the tray shield at runtime instead of shipping <c>.ico</c> files.
/// <para>
/// Four colours × several DPI scales is a lot of binary blobs to keep in a repo whose
/// review story is "read the diff"; a <see cref="DrawingVisual"/> rendered to a
/// <see cref="RenderTargetBitmap"/> costs a few milliseconds at startup and keeps the
/// tree text-only.
/// </para>
/// <para>
/// The handle dance at the end is the price of the WPF/GDI+ boundary:
/// <c>Bitmap.GetHicon</c> hands back an unmanaged HICON that <c>Icon.FromHandle</c> does
/// not own, so the icon is round-tripped through its own ICO bytes and the handle is
/// destroyed immediately — otherwise every rebuild leaks a GDI object.
/// </para>
/// <para>
/// <b>Ownership contract: this factory never hands out a shared <see cref="Drawing.Icon"/>.</b>
/// What is cached is the encoded ICO <i>bytes</i> (private, immutable in practice), not
/// <see cref="Drawing.Icon"/> instances; <see cref="CreateIcon"/> materialises a brand-new,
/// independently HICON-backed icon on every call and the caller owns it. That is a hard
/// requirement, not a tidiness preference: <c>H.NotifyIcon.Wpf</c> 2.3.2 (the pinned version)
/// <b>disposes the previously assigned icon whenever <c>TaskbarIcon.Icon</c> is reassigned</b>.
/// The earlier design cached one <see cref="Drawing.Icon"/> per state and handed the same
/// instance to the tray every time, so the first Running → Stopped → Running cycle disposed the
/// cached Running icon and the next assignment of it threw
/// <c>ObjectDisposedException: Object name: 'Icon'</c> from <c>Icon.get_Handle</c> inside
/// <c>TaskbarIcon.UpdateIcon</c>. That was observed in 25 captured crash logs over a month (they
/// cluster in pairs ~20 s apart, matching sleep/resume and gateway-restart flaps) and reproduced
/// deterministically with a standalone harness against 2.3.2: assign B → A is disposed; assign A
/// again → the exception above.
/// </para>
/// </summary>
public static class ShieldIconFactory
{
    private const int IconSize = 32;

    /// <summary>
    /// Per-state encoded ICO bytes — the render/encode cost is paid once, the per-assignment cost
    /// is a small array read plus <c>CreateIconFromResourceEx</c>. Deliberately <em>bytes</em> and
    /// not <see cref="Drawing.Icon"/>: a cached <see cref="Drawing.Icon"/> would be a shared
    /// instance, and any consumer that disposes what it is given (H.NotifyIcon 2.3.2 does, on
    /// reassignment) would poison it for every later caller. With bytes there is no master
    /// instance to poison, so the contract is enforced by construction rather than by convention.
    /// Guarded by <see cref="Gate"/>; entries are never mutated after insertion.
    /// </summary>
    private static readonly Dictionary<ShieldState, byte[]> IconBytesCache = new();
    private static readonly object Gate = new();

    /// <summary>Fill colour per state. Also used by the shell for the status dot.</summary>
    public static Color ColorFor(ShieldState state) => state switch
    {
        ShieldState.Running => Color.FromRgb(0x2E, 0xA0, 0x43),   // green
        ShieldState.Warning => Color.FromRgb(0xE8, 0xA3, 0x17),   // amber
        ShieldState.Critical => Color.FromRgb(0xD1, 0x34, 0x38),  // red
        _ => Color.FromRgb(0x8A, 0x8A, 0x8A),                     // gray
    };

    /// <summary>Maps the gateway state machine onto a tray colour.</summary>
    public static ShieldState StateFor(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.HasCriticalAlert)
        {
            return ShieldState.Critical;
        }

        return snapshot.State switch
        {
            AppGatewayState.Running => ShieldState.Running,
            AppGatewayState.Degraded => ShieldState.Warning,
            AppGatewayState.WslGatewayDetected => ShieldState.Warning,
            _ => ShieldState.Stopped,
        };
    }

    /// <summary>
    /// Returns a <b>new</b> <see cref="Drawing.Icon"/> for <paramref name="state"/> that the caller
    /// owns. Every call returns a distinct instance with its own HICON; the shield is drawn and
    /// encoded only on the first request per state, later calls just re-hydrate the cached ICO bytes.
    /// <para>
    /// Replaces the old <c>Get(ShieldState)</c>, which returned one shared cached instance and
    /// crashed the tray: <c>H.NotifyIcon.Wpf</c> 2.3.2 disposes the icon previously assigned to
    /// <c>TaskbarIcon.Icon</c> on reassignment, so re-assigning the shared instance after any
    /// state change threw <c>ObjectDisposedException ('Icon')</c> (25 field crashes; reproduced
    /// with a harness). Hand the result to exactly one owner — assign it to
    /// <c>TaskbarIcon.Icon</c> once, or dispose it yourself — and never cache it or assign it
    /// twice. A consumer that takes ownership (the tray) is expected to dispose it; disposing it
    /// again is harmless because <see cref="Drawing.Icon.Dispose()"/> is idempotent.
    /// </para>
    /// <para>
    /// Why re-create from bytes rather than <c>master.Clone()</c>: a <c>Clone</c> would still need
    /// a live master <see cref="Drawing.Icon"/> held for the process lifetime (a long-lived GDI
    /// handle and one more object that a careless caller could dispose). <c>new Icon(Stream)</c>
    /// on the cached bytes is the same construction the factory has always used to build its
    /// icons — a valid, self-owned HICON sized to the system icon metric — just repeated, and it
    /// leaves nothing shared.
    /// </para>
    /// </summary>
    public static Drawing.Icon CreateIcon(ShieldState state)
    {
        lock (Gate)
        {
            if (IconBytesCache.TryGetValue(state, out var bytes))
            {
                return FromIcoBytes(bytes);
            }

            // Materialise the first icon before caching the bytes so a malformed encode throws
            // here (as it always did) instead of poisoning the cache for every later call.
            bytes = EncodeIco(state);
            var first = FromIcoBytes(bytes);
            IconBytesCache[state] = bytes;
            return first;
        }
    }

    /// <summary>The same shield as an <see cref="ImageSource"/>, for in-window status chips.</summary>
    public static ImageSource CreateImage(ShieldState state, int size = IconSize)
    {
        var bitmap = Render(state, size);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Builds a fresh, self-owned <see cref="Drawing.Icon"/> from encoded ICO bytes.
    /// <c>Icon(Stream)</c> copies the stream into the icon, so the stream is disposed here and the
    /// result has no dependency on <paramref name="icoBytes"/> or on any other icon.
    /// </summary>
    private static Drawing.Icon FromIcoBytes(byte[] icoBytes)
    {
        using var stream = new MemoryStream(icoBytes, writable: false);
        return new Drawing.Icon(stream);
    }

    /// <summary>Renders the shield and encodes it as ICO bytes (the only expensive step; done once per state).</summary>
    private static byte[] EncodeIco(ShieldState state)
    {
        var bitmap = Render(state, IconSize);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var pngStream = new MemoryStream();
        encoder.Save(pngStream);
        pngStream.Position = 0;

        using var gdiBitmap = new Drawing.Bitmap(pngStream);
        var handle = gdiBitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle);
            using var iconStream = new MemoryStream();
            borrowed.Save(iconStream);
            return iconStream.ToArray();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    private static RenderTargetBitmap Render(ShieldState state, int size)
    {
        var fill = new SolidColorBrush(ColorFor(state));
        fill.Freeze();

        var stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        stroke.Freeze();

        var glyph = new SolidColorBrush(Colors.White);
        glyph.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawGeometry(fill, new Pen(stroke, 1), ShieldGeometry(size));
            context.DrawGeometry(glyph, null, ClawGeometry(size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>Classic heater shield: square shoulders, curved flanks, pointed base.</summary>
    private static Geometry ShieldGeometry(int size)
    {
        var s = size / 32.0;
        var figure = new PathFigure { StartPoint = new Point(16 * s, 2 * s), IsClosed = true, IsFilled = true };

        figure.Segments.Add(new LineSegment(new Point(28 * s, 7 * s), true));
        figure.Segments.Add(new LineSegment(new Point(28 * s, 16 * s), true));
        figure.Segments.Add(new BezierSegment(
            new Point(28 * s, 24 * s),
            new Point(22 * s, 28 * s),
            new Point(16 * s, 30 * s),
            true));
        figure.Segments.Add(new BezierSegment(
            new Point(10 * s, 28 * s),
            new Point(4 * s, 24 * s),
            new Point(4 * s, 16 * s),
            true));
        figure.Segments.Add(new LineSegment(new Point(4 * s, 7 * s), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Three claw marks across the boss — the "claw" half of the name.</summary>
    private static Geometry ClawGeometry(int size)
    {
        var s = size / 32.0;
        var group = new GeometryGroup();

        for (var i = 0; i < 3; i++)
        {
            var x = (11 + (i * 5)) * s;
            var figure = new PathFigure { StartPoint = new Point(x, 9 * s), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new LineSegment(new Point(x + (2.2 * s), 9 * s), true));
            figure.Segments.Add(new LineSegment(new Point(x + (1.1 * s), 21 * s), true));
            figure.Segments.Add(new LineSegment(new Point(x - (1.1 * s), 21 * s), true));

            var path = new PathGeometry();
            path.Figures.Add(figure);
            group.Children.Add(path);
        }

        group.Freeze();
        return group;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
