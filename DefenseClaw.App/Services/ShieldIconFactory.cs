using System.Diagnostics;
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
/// Turns the DefenseClaw mark (<see cref="ShieldArtwork"/>, the one source of every icon the app shows) into
/// the tray icon and the window icons, at runtime, instead of shipping an <c>.ico</c> per state.
/// <para>
/// Four states × every DPI scale is a lot of binary blobs to keep in a repo whose review story is "read the
/// diff"; drawing them costs a few milliseconds once WPF's media stack is running and keeps the tree
/// text-only. (The one binary, the exe's <c>Assets\DefenseClaw.ico</c>, is generated from the same artwork
/// and a test keeps it in step.)
/// </para>
/// <para>
/// <b>Every icon is a multi-size <c>.ico</c></b> with a frame drawn for each size Windows may ask for, so the
/// 16 px tray icon is the hand-tuned 16 px drawing rather than a bigger one squeezed down. The tray icon is
/// materialised at the notification area's small-icon size (<see cref="TrayIconSize"/>); a window icon is a
/// <see cref="BitmapFrame"/> whose decoder carries every frame, which is how WPF picks the exact frame for the
/// title bar, the taskbar and alt-tab.
/// </para>
/// <para>
/// <b>Why the encoded icons are also kept on disk.</b> The first <see cref="DrawingVisual"/> a
/// process draws makes WPF bring up its media stack, and that one-off cost is not "a few
/// milliseconds": measured cold, <c>DrawingVisual.RenderOpen</c> alone took 400-570 ms, about half of
/// an otherwise tray-only <c>--minimized</c> startup, and the tray shield was the only thing on that
/// path that needed it. The encoded ICO bytes are therefore persisted (see <see cref="CacheDirectory"/>),
/// keyed by this assembly's build id, so a launch of a build that has already drawn a state never
/// touches WPF rendering for it at all: the first launch of each new build pays what every launch
/// used to, later ones read a few kilobytes. Any failure to read or write the cache falls back to
/// drawing, so the cache can only ever save work.
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
    /// <summary>The size <see cref="CreateImage"/> draws when none is asked for.</summary>
    private const int DefaultImageSize = 32;

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

    /// <summary>
    /// Per-state encoded window icons (<see cref="ShieldArtwork.WindowIconSizes"/>). Bytes for the same reason as
    /// <see cref="IconBytesCache"/>, and because a <see cref="BitmapFrame"/> keeps its decoder, which belongs to the
    /// thread that made it. Guarded by <see cref="Gate"/>.
    /// </summary>
    private static readonly Dictionary<ShieldState, byte[]> WindowIconBytesCache = new();

    /// <summary>A real tray ICO (eight sizes, 16-48 px) is ~35 KB; anything past this is not something this factory wrote.</summary>
    private const int MaxPersistedBytes = 64 * 1024;

    /// <summary>How long another build's persisted icons are left alone; see <see cref="PruneStale"/>.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);

    /// <summary>Guarded by <see cref="Gate"/>.</summary>
    private static bool _prunedCache;

    /// <summary>How many times a shield was actually drawn and encoded (not served from a cache). Tests observe it.</summary>
    internal static int EncodeCount;

    /// <summary>
    /// Where the encoded icons persist between launches: <c>%LOCALAPPDATA%\DefenseClaw.App\cache\icons</c>,
    /// next to the <c>logs</c>, <c>updates</c> and <c>upgrades</c> directories this app already owns. Settable
    /// so tests never touch the real one; null turns persistence off.
    /// </summary>
    internal static string? CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DefenseClaw.App",
        "cache",
        "icons");

    /// <summary>
    /// The colour that marks <paramref name="state"/> on the shield: Cisco blue when healthy, otherwise the
    /// colour of the state's badge (grey for stopped, whose badge is a neutral slate on a greyed shield).
    /// </summary>
    public static Color ColorFor(ShieldState state) => state switch
    {
        ShieldState.Running => ShieldArtwork.CiscoBlue,
        ShieldState.Warning => ShieldArtwork.WarningAmber,
        ShieldState.Critical => ShieldArtwork.CriticalRed,
        _ => Color.FromRgb(0x8A, 0x8A, 0x8A),
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
    /// handle and one more object that a careless caller could dispose). <c>new Icon(Stream, w, h)</c>
    /// on the cached bytes is the same construction the factory has always used to build its
    /// icons — a valid, self-owned HICON, here the frame drawn for the tray's small-icon size
    /// (<see cref="TrayIconSize"/>) — just repeated, and it leaves nothing shared.
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

            // An earlier launch of this build already drew and encoded this state: no WPF rendering.
            if (TryReadPersisted(state, out bytes, out var persisted))
            {
                IconBytesCache[state] = bytes;
                return persisted;
            }

            // Materialise the first icon before caching the bytes so a malformed encode throws
            // here (as it always did) instead of poisoning the cache for every later call.
            bytes = EncodeIco(state);
            var first = FromIcoBytes(bytes);
            IconBytesCache[state] = bytes;
            Persist(state, bytes);
            return first;
        }
    }

    /// <summary>
    /// Forgets the in-memory cache and the "already pruned" flag, so the next <see cref="CreateIcon"/>
    /// behaves like the first one in a fresh process. Tests only.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            IconBytesCache.Clear();
            WindowIconBytesCache.Clear();
            _prunedCache = false;
            EncodeCount = 0;
        }
    }

    /// <summary>
    /// The build id the persisted icons are keyed by: it changes whenever this assembly is rebuilt, and the
    /// artwork (<see cref="ShieldArtwork"/>) is compiled into this assembly, so new artwork never reads an old icon.
    /// </summary>
    private static string BuildKey => typeof(ShieldIconFactory).Module.ModuleVersionId.ToString("N");

    private static string? PersistedPath(ShieldState state) =>
        CacheDirectory is { Length: > 0 } directory
            ? Path.Combine(directory, $"shield-{BuildKey}-tray-{state.ToString().ToLowerInvariant()}.ico")
            : null;

    /// <summary>
    /// The persisted ICO for <paramref name="state"/> and a fresh icon built from it, or false when
    /// there is none this build wrote, or it does not load. A bad file is deleted so it is drawn
    /// again and replaced rather than tried on every launch.
    /// </summary>
    private static bool TryReadPersisted(ShieldState state, out byte[] bytes, out Drawing.Icon icon)
    {
        bytes = Array.Empty<byte>();
        icon = null!;

        var path = PersistedPath(state);
        if (path is null)
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var candidate = File.ReadAllBytes(path);
            if (candidate.Length is < 22 or > MaxPersistedBytes ||
                candidate[0] != 0 || candidate[1] != 0 || candidate[2] != 1 || candidate[3] != 0)
            {
                DeleteQuietly(path);
                return false;
            }

            icon = FromIcoBytes(candidate);
            bytes = candidate;
            return true;
        }
#pragma warning disable CA1031 // Whatever is wrong with the cache, drawing the icon is the answer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"shield icon cache: ignoring {path}: {ex.Message}");
            DeleteQuietly(path);
            return false;
        }
    }

    /// <summary>
    /// Best-effort write of a freshly encoded icon. Written beside the target and moved into place,
    /// so a launch that reads it never sees half a file and two launches never interleave.
    /// </summary>
    private static void Persist(ShieldState state, byte[] bytes)
    {
        var path = PersistedPath(state);
        if (path is null)
        {
            return;
        }

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PruneStale();

            var temporary = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A read-only or locked profile just means the next launch draws again.
            Trace.TraceWarning($"shield icon cache: could not write {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Once per process: drops icons other builds wrote more than <see cref="StaleAfter"/> ago, so the
    /// directory does not gather four files per release forever. Recent ones stay: an installed build and a
    /// dev build alternating on one machine would otherwise delete each other's icons at every launch.
    /// </summary>
    private static void PruneStale()
    {
        if (_prunedCache || CacheDirectory is not { Length: > 0 } directory)
        {
            return;
        }

        _prunedCache = true;

        try
        {
            var current = $"shield-{BuildKey}-";
            var cutoff = DateTime.UtcNow - StaleAfter;
            foreach (var file in Directory.GetFiles(directory, "shield-*"))
            {
                if (!Path.GetFileName(file).StartsWith(current, StringComparison.Ordinal) &&
                    File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    DeleteQuietly(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by another launch, or already gone.
        }
    }

    /// <summary>The same shield as a single frozen bitmap of <paramref name="size"/> pixels, for in-window status chips.</summary>
    public static ImageSource CreateImage(ShieldState state, int size = DefaultImageSize) =>
        ShieldArtwork.Render(state, size);

    /// <summary>
    /// A window icon for <paramref name="state"/> (the brand mark, <see cref="ShieldState.Running"/>, by default):
    /// a <see cref="BitmapFrame"/> decoded from a multi-size ICO (<see cref="ShieldArtwork.WindowIconSizes"/>).
    /// Assign it to <see cref="Window.Icon"/>: WPF sees the frame's icon decoder and, for each icon handle it
    /// makes (small for the title bar, large for the taskbar and alt-tab), takes the frame drawn for that size
    /// instead of scaling one bitmap. Returns a new frame on every call; the frame's decoder belongs to the
    /// calling thread, so create it on the thread that owns the window. Drawing happens once per state.
    /// </summary>
    public static BitmapFrame CreateWindowIcon(ShieldState state = ShieldState.Running)
    {
        byte[]? bytes;
        lock (Gate)
        {
            if (!WindowIconBytesCache.TryGetValue(state, out bytes))
            {
                bytes = ShieldArtwork.EncodeIco(state, ShieldArtwork.WindowIconSizes);
                WindowIconBytesCache[state] = bytes;
            }
        }

        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        // The largest frame, for anything that draws Window.Icon directly; the icon handles use all of them.
        // (WIC does not keep the file's order, so it is looked for rather than assumed to be last.)
        return decoder.Frames.MaxBy(frame => frame.PixelWidth)!;
    }

    /// <summary>
    /// The notification area's small-icon size in pixels at the system DPI: 16 at 100 %, 24 at 150 %, 36 at 225 %.
    /// The tray icon is materialised at this size, so the frame drawn for it is used as is.
    /// </summary>
    internal static int TrayIconSize => Math.Clamp(GetSystemMetrics(SmCxSmIcon), 16, 256);

    /// <summary>
    /// Builds a fresh, self-owned <see cref="Drawing.Icon"/> from encoded ICO bytes, picking the frame drawn for
    /// <see cref="TrayIconSize"/>. <c>Icon(Stream, int, int)</c> copies the stream into the icon, so the stream is
    /// disposed here and the result has no dependency on <paramref name="icoBytes"/> or on any other icon.
    /// </summary>
    private static Drawing.Icon FromIcoBytes(byte[] icoBytes)
    {
        using var stream = new MemoryStream(icoBytes, writable: false);
        var size = TrayIconSize;
        return new Drawing.Icon(stream, size, size);
    }

    /// <summary>Draws the shield at every tray size and encodes it as ICO bytes (the only expensive step; done once per state).</summary>
    private static byte[] EncodeIco(ShieldState state)
    {
        _ = Interlocked.Increment(ref EncodeCount);
        return ShieldArtwork.EncodeIco(state, ShieldArtwork.TrayIconSizes);
    }

    private const int SmCxSmIcon = 49;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
