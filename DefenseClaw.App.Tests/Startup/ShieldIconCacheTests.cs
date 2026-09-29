using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using Drawing = System.Drawing;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>ShieldIconFactory keeps process-wide state (its byte cache, the cache directory), so its tests take turns.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShieldIconFactoryCollection
{
    public const string Name = "ShieldIconFactory";
}

/// <summary>
/// The tray shield is drawn by WPF, and the first thing WPF draws in a process brings its media stack up —
/// about half a second measured cold, on a tray-only startup that needs nothing else from it. So the encoded
/// icons persist between launches of one build. These pin what that must never break: an icon read back is
/// the icon that was drawn, a launch that finds one draws nothing, and any trouble with the cache file or
/// directory means "draw it" rather than "no tray icon".
/// <para>
/// Each test points the factory at its own scratch directory and resets its in-memory state, which is what
/// "a fresh launch" looks like to it. Drawing needs an STA thread.
/// </para>
/// </summary>
[Collection(ShieldIconFactoryCollection.Name)]
public sealed class ShieldIconCacheTests : IDisposable
{
    private readonly string? _originalDirectory = ShieldIconFactory.CacheDirectory;
    private readonly TempDirectory _temp = new();

    public ShieldIconCacheTests()
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

    private string CacheDirectory => ShieldIconFactory.CacheDirectory!;

    private string[] CachedFiles() =>
        Directory.Exists(CacheDirectory) ? Directory.GetFiles(CacheDirectory).OrderBy(f => f, StringComparer.Ordinal).ToArray() : Array.Empty<string>();

    /// <summary>The ICO bytes an icon serializes to: what "the same icon" means, HICON handle aside.</summary>
    private static byte[] Serialize(Drawing.Icon icon)
    {
        using var stream = new MemoryStream();
        icon.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void The_first_launch_of_a_build_draws_the_icon_and_persists_it()
    {
        StaThread.Run(() =>
        {
            using var icon = ShieldIconFactory.CreateIcon(ShieldState.Running);

            Assert.Equal(1, ShieldIconFactory.EncodeCount);

            var file = Assert.Single(CachedFiles());
            var name = System.IO.Path.GetFileName(file);
            Assert.StartsWith("shield-", name, StringComparison.Ordinal);
            Assert.EndsWith("-32-running.ico", name, StringComparison.Ordinal);

            var bytes = File.ReadAllBytes(file);
            Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);
            Assert.True(bytes.Length is > 22 and < 64 * 1024, $"unexpected ICO size {bytes.Length}");
        });
    }

    [Fact]
    public void A_later_launch_reads_the_persisted_icon_and_draws_nothing()
    {
        StaThread.Run(() =>
        {
            using var drawn = ShieldIconFactory.CreateIcon(ShieldState.Stopped);
            Assert.Equal(1, ShieldIconFactory.EncodeCount);

            ShieldIconFactory.ResetForTests(); // a new process, as far as the factory can tell

            using var fromDisk = ShieldIconFactory.CreateIcon(ShieldState.Stopped);

            Assert.Equal(0, ShieldIconFactory.EncodeCount);
            Assert.NotEqual(IntPtr.Zero, fromDisk.Handle);
        });
    }

    [Fact]
    public void The_persisted_icon_is_the_icon_that_was_drawn()
    {
        StaThread.Run(() =>
        {
            foreach (var state in Enum.GetValues<ShieldState>())
            {
                ShieldIconFactory.ResetForTests();
                if (Directory.Exists(CacheDirectory))
                {
                    Directory.Delete(CacheDirectory, recursive: true);
                }

                using var drawn = ShieldIconFactory.CreateIcon(state);
                var drawnBytes = Serialize(drawn);

                ShieldIconFactory.ResetForTests();
                using var fromDisk = ShieldIconFactory.CreateIcon(state);

                Assert.Equal(0, ShieldIconFactory.EncodeCount);
                Assert.Equal(drawnBytes, Serialize(fromDisk));
            }
        });
    }

    [Fact]
    public void Every_state_persists_separately()
    {
        StaThread.Run(() =>
        {
            foreach (var state in Enum.GetValues<ShieldState>())
            {
                using var icon = ShieldIconFactory.CreateIcon(state);
            }

            Assert.Equal(4, ShieldIconFactory.EncodeCount);
            Assert.Equal(4, CachedFiles().Length);
        });
    }

    [Fact]
    public void Every_call_hands_out_its_own_icon_whether_it_was_drawn_or_read_back()
    {
        // The tray disposes what it is given when it swaps icons (H.NotifyIcon 2.3.2), so a shared instance would poison later calls.
        StaThread.Run(() =>
        {
            using var first = ShieldIconFactory.CreateIcon(ShieldState.Running);   // drawn
            using var second = ShieldIconFactory.CreateIcon(ShieldState.Running);  // from the byte cache

            ShieldIconFactory.ResetForTests();
            using var third = ShieldIconFactory.CreateIcon(ShieldState.Running);   // from disk
            using var fourth = ShieldIconFactory.CreateIcon(ShieldState.Running);  // from the byte cache again

            var handles = new[] { first.Handle, second.Handle, third.Handle, fourth.Handle };
            Assert.Equal(4, handles.Distinct().Count());
            Assert.All(handles, handle => Assert.NotEqual(IntPtr.Zero, handle));

            first.Dispose();
            using var after = ShieldIconFactory.CreateIcon(ShieldState.Running);
            Assert.NotEqual(IntPtr.Zero, after.Handle);
        });
    }

    [Fact]
    public void A_corrupt_persisted_file_is_drawn_over_and_replaced()
    {
        StaThread.Run(() =>
        {
            using (ShieldIconFactory.CreateIcon(ShieldState.Warning))
            {
            }

            var file = Assert.Single(CachedFiles());
            var good = File.ReadAllBytes(file);
            File.WriteAllBytes(file, new byte[] { 0, 0, 1, 0, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 });

            ShieldIconFactory.ResetForTests();
            using var icon = ShieldIconFactory.CreateIcon(ShieldState.Warning);

            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            Assert.NotEqual(IntPtr.Zero, icon.Handle);
            Assert.Equal(good, File.ReadAllBytes(Assert.Single(CachedFiles())));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(70 * 1024)]
    public void A_persisted_file_of_the_wrong_size_is_not_trusted(int length)
    {
        StaThread.Run(() =>
        {
            using (ShieldIconFactory.CreateIcon(ShieldState.Critical))
            {
            }

            var file = Assert.Single(CachedFiles());
            var good = File.ReadAllBytes(file);
            var bogus = new byte[length];
            if (length >= 4)
            {
                bogus[2] = 1; // a plausible ICO header, so only the size can give it away
            }

            File.WriteAllBytes(file, bogus);

            ShieldIconFactory.ResetForTests();
            using var icon = ShieldIconFactory.CreateIcon(ShieldState.Critical);

            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            Assert.Equal(good, File.ReadAllBytes(Assert.Single(CachedFiles())));
        });
    }

    [Fact]
    public void A_cache_directory_that_cannot_be_written_still_gives_an_icon()
    {
        StaThread.Run(() =>
        {
            // A directory path that runs through an ordinary file: creating it fails.
            var blocker = _temp.WriteFile("blocker", "not a directory");
            ShieldIconFactory.CacheDirectory = System.IO.Path.Combine(blocker, "icons");

            using var icon = ShieldIconFactory.CreateIcon(ShieldState.Running);
            Assert.NotEqual(IntPtr.Zero, icon.Handle);
            Assert.Equal(1, ShieldIconFactory.EncodeCount);

            // ...and the in-memory cache still spares the second call the drawing.
            using var again = ShieldIconFactory.CreateIcon(ShieldState.Running);
            Assert.Equal(1, ShieldIconFactory.EncodeCount);
        });
    }

    [Fact]
    public void With_no_cache_directory_it_behaves_as_it_always_did()
    {
        StaThread.Run(() =>
        {
            ShieldIconFactory.CacheDirectory = null;

            using var icon = ShieldIconFactory.CreateIcon(ShieldState.Running);

            Assert.NotEqual(IntPtr.Zero, icon.Handle);
            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            Assert.False(Directory.Exists(System.IO.Path.Combine(_temp.Path, "icons")));
        });
    }

    [Fact]
    public void Writing_prunes_old_icons_from_other_builds_but_not_recent_ones_or_anything_else()
    {
        StaThread.Run(() =>
        {
            _ = Directory.CreateDirectory(CacheDirectory);
            var old = System.IO.Path.Combine(CacheDirectory, "shield-00000000000000000000000000000000-32-running.ico");
            var recent = System.IO.Path.Combine(CacheDirectory, "shield-22222222222222222222222222222222-32-running.ico");
            var unrelated = System.IO.Path.Combine(CacheDirectory, "notes.txt");
            File.WriteAllBytes(old, new byte[] { 0, 0, 1, 0 });
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            File.WriteAllBytes(recent, new byte[] { 0, 0, 1, 0 }); // e.g. the installed build, while this one is a dev build
            File.WriteAllText(unrelated, "keep me");

            using var icon = ShieldIconFactory.CreateIcon(ShieldState.Running);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(unrelated));
            Assert.Equal(3, CachedFiles().Length); // this build's icon, the recent one and the unrelated file
        });
    }

    [Fact]
    public void An_icon_persisted_by_another_build_is_not_read()
    {
        StaThread.Run(() =>
        {
            using (ShieldIconFactory.CreateIcon(ShieldState.Running))
            {
            }

            // Rename this build's file as if another build had written it.
            var mine = Assert.Single(CachedFiles());
            var theirs = System.IO.Path.Combine(CacheDirectory, "shield-11111111111111111111111111111111-32-running.ico");
            File.Move(mine, theirs);

            ShieldIconFactory.ResetForTests();
            using var icon = ShieldIconFactory.CreateIcon(ShieldState.Running);

            Assert.Equal(1, ShieldIconFactory.EncodeCount);
            Assert.True(File.Exists(theirs)); // left alone (it is recent): only this build's own file is ever read
            Assert.Equal(2, CachedFiles().Length);
        });
    }
}
