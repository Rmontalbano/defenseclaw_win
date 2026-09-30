using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Startup;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Branding;

/// <summary>
/// <c>Assets\DefenseClaw.ico</c> — the exe's own icon (Explorer, Start menu, pinned taskbar buttons) — is the
/// repo's one binary, and it is generated from <see cref="ShieldArtwork"/>, the same source as the tray and the
/// window icons. These hold the committed file to the current artwork, so a change to the artwork that is not
/// followed by regenerating the file fails here instead of shipping two different marks.
/// <para>
/// <b>Regenerating it:</b> <c>$env:DC_WRITE_APP_ICON = "1"; dotnet test DefenseClaw.App.Tests --filter
/// FullyQualifiedName~AppIconTests</c> rewrites the file. Point <c>DC_ICON_SHEET</c> at a <c>.png</c> path as well
/// (or alone) for a contact sheet: every size on a light and a dark background, and every tray state at the
/// sizes 100-250 % display scaling uses, magnified — look at it before committing new artwork.
/// </para>
/// </summary>
[Collection(ShieldIconFactoryCollection.Name)]
public sealed class AppIconTests
{
    [Fact]
    public void The_committed_app_icon_carries_every_size_with_the_256_px_image_png_compressed()
    {
        var entries = IcoFile.Entries(File.ReadAllBytes(AppIconPath()));

        Assert.Equal(ShieldArtwork.AppIconSizes, entries.Select(entry => entry.Width));
        Assert.All(entries, entry =>
        {
            Assert.Equal(entry.Width, entry.Height);
            Assert.Equal(32, entry.BitCount);
            Assert.Equal(entry.Width >= 256, entry.IsPng);
        });
    }

    [Fact]
    public void The_committed_app_icon_is_the_current_artwork()
    {
        var frames = IcoFile.DecodedFrames(File.ReadAllBytes(AppIconPath()));

        StaThread.Run(() =>
        {
            foreach (var (size, pixels) in frames)
            {
                var expected = IcoFile.Pixels(ShieldArtwork.Render(ShieldState.Running, size));

                // Straight-alpha storage rounds each channel once each way, so allow a step or two, not a redraw.
                var difference = IcoFile.MaxDifference(expected, pixels);
                Assert.True(
                    difference <= 3,
                    $"The {size} px image in Assets\\DefenseClaw.ico differs from ShieldArtwork by up to {difference} per channel. " +
                    "Regenerate it: $env:DC_WRITE_APP_ICON = \"1\"; dotnet test DefenseClaw.App.Tests --filter FullyQualifiedName~AppIconTests");
            }
        });
    }

    [IconGeneratorFact]
    public void Regenerate_the_app_icon_and_contact_sheet()
    {
        StaThread.Run(() =>
        {
            if (Environment.GetEnvironmentVariable("DC_WRITE_APP_ICON") == "1")
            {
                File.WriteAllBytes(AppIconPath(), ShieldArtwork.EncodeIco(ShieldState.Running, ShieldArtwork.AppIconSizes));
            }

            if (Environment.GetEnvironmentVariable("DC_ICON_SHEET") is { Length: > 0 } sheet)
            {
                WriteContactSheet(sheet);
            }
        });
    }

    /// <summary><c>DefenseClaw.App\Assets\DefenseClaw.ico</c> in the source tree this test assembly was built from.</summary>
    private static string AppIconPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DefenseClaw.Win.sln")))
            {
                return Path.Combine(directory.FullName, "DefenseClaw.App", "Assets", "DefenseClaw.ico");
            }
        }

        throw new InvalidOperationException($"No DefenseClaw.Win.sln above {AppContext.BaseDirectory}.");
    }

    private static readonly ShieldState[] TrayStates = { ShieldState.Running, ShieldState.Stopped, ShieldState.Warning, ShieldState.Critical };

    /// <summary>
    /// The review sheet: on a Windows-light and a Windows-dark background, the app icon at every size it ships,
    /// each tray state at 16-40 px (100-250 % scaling) actual size, and the tray sizes magnified pixel for pixel.
    /// </summary>
    private static void WriteContactSheet(string path)
    {
        const int Width = 1180;

        // Each section: a caption, then images left to right (each with the gap after it), wrapping at the edge.
        var sections = new List<(string Caption, List<(BitmapSource Image, double Gap)> Images)>
        {
            ("App icon (Assets\\DefenseClaw.ico): " + string.Join(", ", ShieldArtwork.AppIconSizes.Reverse()) + " px",
                ShieldArtwork.AppIconSizes.Reverse().Select(size => (ShieldArtwork.Render(ShieldState.Running, size), 18.0)).ToList()),
            ("Tray, actual size (16, 20, 24, 32, 36, 40 px): running, stopped, warning, critical",
                Zoomed(new[] { (16, 1), (20, 1), (24, 1), (32, 1), (36, 1), (40, 1) }, 8, 40)),
            ("Tray, magnified: 16 px ×8, 20 px ×6, 24 px ×5 (100 %, 125 %, 150 % display scaling)",
                Zoomed(new[] { (16, 8), (20, 6), (24, 5) }, 6, 28)),
            ("Tray, magnified: 32 px, 36 px, 40 px ×3 (200 %, 225 %, 250 % display scaling)",
                Zoomed(new[] { (32, 3), (36, 3), (40, 3) }, 6, 28)),
        };

        // Laid out once without drawing to measure a panel, then drawn once per background.
        double Layout(DrawingContext? context, double top, string ink)
        {
            var y = top + 14;
            foreach (var (caption, images) in sections)
            {
                if (context is not null)
                {
                    Label(context, caption, ink, 20, y);
                }

                y += 24;
                var (x, rowHeight) = (20.0, 0.0);
                foreach (var (image, gap) in images)
                {
                    if (x + image.PixelWidth > Width - 20)
                    {
                        (x, y, rowHeight) = (20, y + rowHeight + 10, 0);
                    }

                    context?.DrawImage(image, new Rect(x, y, image.PixelWidth, image.PixelHeight));
                    rowHeight = Math.Max(rowHeight, image.PixelHeight);
                    x += image.PixelWidth + gap;
                }

                y += rowHeight + 18;
            }

            return y - top;
        }

        var panelHeight = (int)Math.Ceiling(Layout(null, 0, "#000000"));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var top = 0;
            foreach (var (background, ink) in new[] { ("#F3F3F3", "#1B1B1B"), ("#202020", "#F3F3F3") })
            {
                context.DrawRectangle(Brush(background), null, new Rect(0, top, Width, panelHeight));
                _ = Layout(context, top, ink);
                top += panelHeight;
            }
        }

        var bitmap = new RenderTargetBitmap(Width, panelHeight * 2, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Every tray state at each (size, zoom), grouped by state: <paramref name="gap"/> between sizes, <paramref name="stateGap"/> between states.</summary>
    private static List<(BitmapSource Image, double Gap)> Zoomed((int Size, int Zoom)[] sizes, double gap, double stateGap) =>
        TrayStates
            .SelectMany(state => sizes.Select((entry, index) => (
                Magnify(ShieldArtwork.Render(state, entry.Size), entry.Zoom),
                index == sizes.Length - 1 ? stateGap : gap)))
            .ToList();

    private static void Label(DrawingContext context, string text, string ink, double x, double y) =>
        context.DrawText(
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brush(ink), 1.0),
            new Point(x, y));

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    /// <summary>Nearest-neighbour enlargement, so every pixel of a small icon shows as a crisp square.</summary>
    private static BitmapSource Magnify(BitmapSource source, int zoom)
    {
        if (zoom == 1)
        {
            return source;
        }

        var size = source.PixelWidth;
        var pixels = IcoFile.Pixels(source);
        var large = new byte[size * zoom * size * zoom * 4];
        for (var y = 0; y < size * zoom; y++)
        {
            for (var x = 0; x < size * zoom; x++)
            {
                Array.Copy(pixels, (((y / zoom) * size) + (x / zoom)) * 4, large, ((y * size * zoom) + x) * 4, 4);
            }
        }

        return BitmapSource.Create(size * zoom, size * zoom, 96, 96, PixelFormats.Pbgra32, null, large, size * zoom * 4);
    }
}

/// <summary>Runs only when asked to, through <c>DC_WRITE_APP_ICON=1</c> and/or <c>DC_ICON_SHEET=&lt;png path&gt;</c>.</summary>
internal sealed class IconGeneratorFactAttribute : FactAttribute
{
    public IconGeneratorFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DC_WRITE_APP_ICON") != "1" &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DC_ICON_SHEET")))
        {
            Skip = "Generator: set DC_WRITE_APP_ICON=1 to rewrite Assets\\DefenseClaw.ico, DC_ICON_SHEET=<png> for a contact sheet.";
        }
    }
}
