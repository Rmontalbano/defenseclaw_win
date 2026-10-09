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

    /// <summary>
    /// Every look the tray can have, in the order the sheet lists them: the four states that are always there, the two that are new (paused,
    /// scanning), and the alerting one with no number and with each number the icon can show (1 to 9, then "9+").
    /// </summary>
    internal static readonly (string Name, ShieldState State, int Count)[] TrayLooks =
    {
        ("Running (healthy)", ShieldState.Running, 0),
        ("Stopped (offline)", ShieldState.Stopped, 0),
        ("Warning (degraded)", ShieldState.Warning, 0),
        ("Paused", ShieldState.Paused, 0),
        ("Scanning", ShieldState.Scanning, 0),
        ("Alerting, no number", ShieldState.Critical, 0),
        ("Alerting, 1", ShieldState.Critical, 1),
        ("Alerting, 2", ShieldState.Critical, 2),
        ("Alerting, 3", ShieldState.Critical, 3),
        ("Alerting, 4", ShieldState.Critical, 4),
        ("Alerting, 5", ShieldState.Critical, 5),
        ("Alerting, 6", ShieldState.Critical, 6),
        ("Alerting, 7", ShieldState.Critical, 7),
        ("Alerting, 8", ShieldState.Critical, 8),
        ("Alerting, 9", ShieldState.Critical, 9),
        ("Alerting, 9+", ShieldState.Critical, AlertBucket.Overflow),
    };

    /// <summary>
    /// The review sheet, on a Windows-light and a Windows-dark background side by side: the app icon at every size it ships, then one
    /// row per tray look (<see cref="TrayLooks"/>) with every tray size at actual size (16 to 48 px, 100 % to 300 % display scaling) and
    /// the sizes 100 % to 200 % uses magnified pixel for pixel.
    /// </summary>
    private static void WriteContactSheet(string path)
    {
        const int PanelWidth = 940;
        const int LabelWidth = 150;
        const int RowHeight = 108;
        var actual = ShieldArtwork.TrayIconSizes;
        var magnified = new[] { (Size: 16, Zoom: 6), (Size: 20, Zoom: 5), (Size: 24, Zoom: 4), (Size: 32, Zoom: 3) };

        var appIcons = ShieldArtwork.AppIconSizes.Reverse().Select(size => ShieldArtwork.Render(ShieldState.Running, size)).ToList();
        var appIconsHeight = appIcons.Max(image => image.PixelHeight);

        double Panel(DrawingContext? context, double left, string ink)
        {
            var y = 14.0;
            if (context is not null)
            {
                Label(context, "App icon (Assets\\DefenseClaw.ico): " + string.Join(", ", ShieldArtwork.AppIconSizes.Reverse()) + " px", ink, left + 20, y);
            }

            y += 24;
            var x = left + 20;
            foreach (var image in appIcons)
            {
                context?.DrawImage(image, new Rect(x, y + appIconsHeight - image.PixelHeight, image.PixelWidth, image.PixelHeight));
                x += image.PixelWidth + 18;
            }

            y += appIconsHeight + 22;
            if (context is not null)
            {
                Label(context, "Tray: actual size at 16, 20, 24, 28, 32, 36, 40 and 48 px, then 16 px ×6, 20 px ×5, 24 px ×4, 32 px ×3", ink, left + 20, y);
            }

            y += 26;
            foreach (var (name, state, count) in TrayLooks)
            {
                if (context is not null)
                {
                    Label(context, name, ink, left + 20, y + 4);
                }

                var at = left + 20 + LabelWidth;
                foreach (var size in actual)
                {
                    context?.DrawImage(ShieldArtwork.Render(state, size, count), new Rect(at, y + 4, size, size));
                    at += size + 8;
                }

                at += 16;
                foreach (var (size, zoom) in magnified)
                {
                    var image = Magnify(ShieldArtwork.Render(state, size, count), zoom);
                    context?.DrawImage(image, new Rect(at, y, image.PixelWidth, image.PixelHeight));
                    at += image.PixelWidth + 8;
                }

                y += RowHeight;
            }

            return y + 10;
        }

        var height = (int)Math.Ceiling(Panel(null, 0, "#000000"));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var left = 0;
            foreach (var (background, ink) in new[] { ("#F3F3F3", "#1B1B1B"), ("#202020", "#F3F3F3") })
            {
                context.DrawRectangle(Brush(background), null, new Rect(left, 0, PanelWidth, height));
                _ = Panel(context, left, ink);
                left += PanelWidth;
            }
        }

        var bitmap = new RenderTargetBitmap(PanelWidth * 2, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

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
