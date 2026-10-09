using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using Drawing = System.Drawing;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The icon cache is keyed by the look, not only the state: the alerting state has an icon for each number it can show, and every other
/// state has one. These pin what that costs: one drawing per distinct key per process, one file per key per build, a name that keeps
/// the old files' names, and nothing drawn again for a key that has been seen. (The cache's behaviour with bad files, other builds and
/// unwritable directories is <see cref="ShieldIconCacheTests"/>'s, and holds for every key alike.)
/// </summary>
[Collection(ShieldIconFactoryCollection.Name)]
public sealed class ShieldIconKeyCacheTests : IDisposable
{
    private readonly string? _originalDirectory = ShieldIconFactory.CacheDirectory;
    private readonly TempDirectory _temp = new();

    public ShieldIconKeyCacheTests()
    {
        ShieldIconFactory.CacheDirectory = System.IO.Path.Combine(_temp.Path, "icons");
        ShieldIconFactory.ResetForTests();
    }

    public void Dispose()
    {
        ShieldIconFactory.CacheDirectory = _originalDirectory;
        ShieldIconFactory.ResetForTests();
        _temp.Dispose();
    }

    private string[] CachedNames() =>
        Directory.Exists(ShieldIconFactory.CacheDirectory!)
            ? Directory.GetFiles(ShieldIconFactory.CacheDirectory!).Select(file => System.IO.Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    private static byte[] Serialize(Drawing.Icon icon)
    {
        using var stream = new MemoryStream();
        icon.Save(stream);
        return stream.ToArray();
    }

    private static void Make(TrayShieldKey key)
    {
        using var icon = ShieldIconFactory.CreateIcon(key);
    }

    [Fact]
    public void Each_number_the_icon_can_show_is_drawn_once_however_often_the_tray_swaps_to_it()
    {
        StaThread.Run(() =>
        {
            for (var round = 0; round < 4; round++)
            {
                foreach (var bucket in Enumerable.Range(0, 11))
                {
                    Make(new TrayShieldKey(ShieldState.Critical, bucket));
                }
            }

            // No number, one to nine, and "9+".
            Assert.Equal(11, ShieldIconFactory.EncodeCount);
        });
    }

    [Fact]
    public void Ten_findings_and_five_hundred_share_one_icon()
    {
        StaThread.Run(() =>
        {
            Make(new TrayShieldKey(ShieldState.Critical, 10));
            Make(new TrayShieldKey(ShieldState.Critical, 11));
            Make(new TrayShieldKey(ShieldState.Critical, 500));
            Make(new TrayShieldKey(ShieldState.Critical, AlertBucket.Overflow));

            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            _ = Assert.Single(CachedNames());
        });
    }

    [Fact]
    public void A_state_that_shows_no_number_has_one_icon_whatever_the_count_beside_it()
    {
        StaThread.Run(() =>
        {
            foreach (var state in new[] { ShieldState.Running, ShieldState.Stopped, ShieldState.Warning, ShieldState.Paused, ShieldState.Scanning })
            {
                Make(new TrayShieldKey(state, 0));
                Make(new TrayShieldKey(state, 3));
                Make(new TrayShieldKey(state, 500));
                Make(new TrayShieldKey(state, AlertBucket.Overflow));
            }

            Assert.Equal(5, ShieldIconFactory.EncodeCount);
            Assert.Equal(5, CachedNames().Length);
        });
    }

    [Fact]
    public void The_state_overload_is_the_key_with_no_number()
    {
        StaThread.Run(() =>
        {
            using (ShieldIconFactory.CreateIcon(ShieldState.Critical))
            {
            }

            Make(new TrayShieldKey(ShieldState.Critical, 0));

            Assert.Equal(1, ShieldIconFactory.EncodeCount);
        });
    }

    [Fact]
    public void Every_icon_a_tray_can_need_is_sixteen_drawings_and_sixteen_small_files()
    {
        StaThread.Run(() =>
        {
            foreach (var state in Enum.GetValues<ShieldState>())
            {
                foreach (var bucket in Enumerable.Range(0, 11))
                {
                    Make(new TrayShieldKey(state, bucket));
                }
            }

            Assert.Equal(16, ShieldIconFactory.EncodeCount);
            var files = Directory.GetFiles(ShieldIconFactory.CacheDirectory!);
            Assert.Equal(16, files.Length);

            // Each must stay under the size the cache will read back (a bigger file is taken for something this factory did not write).
            Assert.All(files, file => Assert.True(new FileInfo(file).Length is > 22 and <= 64 * 1024, $"{file} is {new FileInfo(file).Length} bytes"));
        });
    }

    [Fact]
    public void A_numbered_icon_is_persisted_under_a_name_that_says_its_number_and_the_old_names_are_unchanged()
    {
        StaThread.Run(() =>
        {
            Make(new TrayShieldKey(ShieldState.Running, 0));
            Make(new TrayShieldKey(ShieldState.Critical, 0));
            Make(new TrayShieldKey(ShieldState.Critical, 3));
            Make(new TrayShieldKey(ShieldState.Critical, 10));

            var names = CachedNames();

            Assert.Equal(4, names.Length);
            Assert.EndsWith("-tray-running.ico", names.Single(name => name.Contains("running", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.Contains(names, name => name.EndsWith("-tray-critical.ico", StringComparison.Ordinal));
            Assert.Contains(names, name => name.EndsWith("-tray-critical-n3.ico", StringComparison.Ordinal));
            Assert.Contains(names, name => name.EndsWith("-tray-critical-n10.ico", StringComparison.Ordinal));
            Assert.All(names, name => Assert.StartsWith("shield-", name, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_later_launch_reads_back_every_numbered_icon_it_drew_and_draws_nothing()
    {
        StaThread.Run(() =>
        {
            var keys = new[]
            {
                new TrayShieldKey(ShieldState.Critical, 0),
                new TrayShieldKey(ShieldState.Critical, 1),
                new TrayShieldKey(ShieldState.Critical, 7),
                new TrayShieldKey(ShieldState.Critical, AlertBucket.Overflow),
                new TrayShieldKey(ShieldState.Paused, 0),
                new TrayShieldKey(ShieldState.Scanning, 0),
            };
            var drawn = new Dictionary<TrayShieldKey, byte[]>();
            foreach (var key in keys)
            {
                using var icon = ShieldIconFactory.CreateIcon(key);
                drawn[key] = Serialize(icon);
            }

            Assert.Equal(keys.Length, ShieldIconFactory.EncodeCount);

            ShieldIconFactory.ResetForTests(); // a new process, as far as the factory can tell

            foreach (var key in keys)
            {
                using var icon = ShieldIconFactory.CreateIcon(key);
                Assert.Equal(drawn[key], Serialize(icon));
            }

            Assert.Equal(0, ShieldIconFactory.EncodeCount);
        });
    }

    [Fact]
    public void Different_numbers_are_different_icons_on_disk_and_in_hand()
    {
        StaThread.Run(() =>
        {
            using var three = ShieldIconFactory.CreateIcon(new TrayShieldKey(ShieldState.Critical, 3));
            using var four = ShieldIconFactory.CreateIcon(new TrayShieldKey(ShieldState.Critical, 4));
            using var threeAgain = ShieldIconFactory.CreateIcon(new TrayShieldKey(ShieldState.Critical, 3));

            Assert.NotEqual(Serialize(three), Serialize(four));
            Assert.Equal(Serialize(three), Serialize(threeAgain));
            Assert.NotEqual(three.Handle, threeAgain.Handle);
        });
    }

    [Fact]
    public void Every_key_hands_the_tray_the_frame_for_its_size_and_its_own_handle_each_time()
    {
        StaThread.Run(() =>
        {
            var handles = new List<IntPtr>();
            foreach (var key in new[] { new TrayShieldKey(ShieldState.Critical, 7), new TrayShieldKey(ShieldState.Paused, 0), new TrayShieldKey(ShieldState.Scanning, 0) })
            {
                for (var i = 0; i < 3; i++)
                {
                    using var icon = ShieldIconFactory.CreateIcon(key);
                    Assert.Equal(ShieldIconFactory.TrayIconSize, icon.Width);
                    Assert.NotEqual(IntPtr.Zero, icon.Handle);
                    handles.Add(icon.Handle);
                }
            }

            // Handles may be reused once an icon is disposed, so this checks only that each icon was real; the sharing rule is pinned by
            // Every_call_hands_out_its_own_icon_whether_it_was_drawn_or_read_back.
            Assert.Equal(9, handles.Count);
        });
    }

    [Fact]
    public void A_corrupt_numbered_file_is_drawn_over_like_any_other()
    {
        StaThread.Run(() =>
        {
            var key = new TrayShieldKey(ShieldState.Critical, 5);
            Make(key);
            var file = Directory.GetFiles(ShieldIconFactory.CacheDirectory!).Single();
            var good = File.ReadAllBytes(file);
            File.WriteAllBytes(file, new byte[] { 0, 0, 1, 0, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 });

            ShieldIconFactory.ResetForTests();
            using var icon = ShieldIconFactory.CreateIcon(key);

            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            Assert.NotEqual(IntPtr.Zero, icon.Handle);
            Assert.Equal(good, File.ReadAllBytes(Directory.GetFiles(ShieldIconFactory.CacheDirectory!).Single()));
        });
    }
}
