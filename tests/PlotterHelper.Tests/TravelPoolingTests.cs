using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class TravelPoolingTests
{
    // Two same-color (red) squares far apart horizontally. With --group-by-color +
    // --optimize-travel the outlines AND hatches of both squares must be pooled into
    // one nearest-neighbor set per color, so all of the left square is drawn before
    // any of the right square (one left<->right crossing) -- not outlines-first then
    // per-element hatches (left->right->left->right, three crossings).
    private const string TwoSameColorSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='20'>" +
        "<rect x='0' y='0' width='10' height='10' stroke='red' fill='red'/>" +
        "<rect x='100' y='0' width='10' height='10' stroke='red' fill='red'/>" +
        "</svg>";

    private static SvgDocument Doc(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false, canvasHeight: 20, transformOrigin: true);

    private static Cli.Options Opts(bool optimize) => new()
    {
        Mode = Cli.Mode.Hatch,
        Unit = "mm",
        TravelSpeed = 1000,
        CuttingSpeed = 300,
        Passes = 1,
        PassDepth = 0,
        GroupByColor = true,
        IncludeOutlines = true,
        OptimizeTravel = optimize,
        OptimizeTravelReverse = true,
    };

    [Fact]
    public void Pools_Outlines_And_Hatches_Across_Elements_Of_One_Color()
    {
        var doc = Doc(TwoSameColorSvg);
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 2, HatchAngle = 0, Inset = false, ConnectEnds = false });
        Assert.Equal(2, groups.Count);

        var off = Opts(false);
        var cOff = Cli.EngineBuilder.BuildCompiler(off);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(off, cOff, doc.Paths, groups, 0, 100, "test");
        var (travelOff, crossOff) = MeasureTravel(cOff.Compile(1), 1000, 50);

        var on = Opts(true);
        var cOn = Cli.EngineBuilder.BuildCompiler(on);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(on, cOn, doc.Paths, groups, 0, 100, "test");
        var (travelOn, crossOn) = MeasureTravel(cOn.Compile(1), 1000, 50);

        Assert.True(travelOn < travelOff / 2, $"pooled travel {travelOn:0.0} not much less than unpooled {travelOff:0.0}");
        Assert.True(crossOn <= 1, $"pooled left<->right crossings {crossOn} should be <= 1");
        Assert.True(crossOff >= 3, $"unpooled left<->right crossings {crossOff} should be >= 3");
    }

    private static (double travel, int crossings) MeasureTravel(string gcode, double travelSpeed, double boundaryX)
    {
        double x = 0, y = 0, tot = 0; int cross = 0; bool? prevRight = null;
        foreach (var raw in gcode.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("G1")) continue;
            double? f = null, xn = null, yn = null;
            foreach (var tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.StartsWith('F') && double.TryParse(tok[1..], out var fv)) f = fv;
                else if (tok.StartsWith('X') && double.TryParse(tok[1..].TrimEnd(';'), out var xv)) xn = xv;
                else if (tok.StartsWith('Y') && double.TryParse(tok[1..].TrimEnd(';'), out var yv)) yn = yv;
            }
            if (xn.HasValue && yn.HasValue)
            {
                if (f.HasValue && Math.Abs(f.Value - travelSpeed) < 1e-3)
                {
                    tot += Math.Sqrt((xn.Value - x) * (xn.Value - x) + (yn.Value - y) * (yn.Value - y));
                    bool right = xn.Value > boundaryX;
                    if (prevRight.HasValue && right != prevRight.Value) cross++;
                    prevRight = right;
                }
                x = xn.Value; y = yn.Value;
            }
        }
        return (tot, cross);
    }
}
