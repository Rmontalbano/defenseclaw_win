using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Startup;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Branding;

/// <summary>
/// The DefenseClaw mark as the tray and the windows get it: a frame drawn for every size Windows asks for, crisp
/// pixels at the tray's 16 and 20 px, and four states an operator can tell apart at 16 px without relying on
/// colour. Drawing needs an STA thread; the factory's caches are process-wide, hence the shared collection.
/// </summary>
[Collection(ShieldIconFactoryCollection.Name)]
public sealed class ShieldArtworkTests
{
    private static readonly ShieldState[] States = Enum.GetValues<ShieldState>();

    [Fact]
    public void A_tray_icon_carries_a_frame_for_every_tray_size_and_hands_the_tray_its_own()
    {
        var original = ShieldIconFactory.CacheDirectory;
        StaThread.Run(() =>
        {
            try
            {
                ShieldIconFactory.CacheDirectory = null; // never the real %LOCALAPPDATA% cache
                ShieldIconFactory.ResetForTests();

                foreach (var state in States)
                {
                    var entries = IcoFile.Entries(ShieldArtwork.EncodeIco(state, ShieldArtwork.TrayIconSizes));
                    Assert.Equal(ShieldArtwork.TrayIconSizes, entries.Select(entry => entry.Width));
                    Assert.All(entries, entry => Assert.False(entry.IsPng, "tray frames are plain 32-bit DIBs"));

                    using var icon = ShieldIconFactory.CreateIcon(state);
                    Assert.Equal(ShieldIconFactory.TrayIconSize, icon.Width);
                }
            }
            finally
            {
                ShieldIconFactory.CacheDirectory = original;
                ShieldIconFactory.ResetForTests();
            }
        });
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(20, true)]
    [InlineData(24, false)] // from 24 px the state badges are the vector ones, anti-aliased
    public void The_smallest_tray_sizes_are_pixel_snapped(int size, bool badgesToo)
    {
        StaThread.Run(() =>
        {
            foreach (var state in badgesToo ? States : new[] { ShieldState.Running })
            {
                var pixels = IcoFile.Pixels(ShieldArtwork.Render(state, size));
                var partial = Enumerable.Range(0, size * size).Count(i => pixels[(i * 4) + 3] is not (0 or 255));

                Assert.True(partial == 0, $"{state} at {size} px has {partial} half-transparent pixels: the grid drawings must be all-or-nothing");
            }
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(36)]
    public void Every_state_is_told_apart_from_every_other_without_colour(int size)
    {
        // Compared as a colour-blind eye would: coverage and lightness only.
        StaThread.Run(() =>
        {
            var looks = States.ToDictionary(state => state, state => Lightness(ShieldArtwork.Render(state, size)));

            foreach (var first in States)
            {
                foreach (var second in States.Where(state => state > first))
                {
                    var (a, b) = (looks[first], looks[second]);
                    var different = Enumerable.Range(0, a.Length).Count(i => Math.Abs(a[i] - b[i]) > 48);
                    Assert.True(different >= size / 2, $"{first} and {second} at {size} px differ in only {different} pixels once colour is taken away");
                }
            }
        });
    }

    [Fact]
    public void A_healthy_shield_is_Cisco_blue_with_nothing_on_it_and_every_other_state_carries_a_badge()
    {
        StaThread.Run(() =>
        {
            Assert.Equal(Color.FromRgb(0x04, 0x9F, 0xD9), ShieldIconFactory.ColorFor(ShieldState.Running));

            var running = IcoFile.Pixels(ShieldArtwork.Render(ShieldState.Running, 64));

            // The face just under the top edge, above the claw's movable finger: the top of the gradient, Cisco blue.
            var at = ((12 * 64) + 48) * 4;
            var (b, g, r) = (running[at], running[at + 1], running[at + 2]);
            Assert.True(Math.Abs(r - 0x04) + Math.Abs(g - 0x9F) + Math.Abs(b - 0xD9) < 40, $"expected Cisco blue near the top of the face, got #{r:X2}{g:X2}{b:X2}");

            // The badge corner: transparent beside the shield's point when healthy, the badge in every other state.
            foreach (var state in States)
            {
                var pixels = IcoFile.Pixels(ShieldArtwork.Render(state, 64));
                Assert.Equal(state == ShieldState.Running ? 0 : 255, pixels[(((56 * 64) + 56) * 4) + 3]);
            }
        });
    }

    [Fact]
    public void Every_state_draws_at_any_size_from_16_up()
    {
        StaThread.Run(() =>
        {
            foreach (var state in States)
            {
                foreach (var size in ShieldArtwork.WindowIconSizes.Concat(new[] { 17, 30, 50, 100 }))
                {
                    var bitmap = ShieldArtwork.Render(state, size);
                    Assert.Equal(size, bitmap.PixelWidth);

                    var pixels = IcoFile.Pixels(bitmap);
                    var opaque = Enumerable.Range(0, size * size).Count(i => pixels[(i * 4) + 3] == 255);
                    Assert.True(opaque > size * size / 3, $"{state} at {size} px is mostly empty ({opaque} opaque pixels)");
                }
            }
        });
    }

    [Fact]
    public void A_window_icon_offers_WPF_a_frame_drawn_for_every_window_size()
    {
        StaThread.Run(() =>
        {
            foreach (var state in States)
            {
                var icon = ShieldIconFactory.CreateWindowIcon(state);

                // WPF only picks per-size frames when the icon's decoder is an icon decoder with all of them.
                Assert.IsType<IconBitmapDecoder>(icon.Decoder);
                Assert.Equal(ShieldArtwork.WindowIconSizes, icon.Decoder.Frames.Select(frame => frame.PixelWidth).Order());
                Assert.Equal(256, icon.PixelWidth);
                Assert.NotSame(icon, ShieldIconFactory.CreateWindowIcon(state));
            }
        });
    }

    /// <summary>Per pixel, lightness 0-255 composited over mid-grey, so transparency counts as a difference too.</summary>
    private static int[] Lightness(BitmapSource bitmap)
    {
        var pixels = IcoFile.Pixels(bitmap);
        var lightness = new int[pixels.Length / 4];
        for (var i = 0; i < lightness.Length; i++)
        {
            var (b, g, r, a) = (pixels[i * 4], pixels[(i * 4) + 1], pixels[(i * 4) + 2], pixels[(i * 4) + 3]);
            var over = 128 * (255 - a) / 255;
            lightness[i] = (int)((0.299 * (r + over)) + (0.587 * (g + over)) + (0.114 * (b + over)));
        }

        return lightness;
    }
}
