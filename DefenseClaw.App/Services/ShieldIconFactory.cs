using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace DefenseClaw.App.Services;

/// <summary>
/// What the shield shows. Which one wins when several apply is <see cref="ShieldIconFactory.StateFor(GatewaySnapshot, int?, bool)"/>'s
/// precedence, the Mac's menu-bar icon (<c>AppState.menuBarState</c>): paused, scanning, offline, alerting, degraded, healthy.
/// </summary>
public enum ShieldState
{
    /// <summary>The Mac's offline: gateway stopped, not installed, or not initialized.</summary>
    Stopped = 0,

    /// <summary>The Mac's healthy: everything running and native.</summary>
    Running,

    /// <summary>The Mac's degraded: degraded reads, or a WSL gateway masquerading as the native one.</summary>
    Warning,

    /// <summary>
    /// The Mac's alerting: unacknowledged findings are waiting. The badge carries their number (1 to 9, then "9+"); with no number to
    /// show (the counts have not been read yet, and the gateway's last alert poll held a CRITICAL) it is a bare exclamation mark.
    /// </summary>
    Critical,

    /// <summary>The operator paused monitoring (<see cref="GatewaySnapshot.IsPaused"/>): nothing is polling, so what the rest of the app knows is as of the pause.</summary>
    Paused,

    /// <summary>A <c>defenseclaw</c> scan this app started is running (<see cref="ScanActivity"/>).</summary>
    Scanning,
}

/// <summary>
/// The count of unacknowledged findings as the tray icon can show it: nothing, 1 to 9, or "9+". The icon is 16 px at 100 % display
/// scaling, so more than one digit does not fit; ten findings and five hundred look the same, and the tooltip has the real number.
/// </summary>
internal static class AlertBucket
{
    /// <summary>No number: nothing is waiting, or it is not known yet.</summary>
    public const int None = 0;

    /// <summary>The largest number the icon spells out.</summary>
    public const int MaxDigit = 9;

    /// <summary>"9+": ten or more.</summary>
    public const int Overflow = MaxDigit + 1;

    /// <summary>The bucket for a count (negative reads as none).</summary>
    public static int For(int count) => count <= 0 ? None : Math.Min(count, Overflow);

    /// <summary>What the badge says: "" for none, "7", "9+".</summary>
    public static string Text(int bucket) => For(bucket) switch
    {
        None => string.Empty,
        Overflow => "9+",
        var digit => digit.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Which tray icon to show: the state and, for the alerting one, the bucket of the count on it. Every distinct key is one icon
/// (<see cref="ShieldIconFactory.CreateIcon(TrayShieldKey)"/>), cached and persisted per key. The constructor takes a count or a bucket
/// and leaves the bucket at none for every state that has no number on it, so "paused with 3 findings" and "paused with 7" are the same
/// key and the same icon, and so are "alerting with 10" and "alerting with 500".
/// </summary>
internal readonly record struct TrayShieldKey
{
    /// <param name="state">What the shield shows.</param>
    /// <param name="count">The number of unacknowledged findings (or a bucket; <see cref="AlertBucket.For"/> is idempotent); only the alerting state keeps it.</param>
    public TrayShieldKey(ShieldState state, int count)
    {
        State = state;
        Bucket = state == ShieldState.Critical ? AlertBucket.For(count) : AlertBucket.None;
    }

    public ShieldState State { get; }

    /// <summary>An <see cref="AlertBucket"/> value; always <see cref="AlertBucket.None"/> unless <see cref="State"/> is <see cref="ShieldState.Critical"/>.</summary>
    public int Bucket { get; }

    public override string ToString() => Bucket == AlertBucket.None ? State.ToString() : $"{State} ({AlertBucket.Text(Bucket)})";
}

/// <summary>
/// Turns the DefenseClaw mark (<see cref="ShieldArtwork"/>, the one source of every icon the app shows) into
/// the tray icon and the window icons, at runtime, instead of shipping an <c>.ico</c> per state.
/// <para>
/// Six states, ten counts on one of them, × every DPI scale is a lot of binary blobs to keep in a repo whose review story is "read the
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
    /// Per-icon (state, and count bucket for the alerting one: <see cref="TrayShieldKey"/>) encoded ICO bytes — the render/encode cost
    /// is paid once, the per-assignment cost is a small array read plus <c>CreateIconFromResourceEx</c>. At most 16 entries:
    /// the six states, and the nine counts and "9+" besides on the alerting one. Deliberately <em>bytes</em> and
    /// not <see cref="Drawing.Icon"/>: a cached <see cref="Drawing.Icon"/> would be a shared
    /// instance, and any consumer that disposes what it is given (H.NotifyIcon 2.3.2 does, on
    /// reassignment) would poison it for every later caller. With bytes there is no master
    /// instance to poison, so the contract is enforced by construction rather than by convention.
    /// Guarded by <see cref="Gate"/>; entries are never mutated after insertion.
    /// </summary>
    private static readonly Dictionary<TrayShieldKey, byte[]> IconBytesCache = new();
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
        ShieldState.Paused => ShieldArtwork.StoppedSlate,
        ShieldState.Scanning => ShieldArtwork.ScanningBlue,
        _ => Color.FromRgb(0x8A, 0x8A, 0x8A),
    };

    /// <summary>
    /// What the gateway snapshot alone says: paused, offline, alerting (a CRITICAL in the last alert poll), degraded or healthy.
    /// This is the shield the dashboard's taskbar button wears; the tray also knows the unacknowledged count and whether a scan is
    /// running, and uses <see cref="StateFor(GatewaySnapshot, int?, bool)"/>.
    /// </summary>
    public static ShieldState StateFor(GatewaySnapshot snapshot) => StateFor(snapshot, unacknowledged: null, scanning: false);

    /// <summary>
    /// Maps what the app knows onto the shield, with the Mac's precedence (<c>AppState.menuBarState</c>): the first that applies wins.
    /// <list type="number">
    /// <item><description><b>Paused</b>: the operator paused monitoring. Nothing is polling, so nothing below is current.</description></item>
    /// <item><description><b>Scanning</b>: a scan this app started is running (<see cref="ScanActivity"/>).</description></item>
    /// <item><description><b>Offline</b> (<see cref="ShieldState.Stopped"/>): the gateway is stopped, not installed, not initialized, or not heard from yet.</description></item>
    /// <item><description><b>Alerting</b> (<see cref="ShieldState.Critical"/>): unacknowledged findings are waiting.</description></item>
    /// <item><description><b>Degraded</b> (<see cref="ShieldState.Warning"/>): degraded reads, or a WSL gateway standing in for the native one.</description></item>
    /// <item><description><b>Healthy</b> (<see cref="ShieldState.Running"/>).</description></item>
    /// </list>
    /// Offline outranks alerting, as on the Mac: the counts are read from the audit database, which answers with the gateway down, but
    /// a shield that says "findings" while nothing is protecting the machine says the wrong thing first.
    /// </summary>
    /// <param name="snapshot">The gateway state, including whether monitoring is paused.</param>
    /// <param name="unacknowledged">
    /// How many unacknowledged findings there are (<see cref="AlertCountsService"/>), or null while they have not been read: then the
    /// gateway's own last alert poll decides, as it did before the counts were on the icon (a CRITICAL in it is alerting). Once the
    /// count is known it is the whole answer, so an acknowledged CRITICAL the gateway still lists does not keep the shield red.
    /// </param>
    /// <param name="scanning">True while a scan is in flight.</param>
    public static ShieldState StateFor(GatewaySnapshot snapshot, int? unacknowledged, bool scanning)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IsPaused)
        {
            return ShieldState.Paused;
        }

        if (scanning)
        {
            return ShieldState.Scanning;
        }

        if (snapshot.State is not (AppGatewayState.Running or AppGatewayState.Degraded or AppGatewayState.WslGatewayDetected))
        {
            return ShieldState.Stopped;
        }

        var alerting = unacknowledged.HasValue ? unacknowledged.Value > 0 : snapshot.HasCriticalAlert;
        if (alerting)
        {
            return ShieldState.Critical;
        }

        return snapshot.State == AppGatewayState.Running ? ShieldState.Running : ShieldState.Warning;
    }

    /// <summary>
    /// The icon to show: <see cref="StateFor(GatewaySnapshot, int?, bool)"/>, and for the alerting state the bucket of the count
    /// (<see cref="AlertBucket"/>). Every other state shows its own glyph where the number would go, so it carries none.
    /// </summary>
    internal static TrayShieldKey KeyFor(GatewaySnapshot snapshot, int? unacknowledged, bool scanning) =>
        new(StateFor(snapshot, unacknowledged, scanning), unacknowledged ?? 0);

    /// <summary>
    /// Returns a <b>new</b> <see cref="Drawing.Icon"/> for <paramref name="state"/> that the caller
    /// owns. Every call returns a distinct instance with its own HICON; the shield is drawn and
    /// encoded only on the first request per state (and, for the alerting state's numbered icons,
    /// per count: <see cref="CreateIcon(TrayShieldKey)"/>), later calls just re-hydrate the cached ICO bytes.
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
    public static Drawing.Icon CreateIcon(ShieldState state) => CreateIcon(new TrayShieldKey(state, AlertBucket.None));

    /// <summary>
    /// <see cref="CreateIcon(ShieldState)"/> for one cache key: the state, and for the alerting one the bucket of the count on it.
    /// Same ownership contract (a new icon every call, the caller's to dispose), and the same economy: a key is drawn and encoded
    /// once per process, and persisted once per build, however many times the tray swaps to it.
    /// </summary>
    internal static Drawing.Icon CreateIcon(TrayShieldKey key)
    {
        lock (Gate)
        {
            if (IconBytesCache.TryGetValue(key, out var bytes))
            {
                return FromIcoBytes(bytes);
            }

            // An earlier launch of this build already drew and encoded this icon: no WPF rendering.
            if (TryReadPersisted(key, out bytes, out var persisted))
            {
                IconBytesCache[key] = bytes;
                return persisted;
            }

            // Materialise the first icon before caching the bytes so a malformed encode throws
            // here (as it always did) instead of poisoning the cache for every later call.
            bytes = EncodeIco(key);
            var first = FromIcoBytes(bytes);
            IconBytesCache[key] = bytes;
            Persist(key, bytes);
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

    /// <summary>
    /// <c>shield-{build}-tray-{state}.ico</c>, and <c>-n{bucket}</c> after the state for an icon with a number on it
    /// (<c>shield-{build}-tray-critical-n3.ico</c>). A key with no number keeps the name it has always had.
    /// </summary>
    private static string? PersistedPath(TrayShieldKey key) =>
        CacheDirectory is { Length: > 0 } directory
            ? Path.Combine(
                directory,
                $"shield-{BuildKey}-tray-{key.State.ToString().ToLowerInvariant()}" +
                (key.Bucket == AlertBucket.None ? string.Empty : "-n" + key.Bucket.ToString(CultureInfo.InvariantCulture)) +
                ".ico")
            : null;

    /// <summary>
    /// The persisted ICO for <paramref name="key"/> and a fresh icon built from it, or false when
    /// there is none this build wrote, or it does not load. A bad file is deleted so it is drawn
    /// again and replaced rather than tried on every launch.
    /// </summary>
    private static bool TryReadPersisted(TrayShieldKey key, out byte[] bytes, out Drawing.Icon icon)
    {
        bytes = Array.Empty<byte>();
        icon = null!;

        var path = PersistedPath(key);
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
    private static void Persist(TrayShieldKey key, byte[] bytes)
    {
        var path = PersistedPath(key);
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

    /// <summary>Draws the shield at every tray size and encodes it as ICO bytes (the only expensive step; done once per key).</summary>
    private static byte[] EncodeIco(TrayShieldKey key)
    {
        _ = Interlocked.Increment(ref EncodeCount);
        return ShieldArtwork.EncodeIco(key.State, ShieldArtwork.TrayIconSizes, key.Bucket);
    }

    private const int SmCxSmIcon = 49;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
