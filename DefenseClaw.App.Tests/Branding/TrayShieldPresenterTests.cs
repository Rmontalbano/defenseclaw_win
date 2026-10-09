using System.Diagnostics;
using System.Runtime.InteropServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Startup;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;
using Drawing = System.Drawing;

namespace DefenseClaw.App.Tests.Branding;

/// <summary>
/// What the tray does with the shield: replace the icon only when its look changes, hand the library a new icon every time, dispose the one
/// it replaced, and say nothing at all on a notification that changes nothing the icon or the tooltip shows. The icons here are tiny
/// real ones (a 16 px ICO built in memory), so "disposed" means the handle is gone; no WPF drawing, no tray.
/// </summary>
public sealed class TrayShieldPresenterTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static GatewaySnapshot Snapshot(AppGatewayState state = AppGatewayState.Running, bool paused = false) =>
        new() { State = state, IsPaused = paused, PolledAt = At };

    private static AlertCounts Counts(int total, bool hasMore = false) =>
        new(
            Enumerable.Range(0, total).Select(i => new AlertQueueItem("id-" + i, AuditSeverity.High, "scan-finding", null, "claudecode", At.AddSeconds(-i))).ToArray(),
            hasMore);

    /// <summary>A real, valid 16 px icon with its own handle, made without WPF: ICONDIR, one entry, a 32-bit DIB and its AND mask.</summary>
    private static Drawing.Icon NewIcon()
    {
        const int Size = 16;
        const int MaskStride = 4;
        var pixels = Size * Size * 4;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);

            writer.Write((byte)Size);
            writer.Write((byte)Size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(40 + pixels + (MaskStride * Size));
            writer.Write(6 + 16);

            writer.Write(40);
            writer.Write(Size);
            writer.Write(Size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(pixels + (MaskStride * Size));
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            for (var i = 0; i < Size * Size; i++)
            {
                writer.Write(0x80808080u | 0xFF000000u);
            }

            writer.Write(new byte[MaskStride * Size]);
        }

        stream.Position = 0;
        return new Drawing.Icon(stream);
    }

    /// <summary>The presenter over fakes that record what they are asked, with the tray library's habit of disposing the icon it replaces (or not).</summary>
    private sealed class Scene : IDisposable
    {
        public Scene(bool libraryDisposesReplaced = false)
        {
            Presenter = new TrayShieldPresenter(
                key =>
                {
                    Created.Add(key);
                    return NewIcon();
                },
                icon =>
                {
                    if (libraryDisposesReplaced && Assigned.Count > 0)
                    {
                        Assigned[^1].Dispose();
                    }

                    Assigned.Add(icon);
                },
                Tooltips.Add);
        }

        public List<TrayShieldKey> Created { get; } = new();

        public List<Drawing.Icon> Assigned { get; } = new();

        public List<string> Tooltips { get; } = new();

        public TrayShieldPresenter Presenter { get; }

        public static bool IsDisposed(Drawing.Icon icon)
        {
            try
            {
                _ = icon.Handle;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            Presenter.Dispose();
            foreach (var icon in Assigned)
            {
                icon.Dispose();
            }
        }
    }

    [Fact]
    public void The_first_update_makes_the_icon_and_says_the_state()
    {
        using var scene = new Scene();

        var change = scene.Presenter.Update(Snapshot(), counts: null, scanning: false);

        Assert.Equal(new TrayShieldChange(Icon: true, Tooltip: true), change);
        Assert.Equal(new[] { new TrayShieldKey(ShieldState.Running, 0) }, scene.Created);
        _ = Assert.Single(scene.Assigned);
        Assert.Equal("DefenseClaw — Running", Assert.Single(scene.Tooltips));
        Assert.Equal(new TrayShieldKey(ShieldState.Running, 0), scene.Presenter.Key);
        Assert.Equal("DefenseClaw — Running", scene.Presenter.Tooltip);
    }

    [Fact]
    public void Idle_updates_with_unchanged_inputs_create_no_icon_assign_nothing_and_say_nothing()
    {
        using var scene = new Scene();
        _ = scene.Presenter.Update(Snapshot(), Counts(3), scanning: false);

        for (var i = 0; i < 1_000; i++)
        {
            // The monitor's snapshots differ in what the shield and the tooltip do not show (the poll time), and the counts service hands out a
            // new, equal AlertCounts: neither is a reason to touch the tray.
            var change = scene.Presenter.Update(Snapshot() with { PolledAt = At.AddSeconds(i) }, Counts(3), scanning: false);

            Assert.Equal(default, change);
        }

        _ = Assert.Single(scene.Created);
        _ = Assert.Single(scene.Assigned);
        _ = Assert.Single(scene.Tooltips);
        Assert.False(Scene.IsDisposed(scene.Assigned[0]));
    }

    [Fact]
    public void A_count_that_stays_in_its_bucket_changes_the_tooltip_and_not_the_icon()
    {
        using var scene = new Scene();
        _ = scene.Presenter.Update(Snapshot(), Counts(10), scanning: false);

        var eleven = scene.Presenter.Update(Snapshot(), Counts(11), scanning: false);
        var many = scene.Presenter.Update(Snapshot(), Counts(500, hasMore: true), scanning: false);

        Assert.Equal(new TrayShieldChange(Icon: false, Tooltip: true), eleven);
        Assert.Equal(new TrayShieldChange(Icon: false, Tooltip: true), many);
        Assert.Equal(new[] { new TrayShieldKey(ShieldState.Critical, AlertBucket.Overflow) }, scene.Created);
        Assert.Equal(
            new[]
            {
                "DefenseClaw — 10 unacknowledged findings\nRunning",
                "DefenseClaw — 11 unacknowledged findings\nRunning",
                "DefenseClaw — 500+ unacknowledged findings\nRunning",
            },
            scene.Tooltips);
    }

    [Fact]
    public void A_new_bucket_makes_one_new_icon_and_disposes_the_one_it_replaced()
    {
        using var scene = new Scene();
        _ = scene.Presenter.Update(Snapshot(), Counts(3), scanning: false);
        var first = scene.Assigned[0];

        var change = scene.Presenter.Update(Snapshot(), Counts(4), scanning: false);

        Assert.True(change.Icon);
        Assert.Equal(
            new[] { new TrayShieldKey(ShieldState.Critical, 3), new TrayShieldKey(ShieldState.Critical, 4) },
            scene.Created);
        Assert.Equal(2, scene.Assigned.Count);
        Assert.NotSame(scene.Assigned[0], scene.Assigned[1]);
        Assert.True(Scene.IsDisposed(first), "the replaced icon was not disposed");
        Assert.False(Scene.IsDisposed(scene.Assigned[1]));
    }

    [Fact]
    public void The_replaced_icon_is_disposed_whether_or_not_the_library_did_it_first()
    {
        // H.NotifyIcon.Wpf 2.3.2 disposes the icon it had when it is given another; a future version might not. Either way nothing leaks, and
        // the second disposal is harmless.
        foreach (var libraryDisposes in new[] { true, false })
        {
            using var scene = new Scene(libraryDisposes);
            _ = scene.Presenter.Update(Snapshot(), counts: null, scanning: false);
            _ = scene.Presenter.Update(Snapshot(paused: true), counts: null, scanning: false);
            _ = scene.Presenter.Update(Snapshot(), counts: null, scanning: true);

            Assert.True(Scene.IsDisposed(scene.Assigned[0]), $"library disposes: {libraryDisposes}");
            Assert.True(Scene.IsDisposed(scene.Assigned[1]), $"library disposes: {libraryDisposes}");
            Assert.False(Scene.IsDisposed(scene.Assigned[2]), $"library disposes: {libraryDisposes}");
        }
    }

    [Fact]
    public void Every_icon_handed_over_is_a_new_one()
    {
        using var scene = new Scene();

        // Back and forth between two looks: each swap is a new icon, never the one that was showing before (the library would have disposed it).
        for (var i = 0; i < 6; i++)
        {
            _ = scene.Presenter.Update(Snapshot(paused: i % 2 == 0), counts: null, scanning: false);
        }

        Assert.Equal(6, scene.Assigned.Count);
        Assert.Equal(6, scene.Assigned.Distinct().Count());
        Assert.Equal(5, scene.Assigned.Count(Scene.IsDisposed));
    }

    [Fact]
    public void Each_look_in_a_session_is_one_swap()
    {
        using var scene = new Scene();

        var inputs = new (GatewaySnapshot Snapshot, AlertCounts? Counts, bool Scanning, ShieldState Expected, int Bucket)[]
        {
            (Snapshot(), Counts(0), false, ShieldState.Running, 0),
            (Snapshot(), Counts(2), false, ShieldState.Critical, 2),
            (Snapshot(), Counts(2), true, ShieldState.Scanning, 0),
            (Snapshot(paused: true), Counts(2), true, ShieldState.Paused, 0),
            (Snapshot(paused: true), Counts(7), false, ShieldState.Paused, 0),
            (Snapshot(), Counts(7), false, ShieldState.Critical, 7),
            (Snapshot(AppGatewayState.GatewayStopped), Counts(7), false, ShieldState.Stopped, 0),
            (Snapshot(AppGatewayState.Degraded), Counts(0), false, ShieldState.Warning, 0),
            (Snapshot(AppGatewayState.Degraded), Counts(0), false, ShieldState.Warning, 0),
            (Snapshot(), Counts(0), false, ShieldState.Running, 0),
        };

        var swaps = 0;
        foreach (var (snapshot, counts, scanning, expected, bucket) in inputs)
        {
            if (scene.Presenter.Update(snapshot, counts, scanning).Icon)
            {
                swaps++;
            }

            Assert.Equal(new TrayShieldKey(expected, bucket), scene.Presenter.Key);
        }

        // The two repeats (paused with a different count, degraded twice) are not swaps.
        Assert.Equal(8, swaps);
        Assert.Equal(8, scene.Created.Count);
    }

    [Fact]
    public void A_failed_drawing_keeps_the_old_icon_and_the_tooltip_still_follows()
    {
        var fail = false;
        var assigned = new List<Drawing.Icon>();
        var tooltips = new List<string>();
        using var presenter = new TrayShieldPresenter(
            _ => fail ? throw new InvalidOperationException("WPF could not draw") : NewIcon(),
            assigned.Add,
            tooltips.Add);

        _ = presenter.Update(Snapshot(), counts: null, scanning: false);
        fail = true;

        _ = Assert.Throws<InvalidOperationException>(() => presenter.Update(Snapshot(paused: true), counts: null, scanning: false));

        // The shield is still the one that was showing, so the next update retries the swap instead of believing it was made.
        Assert.Equal(new TrayShieldKey(ShieldState.Running, 0), presenter.Key);
        Assert.False(Scene.IsDisposed(assigned[0]));
        Assert.Equal("DefenseClaw — Monitoring paused", tooltips[^1]);

        fail = false;
        var retry = presenter.Update(Snapshot(paused: true), counts: null, scanning: false);

        Assert.True(retry.Icon);
        Assert.Equal(new TrayShieldKey(ShieldState.Paused, 0), presenter.Key);
        Assert.Equal(2, assigned.Count);

        foreach (var icon in assigned)
        {
            icon.Dispose();
        }
    }

    [Fact]
    public void An_icon_the_tray_refuses_is_disposed_and_the_old_one_stays()
    {
        var made = new List<Drawing.Icon>();
        var refuse = false;
        using var presenter = new TrayShieldPresenter(
            _ =>
            {
                var icon = NewIcon();
                made.Add(icon);
                return icon;
            },
            _ =>
            {
                if (refuse)
                {
                    throw new InvalidOperationException("the shell said no");
                }
            },
            _ => { });

        _ = presenter.Update(Snapshot(), counts: null, scanning: false);
        refuse = true;

        _ = Assert.Throws<InvalidOperationException>(() => presenter.Update(Snapshot(paused: true), counts: null, scanning: false));

        Assert.True(Scene.IsDisposed(made[1]), "an icon nobody owns leaked");
        Assert.False(Scene.IsDisposed(made[0]));
        Assert.Equal(new TrayShieldKey(ShieldState.Running, 0), presenter.Key);
    }

    [Fact]
    public void Disposing_releases_the_icon_showing_and_later_updates_do_nothing()
    {
        using var scene = new Scene();
        _ = scene.Presenter.Update(Snapshot(), Counts(3), scanning: false);

        scene.Presenter.Dispose();
        scene.Presenter.Dispose();
        var change = scene.Presenter.Update(Snapshot(paused: true), Counts(9), scanning: true);

        Assert.True(Scene.IsDisposed(scene.Assigned[0]));
        Assert.Equal(default, change);
        _ = Assert.Single(scene.Created);
        _ = Assert.Single(scene.Tooltips);
    }

    [Fact]
    public void Nothing_is_accepted_without_its_collaborators()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new TrayShieldPresenter(null!, _ => { }, _ => { }));
        _ = Assert.Throws<ArgumentNullException>(() => new TrayShieldPresenter(_ => NewIcon(), null!, _ => { }));
        _ = Assert.Throws<ArgumentNullException>(() => new TrayShieldPresenter(_ => NewIcon(), _ => { }, null!));
        using var scene = new Scene();
        _ = Assert.Throws<ArgumentNullException>(() => scene.Presenter.Update(null!, counts: null, scanning: false));
    }
}

/// <summary>
/// The same presenter over the real factory, drawing real icons on an STA thread: what the tray costs in handles and in drawing when
/// nothing changes, and when everything keeps changing. The factory's caches are process-wide, so these take turns with the other icon tests.
/// </summary>
[Collection(ShieldIconFactoryCollection.Name)]
public sealed class TrayShieldHandleTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string? _originalDirectory = ShieldIconFactory.CacheDirectory;

    public TrayShieldHandleTests()
    {
        // Never the real %LOCALAPPDATA% cache.
        ShieldIconFactory.CacheDirectory = null;
        ShieldIconFactory.ResetForTests();
    }

    public void Dispose()
    {
        ShieldIconFactory.CacheDirectory = _originalDirectory;
        ShieldIconFactory.ResetForTests();
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    /// <summary>The process's GDI and USER object counts: an HICON is one of the latter, its bitmaps are the former.</summary>
    private static (uint Gdi, uint User) Handles()
    {
        using var process = Process.GetCurrentProcess();
        return (GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));
    }

    private static GatewaySnapshot Snapshot(AppGatewayState state = AppGatewayState.Running, bool paused = false, bool critical = false) =>
        new() { State = state, IsPaused = paused, CriticalAlertCount = critical ? 1 : 0, PolledAt = At };

    private static AlertCounts Counts(int total) =>
        new(
            Enumerable.Range(0, total).Select(i => new AlertQueueItem("id-" + i, AuditSeverity.High, "scan-finding", null, "claudecode", At.AddSeconds(-i))).ToArray(),
            hasMore: false);

    /// <summary>Eight inputs, each a different look from the one before it (and from the first when the list wraps).</summary>
    private static (GatewaySnapshot Snapshot, AlertCounts? Counts, bool Scanning)[] Looks() => new (GatewaySnapshot Snapshot, AlertCounts? Counts, bool Scanning)[]
    {
        (Snapshot(), Counts(0), false),                                   // healthy
        (Snapshot(), Counts(3), false),                                   // alerting, 3
        (Snapshot(paused: true), Counts(3), false),                       // paused
        (Snapshot(), Counts(25), false),                                  // alerting, 9+
        (Snapshot(), Counts(3), true),                                    // scanning
        (Snapshot(AppGatewayState.GatewayStopped), Counts(3), false),     // offline
        (Snapshot(), null, false),                                        // healthy, no count yet
        (Snapshot(AppGatewayState.Degraded), Counts(0), false),           // degraded
    };

    [Fact]
    public void Swapping_the_shield_over_and_over_leaks_neither_user_nor_gdi_handles()
    {
        StaThread.Run(() =>
        {
            // Every icon drawn once first, so what is counted is the swapping, not WPF warming up.
            var looks = Looks();
            foreach (var (snapshot, counts, scanning) in looks)
            {
                using var warm = ShieldIconFactory.CreateIcon(ShieldIconFactory.KeyFor(snapshot, counts?.Total, scanning));
            }

            // No library disposing the replaced icon here: if the presenter did not, every swap would leave one behind.
            var shown = new List<Drawing.Icon>();
            var before = Handles();
            using (var presenter = new TrayShieldPresenter(ShieldIconFactory.CreateIcon, shown.Add, _ => { }))
            {
                for (var i = 0; i < 800; i++)
                {
                    var (snapshot, counts, scanning) = looks[i % looks.Length];
                    Assert.True(presenter.Update(snapshot, counts, scanning).Icon, $"swap {i} changed nothing");
                }
            }

            var after = Handles();

            Assert.Equal(800, shown.Count);
            Assert.True(after.User <= before.User + 100, $"USER objects {before.User} -> {after.User} over 800 swaps");
            Assert.True(after.Gdi <= before.Gdi + 100, $"GDI objects {before.Gdi} -> {after.Gdi} over 800 swaps");
        });
    }

    [Fact]
    public void An_idle_tray_draws_nothing_encodes_nothing_and_holds_no_new_handle()
    {
        StaThread.Run(() =>
        {
            var created = 0;
            var assigned = 0;
            using var presenter = new TrayShieldPresenter(
                key =>
                {
                    created++;
                    return ShieldIconFactory.CreateIcon(key);
                },
                _ => assigned++,
                _ => { });

            _ = presenter.Update(Snapshot(), Counts(3), scanning: false);
            Assert.Equal(1, created);
            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            var before = Handles();

            // A day of notifications that change nothing the tray shows: new equal counts, a snapshot a poll newer.
            for (var i = 0; i < 2_000; i++)
            {
                _ = presenter.Update(Snapshot() with { PolledAt = At.AddSeconds(i) }, Counts(3), scanning: false);
            }

            var after = Handles();

            Assert.Equal(1, created);
            Assert.Equal(1, assigned);
            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            Assert.True(after.User <= before.User + 40, $"USER objects {before.User} -> {after.User} while idle");
            Assert.True(after.Gdi <= before.Gdi + 40, $"GDI objects {before.Gdi} -> {after.Gdi} while idle");
        });
    }

    [Fact]
    public void Coming_back_to_a_look_reuses_the_drawing_and_makes_only_a_new_handle()
    {
        StaThread.Run(() =>
        {
            using var presenter = new TrayShieldPresenter(ShieldIconFactory.CreateIcon, _ => { }, _ => { });
            var looks = Looks();

            for (var round = 0; round < 5; round++)
            {
                foreach (var (snapshot, counts, scanning) in looks)
                {
                    _ = presenter.Update(snapshot, counts, scanning);
                }
            }

            // Eight looks, but the healthy one twice (with and without a count): seven distinct icons, drawn once each however often they came round.
            Assert.Equal(7, ShieldIconFactory.EncodeCount);
        });
    }
}
