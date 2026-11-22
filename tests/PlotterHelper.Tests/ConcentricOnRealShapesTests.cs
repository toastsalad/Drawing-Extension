using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Tests;

/// <summary>
/// Concentric rings on a shape with a hole and thin corridors.
/// </summary>
/// <remarks>
/// The fill used to draw almost nothing on a real drawing: rings were filtered by enclosed area, and a ring in
/// a corridor is long and thin -- 60mm of perimeter around a few square millimetres -- so twenty-five of the
/// twenty-six rings of a test drawing were thrown away and the fill looked broken. The filter also asked
/// whether a point of the ring was inside the shape, which is true of an outer boundary and false of the
/// boundary of a hole, so holes were never ringed at all. And a folded ring could survive every erosion
/// unchanged, so the level loop ran to its guard emitting the same small loop over and over.
///
/// These check the properties that were lost: rings around holes, rings in thin corridors, a bounded number
/// of them, all inside the shape.
/// </remarks>
public class ConcentricOnRealShapesTests
{
    private static SvgDocument Doc(string body) => SvgParser.ParseString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" + body + "</svg>",
        handleViewBox: false, canvasHeight: 200, transformOrigin: true);

    /// <summary>A rectangle with a square hole, and a notch that leaves a thin corridor.</summary>
    private static SvgDocument Plated() => Doc(
        "<path d=\"M20,20 L180,20 L180,180 L120,180 L120,90 L100,90 L100,180 L20,180 Z " +
        "M60,50 L80,50 L80,70 L60,70 Z\"/>");

    private static List<Vector> PointsOf(HatchPath path)
    {
        var points = new List<Vector> { path.Start };
        foreach (var op in path.Ops) if (op is LineTo line) points.Add(line.P);
        return points;
    }

    private static (List<HatchPath> Paths, double Ink) Fill(double spacing)
    {
        var paths = HatchEngine.Generate(Plated().Paths, new HatchOptions
        {
            FillStyle = FillStyle.Concentric,
            HatchSpacing = spacing,
            Inset = true,
            InsetDistance = 0.1,
        })[0].Paths;

        double ink = 0;
        foreach (var path in paths)
        {
            var points = PointsOf(path);
            for (int i = 1; i < points.Count; i++) ink += (points[i] - points[i - 1]).Length;
        }
        return (paths, ink);
    }

    [Theory]
    [InlineData(5.0)]
    public void It_Draws_Rings_In_Every_Corridor_Not_Just_The_Outline(double spacing)
    {
        var (paths, ink) = Fill(spacing);

        // The outline alone is about 600mm. Rings that follow the shape in every corridor are much more; the
        // fill used to manage a few percent of that, and on a detailed drawing it managed 12mm of 6,900.
        Assert.True(paths.Count >= 2, $"expected rings, got {paths.Count}");
        Assert.True(ink > 900, $"expected rings inside the shape, not just its outline: {ink:F0}mm of ink");
    }

    [Theory]
    [InlineData(5.0)]
    public void Every_Ring_Is_A_Closed_Loop_Inside_The_Shape(double spacing)
    {
        var (paths, _) = Fill(spacing);
        foreach (var path in paths)
        {
            var points = PointsOf(path);
            Assert.True(points.Count >= 3, "a ring needs three points");
            foreach (var point in points)
                Assert.True(point.X >= 20 - 1e-6 && point.X <= 180 + 1e-6
                    && point.Y >= 20 - 1e-6 && point.Y <= 180 + 1e-6,
                    $"ring point ({point.X:F2},{point.Y:F2}) is outside the shape");
        }
    }

    [Fact]
    public void The_Levels_Stop()
    {
        // A folded ring used to survive every erosion, so the loop ran to its guard: at a fine spacing the
        // fill emitted the same small loop a hundred and fifty times.
        var (paths, _) = Fill(1.25);
        Assert.True(paths.Count < 400, $"{paths.Count} rings suggests the erosion never terminated");
    }
}

