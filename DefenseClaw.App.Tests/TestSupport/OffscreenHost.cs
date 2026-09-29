using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Hosts one element at an exact size in DIPs inside a message-only window (never shown, no taskbar
/// button, no focus) so it gets a real <see cref="PresentationSource"/>: templates apply, Loaded fires,
/// layout runs, and <see cref="RenderPng"/> can rasterize it. UI thread only (see <see cref="UiThread"/>).
/// </summary>
internal sealed class OffscreenHost : IDisposable
{
    private const int HwndMessage = -3;

    private readonly HwndSource _source;
    private readonly Border _frame = new();

    public OffscreenHost(FrameworkElement content, double width, double height)
    {
        _source = new HwndSource(new HwndSourceParameters("DefenseClaw.Offscreen")
        {
            Width = 1,
            Height = 1,
            ParentWindow = new IntPtr(HwndMessage),
            WindowStyle = 0,
        });

        // The window's own background, so a rendered PNG reads like the real window rather than as a
        // transparent cut-out.
        _frame.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        _frame.Width = width;
        _frame.Height = height;
        _frame.Child = content;
        _source.RootVisual = _frame;

        Relayout();
    }

    public FrameworkElement Content => (FrameworkElement)_frame.Child;

    /// <summary>Runs layout to completion, then lets queued dispatcher work (loaded handlers, coalesced scrolls) drain.</summary>
    public void Relayout()
    {
        _frame.Measure(new Size(_frame.Width, _frame.Height));
        _frame.Arrange(new Rect(0, 0, _frame.Width, _frame.Height));
        _frame.UpdateLayout();
        UiThread.Settle();
        _frame.UpdateLayout();
    }

    /// <summary>Resizes the hosted area (the window it stands in for).</summary>
    public void Resize(double width, double height)
    {
        _frame.Width = width;
        _frame.Height = height;
        Relayout();
    }

    public void RenderPng(string path)
    {
        var width = (int)Math.Ceiling(_frame.Width);
        var height = (int)Math.Ceiling(_frame.Height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_frame);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public void Dispose()
    {
        _frame.Child = null;
        _source.RootVisual = null;
        _source.Dispose();
    }
}

/// <summary>Visual-tree helpers for the layout and virtualization assertions.</summary>
internal static class VisualTree
{
    public static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    public static T? Find<T>(DependencyObject root, Func<T, bool>? predicate = null)
        where T : DependencyObject =>
        Descendants<T>(root).FirstOrDefault(candidate => predicate is null || predicate(candidate));
}

/// <summary>
/// Writes a PNG of a hosted view for a human to look at - only when the <c>DC_RENDER_DIR</c> environment variable names a
/// folder. A plain test run writes no files.
/// </summary>
internal static class RenderTo
{
    public const string EnvironmentVariable = "DC_RENDER_DIR";

    public static void Png(OffscreenHost host, string name)
    {
        var directory = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        host.RenderPng(Path.Combine(directory, name + ".png"));
    }
}
