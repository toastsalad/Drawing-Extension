using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class ThumbnailTests
{
    private static SvgDocument Doc(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false,
            canvasHeight: 100, transformOrigin: true);

    private static readonly byte[] PngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public void PngImage_Encodes_Valid_RGBA_Png_With_Correct_Dimensions()
    {
        int w = 5, h = 3;
        var rgba = new byte[w * h * 4];
        for (int pixel = 0; pixel < rgba.Length; pixel += 4) { rgba[pixel] = 10; rgba[pixel + 1] = 20; rgba[pixel + 2] = 30; rgba[pixel + 3] = 255; }
        var png = Cli.Output.PngImage.EncodeRgba(w, h, rgba);

        // PNG signature.
        for (int sigByte = 0; sigByte < PngSig.Length; sigByte++) Assert.Equal(PngSig[sigByte], png[sigByte]);
        // IHDR width/height (big-endian at bytes 16..23).
        Assert.Equal(w, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(h, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        // IDAT is zlib-compressed and decompresses back to the raw scanlines
        // (filter byte 0 + RGBA row per scanline).
        int rawLen = h * (w * 4 + 1);
        byte[] idat = ExtractChunk(png, "IDAT");
        byte[] raw;
        using (var cms = new MemoryStream(idat))
        using (var zs = new System.IO.Compression.ZLibStream(cms, System.IO.Compression.CompressionMode.Decompress))
        using (var oms = new MemoryStream())
        {
            zs.CopyTo(oms);
            raw = oms.ToArray();
        }
        Assert.Equal(rawLen, raw.Length);
        Assert.Equal(0, raw[0]);                      // filter byte = None
        Assert.Equal(10, raw[1]);                    // first pixel R
    }

    [Fact]
    public void ThumbnailRenderer_Renders_Outlines_And_Hatches()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });

        var png = Cli.Output.ThumbnailRenderer.Render(doc, groups, showOutlines: true, 0.01);
        Assert.NotNull(png);
        for (int sigByte = 0; sigByte < PngSig.Length; sigByte++) Assert.Equal(PngSig[sigByte], png[sigByte]);
        int iw = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int ih = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        Assert.InRange(iw, 1, 400);
        Assert.InRange(ih, 1, 400);

        // The rect fills the bbox so the rendered image is near the max size.
        Assert.True(iw >= 300 || ih >= 300);
    }

    [Fact]
    public void ThumbnailRenderer_Returns_Null_For_Empty_Document()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\"/>");
        Assert.Null(Cli.Output.ThumbnailRenderer.Render(doc, new List<HatchGroup>(), true, 0.01));
    }

    [Fact]
    public void BuildBlock_Is_Parsable_By_Moonraker_Regex_And_Decodes_To_Png()
    {
        // Mirrors Moonraker's metadata.py parse_thumbnails: it finds
        //   ; thumbnail begin <W> <H> <base64len>
        //   ; <base64...>
        //   ; thumbnail end
        // validates len(data) == base64len, then base64-decodes to the PNG.
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\">" +
                      "<rect x=\"5\" y=\"5\" width=\"60\" height=\"60\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 6, HatchAngle = 45, Inset = false, ConnectEnds = false });
        var png = Cli.Output.ThumbnailRenderer.Render(doc, groups, true, 0.01)!;
        var block = Cli.Output.ThumbnailRenderer.BuildBlock(png);

        var m = System.Text.RegularExpressions.Regex.Match(block,
            @"(thumbnail(?:_[A-Za-z0-9]+)?) begin([;/\+=\w\s]+?); \1 end");
        Assert.True(m.Success, "block does not match Moonraker's thumbnail regex");

        var lines = m.Groups[2].Value.Replace("; ", "").Split(new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        var ints = System.Text.RegularExpressions.Regex.Matches(lines[0], @"\d+")
            .Select(x => int.Parse(x.Value)).ToArray();
        Assert.Equal(3, ints.Length);                 // W, H, base64 length
        var data = string.Join("", lines.Skip(1));
        Assert.Equal(ints[2], data.Length);            // declared size == actual base64 length

        var decoded = Convert.FromBase64String(data);
        for (int sigByte = 0; sigByte < PngSig.Length; sigByte++) Assert.Equal(PngSig[sigByte], decoded[sigByte]);
        Assert.Equal(ints[0], (decoded[16] << 24) | (decoded[17] << 16) | (decoded[18] << 8) | decoded[19]);
        Assert.Equal(ints[1], (decoded[20] << 24) | (decoded[21] << 16) | (decoded[22] << 8) | decoded[23]);
    }

    [Fact]
    public void BuildBlock_Prepended_To_Gcode_Keeps_Gcode_Intact()
    {
        var gcode = "G90;\nG1 F1000 X0 Y0;\nM3 S255;\n";
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0,0,0,13, 0x49,0x48,0x44,0x52, 0,0,0,4,0,0,0,3,8,6,0,0,0 };
        var block = Cli.Output.ThumbnailRenderer.BuildBlock(png);
        var combined = block + gcode;
        Assert.StartsWith("; thumbnail begin 4 3 ", block);
        Assert.EndsWith("; thumbnail end\n", block);
        Assert.EndsWith(gcode, combined);
    }

    private static byte[] ExtractChunk(byte[] png, string type)
    {
        int o = 8;
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        while (o < png.Length)
        {
            int len = (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
            o += 4;
            bool match = png[o] == t[0] && png[o + 1] == t[1] && png[o + 2] == t[2] && png[o + 3] == t[3];
            o += 4;
            if (match) { var d = new byte[len]; Buffer.BlockCopy(png, o, d, 0, len); return d; }
            o += len + 4;
        }
        throw new Exception("chunk " + type + " not found");
    }
}
