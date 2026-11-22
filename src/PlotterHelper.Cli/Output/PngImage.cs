using System.IO.Compression;

namespace PlotterHelper.Cli.Output;

/// <summary>
/// Minimal, dependency-free PNG encoder for 8-bit RGBA images. Used only to render
/// the Klipper upload thumbnail -- keeping it in-tree avoids pulling in an image
/// library (and its native deps) into the single-file engine.
///
/// The PNG IDAT chunk stores zlib-format (RFC 1950) data, i.e. a 2-byte header, raw
/// DEFLATE, and a 4-byte Adler-32 trailer. .NET's <see cref="ZLibStream"/> emits
/// exactly that wrapper, so we can feed it the raw scanlines directly.
/// </summary>
internal static class PngImage
{
    private static readonly byte[] Signature =
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // CRC-32 (PNG polynomial 0xEDB88320) over the chunk type + data.
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint idx = 0; idx < 256; idx++)
        {
            uint crc = idx;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (0xEDB88320u ^ (crc >> 1)) : (crc >> 1);
            table[idx] = crc;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var byt in type) crc = CrcTable[(crc ^ byt) & 0xFF] ^ (crc >> 8);
        foreach (var byt in data) crc = CrcTable[(crc ^ byt) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteBe(byte[] buf, int off, int value)
    {
        buf[off] = (byte)(value >> 24);
        buf[off + 1] = (byte)(value >> 16);
        buf[off + 2] = (byte)(value >> 8);
        buf[off + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        stream.Write(len, 0, 4);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);
        var crcBytes = new byte[4];
        WriteBe(crcBytes, 0, (int)Crc32(typeBytes, data));
        stream.Write(crcBytes, 0, 4);
    }

    /// <summary>Encode an RGBA pixel buffer (row-major, top-to-bottom) as a PNG.</summary>
    public static byte[] EncodeRgba(int width, int height, byte[] rgba)
    {
        // IHDR: width, height, bitDepth=8, colorType=6 (RGBA), comp=0, filter=0, interlace=0.
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace

        // Raw image data: one filter byte (0 = None) per scanline, then the RGBA row.
        int rowBytes = width * 4;
        var raw = new byte[height * (rowBytes + 1)];
        for (int y = 0; y < height; y++)
        {
            int off = y * (rowBytes + 1);
            raw[off] = 0; // filter: None
            Buffer.BlockCopy(rgba, y * rowBytes, raw, off + 1, rowBytes);
        }

        // zlib-compress the raw scanlines (ZLibStream writes the RFC-1950 wrapper
        // PNG IDAT requires).
        byte[] idat;
        using (var memStream = new MemoryStream())
        {
            using (var zlibStream = new ZLibStream(memStream, CompressionLevel.Optimal))
            {
                zlibStream.Write(raw, 0, raw.Length);
                zlibStream.Flush();
            }
            idat = memStream.ToArray();
        }

        using var outStream = new MemoryStream();
        outStream.Write(Signature, 0, Signature.Length);
        WriteChunk(outStream, "IHDR", ihdr);
        WriteChunk(outStream, "IDAT", idat);
        WriteChunk(outStream, "IEND", System.Array.Empty<byte>());
        return outStream.ToArray();
    }
}
