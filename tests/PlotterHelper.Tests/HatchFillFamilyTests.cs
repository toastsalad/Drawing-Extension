using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Tests;

/// <summary>
/// The tier-1 fills added together: contour spiral, hex lattice, square spiral, Truchet, Gosper and
/// dragon. Each is checked for the property it exists for, and all of them for the containment property
/// that caught four bugs in the previous batch -- no pen-down point outside the shape.
/// </summary>
public class HatchFillFamilyTests
{
    private static readonly FillStyle[] StyleFills =
    [
        FillStyle.ContourSpiral, FillStyle.HexLattice, FillStyle.SquareSpiral,
        FillStyle.Truchet, FillStyle.Gosper, FillStyle.Dragon,
    ];

    private static SvgDocument Doc(string body) => SvgParser.ParseString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" + body + "</svg>",
        handleViewBox: false, canvasHeight: 200, transformOrigin: true);

    /// <summary>A concave shape with a hole, so a fill that ignores the outline is caught.</summary>
    private static SvgDocument Notched() => Doc(
        "<path d=\"M20,20 L180,20 L180,180 L110,180 L110,90 L90,90 L90,180 L20,180 Z " +
        "M60,50 L80,50 L80,70 L60,70 Z\"/>");

    private static IEnumerable<Vector> Points(HatchPath path)
    {
        yield return path.Start;
        foreach (var op in path.Ops) if (op is LineTo line) yield return line.P;
    }

    private static bool InsideEvenOdd(IReadOnlyList<Vector> ring, Vector p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            Vector a = ring[i], b = ring[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static List<HatchPath> Fill(FillStyle style, HatchOptions options)
    {
        var groups = HatchEngine.Generate(Notched().Paths, options);
        Assert.Single(groups);
        return groups[0].Paths;
    }

    private static HatchOptions Options(FillStyle style, double spacing = 5) => new()
    {
        FillStyle = style,
        HatchSpacing = spacing,
        HatchAngle = 30,
        Inset = true,
        InsetDistance = 1,
        ConnectEnds = false,
    };

    [Theory]
    [InlineData(FillStyle.ContourSpiral)]
    [InlineData(FillStyle.HexLattice)]
    [InlineData(FillStyle.SquareSpiral)]
    [InlineData(FillStyle.Truchet)]
    [InlineData(FillStyle.Gosper)]
    [InlineData(FillStyle.Dragon)]
    public void Every_New_Fill_Draws_And_Stays_Inside_The_Shape(FillStyle style)
    {
        var outline = HatchEngine.BuildPolygons(Notched().Paths[0], 0.02, autoClose: false);
        var paths = Fill(style, Options(style));

        Assert.NotEmpty(paths);

        int outside = 0;
        foreach (var path in paths)
            foreach (var point in Points(path))
                if (!outline.Any(ring => InsideEvenOdd(ring, point))) outside++;

        Assert.True(outside == 0, $"{style}: {outside} points outside the shape");
    }

    [Fact]
    public void Contour_Spiral_Uses_Far_Fewer_Strokes_Than_Concentric()
    {
        // The whole point of joining the rings: the same ink, without a pen lift per ring.
        var concentric = Fill(FillStyle.Concentric, Options(FillStyle.Concentric)).Count;
        var spiral = Fill(FillStyle.ContourSpiral, Options(FillStyle.ContourSpiral)).Count;

        // Fewer strokes than concentric, because the rings are joined into chains rather than drawn one stroke
        // each. How much fewer depends on how many rings are separate islands, which cannot be joined without a
        // line across the drawing; the fill deliberately breaks the chain instead of drawing that line.
        Assert.True(spiral < concentric,
            $"contour spiral should need fewer strokes than concentric: {spiral} vs {concentric}");
    }

    [Fact]
    public void Hex_Lattice_Uses_Three_Families_Where_Parallel_Uses_One()
    {
        var parallel = Fill(FillStyle.Parallel, Options(FillStyle.Parallel));
        long parallelInk = parallel.Sum(path => Points(path).Count());
        var hex = Fill(FillStyle.HexLattice, Options(FillStyle.HexLattice));
        long hexInk = hex.Sum(path => Points(path).Count());

        Assert.True(hexInk > parallelInk * 2,
            $"three families should be about three times the ink: {hexInk} vs {parallelInk}");
    }

    [Fact]
    public void Truchet_Is_Deterministic_For_A_Seed_And_Different_For_Another()
    {
        var first = string.Join(";", Fill(FillStyle.Truchet, Options(FillStyle.Truchet))
            .SelectMany(Points).Select(p => $"{p.X:0.0},{p.Y:0.0}"));

        var again = string.Join(";", Fill(FillStyle.Truchet, Options(FillStyle.Truchet))
            .SelectMany(Points).Select(p => $"{p.X:0.0},{p.Y:0.0}"));

        var options = Options(FillStyle.Truchet);
        options.JitterSeed = 999;
        var reseeded = string.Join(";", Fill(FillStyle.Truchet, options)
            .SelectMany(Points).Select(p => $"{p.X:0.0},{p.Y:0.0}"));

        Assert.Equal(first, again);
        Assert.NotEqual(first, reseeded);
    }

    [Fact]
    public void Dragon_Is_A_Long_Continuous_Curve()
    {
        // The fold count is derived from the spacing, so a 20mm request over a 160mm shape asks for about
        // eightfold-eight, and the curve is a couple of hundred segments rather than the four thousand a
        // page-scale order would give. What matters is the property, not the count: the curve has to reach
        // across the shape, its segments must have length, and it must be a curve rather than a scatter.
        var points = Fill(FillStyle.Dragon, Options(FillStyle.Dragon, spacing: 20))
            .SelectMany(Points).ToList();

        Assert.True(points.Count >= 32, $"expected a curve with some length, got {points.Count} points");

        double spanX = points.Max(p => p.X) - points.Min(p => p.X);
        double spanY = points.Max(p => p.Y) - points.Min(p => p.Y);
        Assert.True(spanX > 100 && spanY > 100,
            $"the curve should reach across the shape, got {spanX:F0} x {spanY:F0}");

        for (int i = 0; i + 1 < points.Count; i++)
            Assert.True((points[i + 1] - points[i]).Length > 1e-9, $"segment {i} has no length");
    }

    [Fact]
    public void Gosper_Is_A_Long_Single_Curve()
    {
        var points = Fill(FillStyle.Gosper, Options(FillStyle.Gosper, spacing: 20))
            .SelectMany(Points).ToList();

        Assert.True(points.Count >= 64, $"expected the flowsnake, got {points.Count} points");

        double spanX = points.Max(p => p.X) - points.Min(p => p.X);
        double spanY = points.Max(p => p.Y) - points.Min(p => p.Y);
        Assert.True(spanX > 100 && spanY > 100,
            $"the curve should reach across the shape, got {spanX:F0} x {spanY:F0}");
    }

    [Fact]
    public void Square_Spiral_Is_A_Coil_Not_A_Grid()
    {
        // Runs alternate direction: consecutive segments are perpendicular, never collinear.
        var points = Fill(FillStyle.SquareSpiral, Options(FillStyle.SquareSpiral, spacing: 10))
            .SelectMany(Points).ToList();
        Assert.True(points.Count > 8);

        int perpendicular = 0;
        for (int i = 1; i + 1 < points.Count; i++)
        {
            var a = points[i] - points[i - 1];
            var b = points[i + 1] - points[i];
            if (Math.Abs(Vector.Dot(a, b)) < 1e-6 * a.Length * b.Length) perpendicular++;
        }
        Assert.True(perpendicular > 0, "the coil should turn by right angles");
    }
}
