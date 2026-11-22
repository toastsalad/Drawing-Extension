using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PlotterHelper.Trace;

/// <summary>
/// Dependency-free reader for 8-bit non-interlaced PNG: greyscale, greyscale+alpha, RGB, RGBA
/// and palette. Anything else, JPEG included, must be transcoded to PNG first.
/// </summary>
public static class PngReader
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static GrayImage Read(string path) => Read(File.ReadAllBytes(path));

    public static GrayImage Read(byte[] file)
    {
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG file");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        var idat = new MemoryStream();

        int pos = 8;
        while (pos + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos, 4));
            string type = Encoding.ASCII.GetString(file, pos + 4, 4);
            int dataStart = pos + 8;
            if (length < 0 || dataStart + (long)length + 4 > file.Length)
                throw new InvalidDataException($"truncated PNG chunk '{type}'");

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataStart, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(dataStart + 4, 4));
                    bitDepth = file[dataStart + 8];
                    colorType = file[dataStart + 9];
                    interlace = file[dataStart + 12];
                    break;
                case "PLTE":
                    palette = file.AsSpan(dataStart, length).ToArray();
                    break;
                case "tRNS":
                    paletteAlpha = file.AsSpan(dataStart, length).ToArray();
                    break;
                case "IDAT":
                    idat.Write(file, dataStart, length);
                    break;
            }

            pos = dataStart + length + 4;
            if (type == "IEND") break;
        }

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("PNG has no usable IHDR");
        if (bitDepth != 8)
            throw new NotSupportedException($"PNG bit depth {bitDepth} not supported (8 only)");
        if (interlace != 0)
            throw new NotSupportedException("interlaced PNG not supported");

        int channels = colorType switch
        {
            0 => 1, // greyscale
            2 => 3, // RGB
            3 => 1, // palette index
            4 => 2, // greyscale + alpha
            6 => 4, // RGBA
            _ => throw new NotSupportedException($"PNG colour type {colorType} not supported"),
        };

        long strideL = (long)width * channels;
        long pixelsL = strideL * height;
        if (pixelsL > int.MaxValue)
            throw new NotSupportedException("PNG too large for a single buffer");
        int stride = (int)strideL;
        var scan = Unfilter(Decompress(idat.ToArray()), stride, height, channels);

        var image = new GrayImage(width, height);
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
                image[x, y] = ToGrey(scan, row + x * channels, colorType, palette, paletteAlpha);
        }
        return image;
    }

    /// <summary>Undo the per-scanline PNG filters (None/Sub/Up/Average/Paeth).</summary>
    private static byte[] Unfilter(byte[] raw, int stride, int height, int channels)
    {
        if (raw.Length < (long)(stride + 1) * height)
            throw new InvalidDataException("truncated PNG image data");

        var scan = new byte[stride * height];
        int src = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = raw[src++];
            int rowStart = y * stride;
            for (int column = 0; column < stride; column++)
            {
                int left = column >= channels ? scan[rowStart + column - channels] : 0;
                int up = y > 0 ? scan[rowStart - stride + column] : 0;
                int upLeft = column >= channels && y > 0 ? scan[rowStart - stride + column - channels] : 0;
                int rawByte = raw[src + column];
                int value = filter switch
                {
                    0 => rawByte,
                    1 => rawByte + left,
                    2 => rawByte + up,
                    3 => rawByte + ((left + up) >> 1),
                    4 => rawByte + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"unsupported PNG filter {filter}"),
                };
                scan[rowStart + column] = (byte)value;
            }
            src += stride;
        }
        return scan;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int distanceLeft = Math.Abs(estimate - left), distanceUp = Math.Abs(estimate - up), distanceUpLeft = Math.Abs(estimate - upLeft);
        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpLeft) return left;
        return distanceUp <= distanceUpLeft ? up : upLeft;
    }

    /// <summary>Composite any alpha over white, then take the luma.</summary>
    private static byte ToGrey(byte[] scan, int at, int colorType, byte[]? palette, byte[]? paletteAlpha)
    {
        static byte Luma(int red, int green, int blue) => (byte)Math.Clamp((299 * red + 587 * green + 114 * blue) / 1000, 0, 255);
        static byte OverWhite(int value, int alpha) => (byte)((value * alpha + 255 * (255 - alpha)) / 255);

        switch (colorType)
        {
            case 0:
                return scan[at];
            case 4:
                return OverWhite(scan[at], scan[at + 1]);
            case 2:
                return Luma(scan[at], scan[at + 1], scan[at + 2]);
            case 6:
                return Luma(OverWhite(scan[at], scan[at + 3]), OverWhite(scan[at + 1], scan[at + 3]), OverWhite(scan[at + 2], scan[at + 3]));
            default:
            {
                int index = scan[at];
                if (palette == null || index * 3 + 2 >= palette.Length)
                    return 255;
                int alpha = paletteAlpha != null && index < paletteAlpha.Length ? paletteAlpha[index] : 255;
                return Luma(OverWhite(palette[index * 3], alpha), OverWhite(palette[index * 3 + 1], alpha), OverWhite(palette[index * 3 + 2], alpha));
            }
        }
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
