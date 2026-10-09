using System.Windows.Media.Imaging;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Startup;
using DefenseClaw.App.Tests.TestSupport;
using Drawing = System.Drawing;

namespace DefenseClaw.App.Tests.Branding;

/// <summary>
/// The new tray looks as drawn: a frame for every size Windows may ask for at 100 % to 300 % display scaling, whole pixels at the sizes
/// that are pixel grids, and above all a count that can be read at 16 px: each digit, and "9+", is exactly the glyph it should be, in
/// its own badge, at every grid size. Drawing needs an STA thread; the factory's caches are process-wide, hence the shared collection.
/// </summary>
[Collection(ShieldIconFactoryCollection.Name)]
public sealed class TrayShieldArtworkTests
{
    /// <summary>The sizes the tray asks for at 100, 125, 150, 200, 250 and 300 % (the issue's list); the icon carries more (175 and 225 %).</summary>
    private static readonly int[] RequiredFrames = { 16, 20, 24, 32, 40, 48 };

    private static readonly int[] GridSizes = { 16, 20, 24 };

    private static readonly int[] Buckets = Enumerable.Range(1, AlertBucket.Overflow).ToArray();

    /// <summary>The 3 × 5 digits the 16 and 20 px badges must show, typed here independently of the artwork's own table.</summary>
    private static readonly Dictionary<char, string[]> Small = new()
    {
        ['1'] = new[] { ".#.", "##.", ".#.", ".#.", "###" },
        ['2'] = new[] { "###", "..#", "###", "#..", "###" },
        ['3'] = new[] { "###", "..#", "###", "..#", "###" },
        ['4'] = new[] { "#.#", "#.#", "###", "..#", "..#" },
        ['5'] = new[] { "###", "#..", "###", "..#", "###" },
        ['6'] = new[] { "###", "#..", "###", "#.#", "###" },
        ['7'] = new[] { "###", "..#", ".#.", ".#.", ".#." },
        ['8'] = new[] { "###", "#.#", "###", "#.#", "###" },
        ['9'] = new[] { "###", "#.#", "###", "..#", "###" },
        ['+'] = new[] { "...", ".#.", "###", ".#.", "..." },
    };

    /// <summary>The 5 × 7 digits the 24 px badge must show.</summary>
    private static readonly Dictionary<char, string[]> Large = new()
    {
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { ".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
        ['+'] = new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." },
    };

    /// <summary>How big the count badge is, in pixels, at a pixel-grid size: a disc for one digit, a pill for "9+".</summary>
    private static (int Width, int Height) Badge(int size, bool wide) => (size, wide) switch
    {
        (16, false) => (7, 7),
        (16, true) => (9, 7),
        (20, false) => (9, 9),
        (20, true) => (11, 9),
        (24, false) => (11, 11),
        (24, true) => (15, 11),
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    private static byte[] Pixels(ShieldState state, int size, int count = 0) => IcoFile.Pixels(ShieldArtwork.Render(state, size, count));

    private static bool IsWhite(byte[] pixels, int size, int x, int y)
    {
        var i = ((y * size) + x) * 4;
        return pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255 && pixels[i + 3] == 255;
    }

    /// <summary>
    /// The glyph in the count badge, as rows of <c>#</c> and <c>.</c>: every pure-white pixel inside the badge's box (the shield's claw is
    /// off-white and the knockout has cleared it there, so only the glyph is), trimmed to its bounding box.
    /// </summary>
    private static string[] GlyphIn(byte[] pixels, int size, bool wide)
    {
        var (width, height) = Badge(size, wide);
        var left = size - width;
        var top = size - height;
        var ink = new List<(int X, int Y)>();
        for (var y = top; y < size; y++)
        {
            for (var x = left; x < size; x++)
            {
                if (IsWhite(pixels, size, x, y))
                {
                    ink.Add((x, y));
                }
            }
        }

        Assert.NotEmpty(ink);
        var (minX, maxX, minY, maxY) = (ink.Min(p => p.X), ink.Max(p => p.X), ink.Min(p => p.Y), ink.Max(p => p.Y));
        return Enumerable.Range(minY, maxY - minY + 1)
            .Select(y => new string(Enumerable.Range(minX, maxX - minX + 1).Select(x => ink.Contains((x, y)) ? '#' : '.').ToArray()))
            .ToArray();
    }

    /// <summary>What <paramref name="text"/> looks like in a font: its characters side by side with a blank column between, trimmed to the ink.</summary>
    private static string[] Expected(Dictionary<char, string[]> font, string text)
    {
        var height = font['1'].Length;
        var rows = Enumerable.Range(0, height).Select(y => string.Join('.', text.Select(c => font[c][y]))).ToArray();

        // Blank rows at the top or bottom (the plus is shorter than the digits) are not ink.
        var first = Array.FindIndex(rows, row => row.Contains('#'));
        var last = Array.FindLastIndex(rows, row => row.Contains('#'));
        var trimmed = rows[first..(last + 1)];
        var left = trimmed.Min(row => row.IndexOf('#', StringComparison.Ordinal));
        var right = trimmed.Max(row => row.LastIndexOf('#'));
        return trimmed.Select(row => row[left..(right + 1)]).ToArray();
    }

    [Fact]
    public void Every_look_has_a_frame_for_every_dpi_size_and_the_shell_picks_the_exact_one()
    {
        StaThread.Run(() =>
        {
            Assert.All(RequiredFrames, size => Assert.Contains(size, ShieldArtwork.TrayIconSizes));

            foreach (var (name, state, count) in AppIconTestsLooks())
            {
                var ico = ShieldArtwork.EncodeIco(state, ShieldArtwork.TrayIconSizes, count);
                var entries = IcoFile.Entries(ico);

                Assert.True(
                    ShieldArtwork.TrayIconSizes.SequenceEqual(entries.Select(entry => entry.Width)),
                    $"{name}: frames {string.Join(", ", entries.Select(entry => entry.Width))}");
                Assert.All(entries, entry =>
                {
                    Assert.False(entry.IsPng);
                    Assert.Equal(32, entry.BitCount);
                });

                // The shell asks the file for a size: the frame it gets is that size, not a larger one squeezed down.
                foreach (var size in ShieldArtwork.TrayIconSizes)
                {
                    using var stream = new MemoryStream(ico, writable: false);
                    using var icon = new Drawing.Icon(stream, size, size);
                    Assert.True(icon.Width == size && icon.Height == size, $"{name}: asked for {size} px, got {icon.Width} × {icon.Height}");
                }
            }
        });
    }

    [Fact]
    public void Every_look_draws_at_any_size_from_16_up_with_a_badge_in_the_corner_unless_it_is_healthy()
    {
        StaThread.Run(() =>
        {
            foreach (var (name, state, count) in AppIconTestsLooks())
            {
                foreach (var size in ShieldArtwork.WindowIconSizes.Concat(new[] { 17, 30, 50, 100 }))
                {
                    var bitmap = ShieldArtwork.Render(state, size, count);
                    Assert.Equal(size, bitmap.PixelWidth);

                    var pixels = IcoFile.Pixels(bitmap);
                    var opaque = Enumerable.Range(0, size * size).Count(i => pixels[(i * 4) + 3] == 255);
                    Assert.True(opaque > size * size / 3, $"{name} at {size} px is mostly empty ({opaque} opaque pixels)");

                    // Just inside the bottom-right corner: transparent beside the shield's point when healthy, the badge otherwise.
                    var corner = size - (size / 8) - 1;
                    Assert.True(
                        pixels[(((corner * size) + corner) * 4) + 3] == (state == ShieldState.Running ? 0 : 255),
                        $"{name} at {size} px: the pixel at ({corner}, {corner}) is {(state == ShieldState.Running ? "not empty" : "not the badge")}");
                }
            }
        });
    }

    [Fact]
    public void The_number_is_on_every_frame_not_only_the_small_ones()
    {
        StaThread.Run(() =>
        {
            foreach (var size in ShieldArtwork.TrayIconSizes)
            {
                var plain = Pixels(ShieldState.Critical, size);
                foreach (var bucket in Buckets)
                {
                    var numbered = Pixels(ShieldState.Critical, size, bucket);
                    var different = Enumerable.Range(0, size * size).Count(i =>
                        numbered[i * 4] != plain[i * 4] || numbered[(i * 4) + 1] != plain[(i * 4) + 1] || numbered[(i * 4) + 2] != plain[(i * 4) + 2] || numbered[(i * 4) + 3] != plain[(i * 4) + 3]);

                    // At least the glyph's own ink differs from the bare exclamation mark's, at every size and for every number.
                    Assert.True(different >= 3, $"{AlertBucket.Text(bucket)} at {size} px differs from the unnumbered badge in only {different} pixels");
                }
            }
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void Every_digit_and_nine_plus_is_exactly_its_glyph_in_whole_pixels(int size)
    {
        StaThread.Run(() =>
        {
            var font = size >= 24 ? Large : Small;
            foreach (var bucket in Buckets)
            {
                var text = AlertBucket.Text(bucket);
                var pixels = Pixels(ShieldState.Critical, size, bucket);

                // The badge is all-or-nothing: no half-lit pixel that a 16 px glyph could blur into.
                var partial = Enumerable.Range(0, size * size).Count(i => pixels[(i * 4) + 3] is not (0 or 255));
                Assert.True(partial == 0, $"{text} at {size} px has {partial} half-transparent pixels");

                var actual = GlyphIn(pixels, size, wide: text.Length > 1);
                var expected = Expected(font, text);
                Assert.True(
                    expected.SequenceEqual(actual),
                    $"{text} at {size} px should read\n{string.Join('\n', expected)}\nbut reads\n{string.Join('\n', actual)}");
            }
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void The_glyph_has_room_in_its_badge_a_pixel_of_badge_all_round(int size)
    {
        // Legible means not touching the rim: every pixel above, below, left and right of the ink is badge (or more ink), never the shield
        // or the transparent corner. (Diagonally a digit's corner may meet the disc's cut corner; that is the disc's shape, not a crowded glyph.)
        StaThread.Run(() =>
        {
            foreach (var bucket in Buckets)
            {
                var pixels = Pixels(ShieldState.Critical, size, bucket);
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        if (!IsWhite(pixels, size, x, y) || x < size - Badge(size, bucket == AlertBucket.Overflow).Width || y < size - Badge(size, bucket == AlertBucket.Overflow).Height)
                        {
                            continue;
                        }

                        foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                        {
                            var (nx, ny) = (x + dx, y + dy);
                            Assert.True(
                                nx >= 0 && ny >= 0 && nx < size && ny < size && pixels[(((ny * size) + nx) * 4) + 3] == 255 && (IsWhite(pixels, size, nx, ny) || IsRed(pixels, size, nx, ny)),
                                $"{AlertBucket.Text(bucket)} at {size} px: the ink at ({x}, {y}) touches something that is not badge at ({nx}, {ny})");
                        }
                    }
                }
            }
        });
    }

    private static bool IsRed(byte[] pixels, int size, int x, int y)
    {
        var i = ((y * size) + x) * 4;
        return Math.Abs(pixels[i + 2] - 0xD1) < 8 && Math.Abs(pixels[i + 1] - 0x34) < 8 && Math.Abs(pixels[i] - 0x38) < 8;
    }

    [Fact]
    public void Nine_plus_is_a_pill_wider_than_the_disc_at_every_size()
    {
        StaThread.Run(() =>
        {
            foreach (var size in ShieldArtwork.TrayIconSizes)
            {
                var disc = RedExtent(Pixels(ShieldState.Critical, size, 9), size);
                var pill = RedExtent(Pixels(ShieldState.Critical, size, AlertBucket.Overflow), size);

                Assert.True(pill.Width > disc.Width, $"at {size} px the pill is {pill.Width} wide and the disc {disc.Width}");
                Assert.True(Math.Abs(pill.Height - disc.Height) <= 1, $"at {size} px the pill is {pill.Height} tall and the disc {disc.Height}");
                Assert.True(pill.Width <= size * 0.7, $"at {size} px the pill takes {pill.Width} of the icon's width");
            }
        });
    }

    /// <summary>The bounding box of the badge's red (the critical colour, within a step) in the bottom-right quarter.</summary>
    private static (int Width, int Height) RedExtent(byte[] pixels, int size)
    {
        var red = new List<(int X, int Y)>();
        for (var y = size / 3; y < size; y++)
        {
            for (var x = size / 3; x < size; x++)
            {
                var i = ((y * size) + x) * 4;
                if (pixels[i + 3] > 200 && Math.Abs(pixels[i + 2] - 0xD1) < 40 && Math.Abs(pixels[i + 1] - 0x34) < 40 && Math.Abs(pixels[i] - 0x38) < 40)
                {
                    red.Add((x, y));
                }
            }
        }

        Assert.NotEmpty(red);
        return (red.Max(p => p.X) - red.Min(p => p.X) + 1, red.Max(p => p.Y) - red.Min(p => p.Y) + 1);
    }

    [Fact]
    public void Every_number_looks_different_from_every_other_at_every_tray_size()
    {
        StaThread.Run(() =>
        {
            foreach (var size in ShieldArtwork.TrayIconSizes)
            {
                var looks = Buckets.Select(bucket => (Bucket: bucket, Pixels: Pixels(ShieldState.Critical, size, bucket))).ToList();
                foreach (var first in looks)
                {
                    foreach (var second in looks.Where(look => look.Bucket > first.Bucket))
                    {
                        Assert.False(
                            first.Pixels.SequenceEqual(second.Pixels),
                            $"{AlertBucket.Text(first.Bucket)} and {AlertBucket.Text(second.Bucket)} are the same icon at {size} px");
                    }
                }
            }
        });
    }

    [Theory]
    [InlineData(ShieldState.Running)]
    [InlineData(ShieldState.Stopped)]
    [InlineData(ShieldState.Warning)]
    [InlineData(ShieldState.Paused)]
    [InlineData(ShieldState.Scanning)]
    public void A_state_with_a_glyph_of_its_own_shows_no_number(ShieldState state)
    {
        StaThread.Run(() =>
        {
            foreach (var size in new[] { 16, 20, 24, 32, 48 })
            {
                var plain = Pixels(state, size);
                Assert.True(plain.SequenceEqual(Pixels(state, size, 7)), $"{state} at {size} px changed with a count of 7");
                Assert.True(plain.SequenceEqual(Pixels(state, size, 500)), $"{state} at {size} px changed with a count of 500");
            }
        });
    }

    [Fact]
    public void Ten_findings_and_five_hundred_are_the_same_icon()
    {
        StaThread.Run(() =>
        {
            foreach (var size in ShieldArtwork.TrayIconSizes)
            {
                Assert.True(
                    Pixels(ShieldState.Critical, size, 10).SequenceEqual(Pixels(ShieldState.Critical, size, 500)),
                    $"at {size} px");
                Assert.True(
                    Pixels(ShieldState.Critical, size, AlertBucket.Overflow).SequenceEqual(Pixels(ShieldState.Critical, size, 10)),
                    $"at {size} px");
            }
        });
    }

    [Theory]
    [InlineData(ShieldState.Paused)]
    [InlineData(ShieldState.Scanning)]
    public void Paused_and_scanning_are_whole_pixels_at_the_pixel_grid_sizes(ShieldState state)
    {
        StaThread.Run(() =>
        {
            foreach (var size in new[] { 16, 20 })
            {
                var pixels = Pixels(state, size);
                var partial = Enumerable.Range(0, size * size).Count(i => pixels[(i * 4) + 3] is not (0 or 255));
                Assert.True(partial == 0, $"{state} at {size} px has {partial} half-transparent pixels");
            }
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(36)]
    public void A_number_is_told_apart_from_every_state_without_colour(int size)
    {
        // Compared as a colour-blind eye would: coverage and lightness only (the same measure as the state test in ShieldArtworkTests).
        StaThread.Run(() =>
        {
            var others = new[] { ShieldState.Running, ShieldState.Stopped, ShieldState.Warning, ShieldState.Paused, ShieldState.Scanning }
                .Select(state => (State: state, Look: Lightness(Pixels(state, size))))
                .ToList();

            foreach (var bucket in Buckets)
            {
                var number = Lightness(Pixels(ShieldState.Critical, size, bucket));
                foreach (var (state, look) in others)
                {
                    var different = Enumerable.Range(0, number.Length).Count(i => Math.Abs(number[i] - look[i]) > 48);
                    Assert.True(different >= size / 2, $"{AlertBucket.Text(bucket)} and {state} at {size} px differ in only {different} pixels once colour is taken away");
                }
            }
        });
    }

    /// <summary>Per pixel, lightness 0-255 composited over mid-grey, so transparency counts as a difference too.</summary>
    private static int[] Lightness(byte[] pixels)
    {
        var lightness = new int[pixels.Length / 4];
        for (var i = 0; i < lightness.Length; i++)
        {
            var (b, g, r, a) = (pixels[i * 4], pixels[(i * 4) + 1], pixels[(i * 4) + 2], pixels[(i * 4) + 3]);
            var over = 128 * (255 - a) / 255;
            lightness[i] = (int)((0.299 * (r + over)) + (0.587 * (g + over)) + (0.114 * (b + over)));
        }

        return lightness;
    }

    [Fact]
    public void The_scanning_badge_is_not_the_pause_badge_or_the_stop_badge_even_in_one_colour()
    {
        StaThread.Run(() =>
        {
            foreach (var size in new[] { 16, 20, 24, 32, 48 })
            {
                var paused = Pixels(ShieldState.Paused, size);
                var scanning = Pixels(ShieldState.Scanning, size);
                Assert.False(paused.SequenceEqual(scanning), $"at {size} px");
                Assert.False(Pixels(ShieldState.Stopped, size).SequenceEqual(paused), $"at {size} px");
            }
        });
    }

    [Fact]
    public void A_paused_shield_stays_Cisco_blue_where_a_stopped_one_goes_grey()
    {
        // Paused is the app not looking, not the gateway being down: the shield keeps its colour and only the badge changes.
        StaThread.Run(() =>
        {
            var running = Pixels(ShieldState.Running, 64);
            var paused = Pixels(ShieldState.Paused, 64);
            var stopped = Pixels(ShieldState.Stopped, 64);

            // The top of the face, above the claw and well clear of the corner badge.
            var at = ((12 * 64) + 48) * 4;
            Assert.True(running.AsSpan(at, 4).SequenceEqual(paused.AsSpan(at, 4)));
            Assert.False(running.AsSpan(at, 4).SequenceEqual(stopped.AsSpan(at, 4)));
        });
    }

    [Fact]
    public void A_window_icon_for_every_new_state_carries_every_window_size()
    {
        StaThread.Run(() =>
        {
            foreach (var state in new[] { ShieldState.Paused, ShieldState.Scanning })
            {
                var icon = ShieldIconFactory.CreateWindowIcon(state);

                Assert.IsType<IconBitmapDecoder>(icon.Decoder);
                Assert.Equal(ShieldArtwork.WindowIconSizes, icon.Decoder.Frames.Select(frame => frame.PixelWidth).Order());
            }
        });
    }

    /// <summary>The looks the contact sheet lists: every state, and the alerting one with every number.</summary>
    private static IEnumerable<(string Name, ShieldState State, int Count)> AppIconTestsLooks() => AppIconTests.TrayLooks;
}
