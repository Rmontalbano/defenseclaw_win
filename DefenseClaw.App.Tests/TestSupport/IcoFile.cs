using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>Reads <c>.ico</c> files the way Windows does, for the icon tests: the directory, and each image's pixels.</summary>
internal static class IcoFile
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>The ICONDIR entries, in file order. A width or height byte of 0 means 256.</summary>
    public static IReadOnlyList<IcoEntry> Entries(byte[] ico)
    {
        ArgumentNullException.ThrowIfNull(ico);
        Assert.True(ico.Length >= 6, "not an ICO: too short");
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));

        var count = BitConverter.ToUInt16(ico, 4);
        var entries = new List<IcoEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var at = 6 + (16 * i);
            var width = ico[at] == 0 ? 256 : ico[at];
            var height = ico[at + 1] == 0 ? 256 : ico[at + 1];
            var bitCount = BitConverter.ToUInt16(ico, at + 6);
            var length = BitConverter.ToInt32(ico, at + 8);
            var offset = BitConverter.ToInt32(ico, at + 12);
            var data = ico.AsSpan(offset, length).ToArray();
            entries.Add(new IcoEntry(width, height, bitCount, data.AsSpan().StartsWith(PngSignature), data));
        }

        return entries;
    }

    /// <summary>Every image in the file decoded by WIC (what Explorer and WPF use), as premultiplied BGRA bytes.</summary>
    public static IReadOnlyList<(int Size, byte[] Pixels)> DecodedFrames(byte[] ico)
    {
        using var stream = new MemoryStream(ico, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames.Select(frame => (frame.PixelWidth, Pixels(frame))).ToList();
    }

    /// <summary>A bitmap's pixels as premultiplied BGRA, row by row from the top.</summary>
    public static byte[] Pixels(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>The largest per-channel difference between two premultiplied BGRA buffers of the same size.</summary>
    public static int MaxDifference(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        var max = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            max = Math.Max(max, Math.Abs(expected[i] - actual[i]));
        }

        return max;
    }
}

/// <summary>One image in an <c>.ico</c>: its declared size and bit depth, whether it is PNG-compressed, and its bytes.</summary>
internal sealed record IcoEntry(int Width, int Height, int BitCount, bool IsPng, byte[] Data);
