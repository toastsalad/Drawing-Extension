using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class TilingTests
{
    private static SvgDocument Doc(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false, canvasHeight: 120, transformOrigin: true);

    private static Cli.Options TileOpts(string gcode, double tw, double th,
        double overlap = 0, bool marks = false, double markSize = 1)
    {
        var o = new Cli.Options
        {
            Mode = Cli.Mode.Cut,
            Gcode = gcode,
            Unit = "mm",
            TravelSpeed = 1000,
            CuttingSpeed = 300,
            Passes = 1,
            PassDepth = 0,
            TileWidth = tw,
            TileHeight = th,
            TileOverlap = overlap,
            TileAlignmentMarks = marks,
            TileMarkSize = markSize,
        };
        return o;
    }

    [Fact]
    public void Tiling_Writes_One_File_Per_Tile_And_Partitions_Content()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plotter-helper-tile-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // Inset rect so its edges (at 10/110) are interior to the 60x60 tiles
            // (seams at 0/60/120), not degenerate on-boundary strokes.
            var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"120\">" +
                          "<rect x=\"10\" y=\"10\" width=\"100\" height=\"100\" stroke=\"black\" fill=\"none\"/></svg>");
            var flat = Cli.Features.Tiling.FlattenOutlines(doc, 0.01);
            int n = Cli.Features.Tiling.Run(TileOpts(Path.Combine(dir, "out.gcode"), 60, 60), flat);
            Assert.Equal(4, n);                       // 120/60 = 2x2 tiles
            var files = Directory.GetFiles(dir, "out_tile_*.gcode");
            Assert.Equal(4, files.Length);

            // Every tile keeps its content within the drawing bbox [0,120], and the
            // union of all tiles reconstructs the rect edges (x spans ~[10,110]).
            double globalMin = double.PositiveInfinity, globalMax = double.NegativeInfinity;
            int nonEmpty = 0;
            foreach (var f in files)
            {
                var xs = new List<double>();
                foreach (var ln in File.ReadAllLines(f))
                {
                    var mx = System.Text.RegularExpressions.Regex.Match(ln, @"X(-?\d+\.?\d*)");
                    if (mx.Success) xs.Add(double.Parse(mx.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
                if (xs.Count == 0) continue;
                nonEmpty++;
                Assert.InRange(xs.Min(), 0, 120.5);
                Assert.InRange(xs.Max(), -0.5, 120);
                globalMin = Math.Min(globalMin, xs.Min());
                globalMax = Math.Max(globalMax, xs.Max());
            }
            Assert.Equal(4, nonEmpty);                // each tile gets an edge
            Assert.InRange(globalMin, 9.5, 10.5);      // left edge ~10
            Assert.InRange(globalMax, 109.5, 110.5);   // right edge ~110
        }
        finally { try { Directory.Delete(dir, true); } catch { /* ignore: best-effort temp-dir cleanup */ } }
    }

    [Fact]
    public void Tiling_Disabled_When_Tile_Size_Zero()
    {
        var o = TileOpts("/tmp/x.gcode", 0, 0);
        Assert.False(Cli.Features.Tiling.Enabled(o));
        o.TileWidth = 60; o.TileHeight = 0;
        Assert.False(Cli.Features.Tiling.Enabled(o));
        o.TileWidth = 60; o.TileHeight = 60; o.Gcode = null;
        Assert.False(Cli.Features.Tiling.Enabled(o));
    }

    [Fact]
    public void Tiling_Overlap_Adds_Extra_Tiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plotter-helper-tile-overlap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"120\">" +
                          "<rect x=\"0\" y=\"0\" width=\"120\" height=\"120\" stroke=\"black\" fill=\"none\"/></svg>");
            var flat = Cli.Features.Tiling.FlattenOutlines(doc, 0.01);
            int none = Cli.Features.Tiling.Run(TileOpts(Path.Combine(dir, "a.gcode"), 60, 60, overlap: 0), flat);
            int withOverlap = Cli.Features.Tiling.Run(TileOpts(Path.Combine(dir, "b.gcode"), 60, 60, overlap: 10), flat);
            // overlap=10 -> step=50 -> ceil(120/50)=3 cols/rows -> 9 tiles (vs 4 with no overlap).
            Assert.Equal(4, none);
            Assert.Equal(9, withOverlap);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* ignore: best-effort temp-dir cleanup */ } }
    }
}
