using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Tests;

/// <summary>
/// Peano and engraving. Peano is checked as a space-filling curve rather than by looking at the
/// output: every cell of the grid must be visited exactly once and consecutive points must be one
/// cell apart, which is what makes it a curve rather than a scatter. Engraving is checked for the
/// tonal behaviour it exists for: denser ink near the boundary than in the middle.
/// </summary>
public class SpaceFillingAndEngravingTests
{
    private static SvgDocument Doc(string body) => SvgParser.ParseString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" + body + "</svg>",
        handleViewBox: false, canvasHeight: 100, transformOrigin: true);

    private static IEnumerable<Vector> AllPoints(HatchGroup group)
    {
        foreach (var path in group.Paths)
            foreach (var point in Points(path))
                yield return point;
    }
    private static IEnumerable<Vector> Points(HatchPath path)
    {
        yield return path.Start;
        foreach (var op in path.Ops) if (op is LineTo line) yield return line.P;
    }

    [Fact]
    public void Peano_Visits_Every_Cell_Once_And_Steps_One_Cell_At_A_Time()
    {
        // The curve itself, without any clipping, over a square that contains all of it.
        var doc = Doc("<path d=\"M0,0 L90,0 L90,90 L0,90 Z\"/>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Peano,
            HatchSpacing = 4,
            Inset = false,
            ConnectEnds = false,
        });

        var points = AllPoints(groups[0]).ToList();
        Assert.True(points.Count > 100, $"expected a long curve, got {points.Count} points");

        // Spacing 4 over a 90-unit span: the smallest power of three giving cells <= 4 wide is 27
        // cells (90/27 = 3.3), so the grid is 27 x 27 and the curve visits 729 points.
        int side = 27;
        Assert.Equal(side * side, points.Count);

        var distinct = new HashSet<(long, long)>();
        foreach (var point in points)
            Assert.True(distinct.Add((Round(point.X, 6), Round(point.Y, 6))), "the curve visited a point twice");

        double cell = 90.0 / side;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            double dx = Math.Abs(points[i + 1].X - points[i].X);
            double dy = Math.Abs(points[i + 1].Y - points[i].Y);
            bool stepX = Math.Abs(dx - cell) < cell * 0.01;
            bool stepY = Math.Abs(dy - cell) < cell * 0.01;
            bool still = dx < cell * 0.01 && dy < cell * 0.01;
            Assert.True((stepX ^ stepY) && !still,
                $"step {i} is not one cell: dx={dx:0.###} dy={dy:0.###} cell={cell:0.###}");
        }
    }

    [Fact]
    public void Peano_And_Hilbert_Are_Different_Curves()
    {
        var doc = Doc("<path d=\"M0,0 L90,0 L90,90 L0,90 Z\"/>");
        var options = new HatchOptions
        {
            HatchSpacing = 4,
            Inset = false,
            ConnectEnds = false,
        };

        options.FillStyle = FillStyle.Peano;
        var peano = string.Join(";", AllPoints(HatchEngine.Generate(doc.Paths, options)[0])
            .Take(40).Select(p => $"{p.X:0.0},{p.Y:0.0}"));

        options.FillStyle = FillStyle.Hilbert;
        var hilbert = string.Join(";", AllPoints(HatchEngine.Generate(doc.Paths, options)[0])
            .Take(40).Select(p => $"{p.X:0.0},{p.Y:0.0}"));

        Assert.NotEqual(peano, hilbert);
    }

    [Fact]
    public void Peano_Stays_Inside_The_Outline()
    {
        var doc = Doc("<path d=\"M10,10 L90,10 L90,90 L10,90 Z\"/>");
        var outline = HatchEngine.BuildPolygons(doc.Paths[0], 0.02, autoClose: false)[0];

        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Peano,
            HatchSpacing = 4,
            Inset = true,
            InsetDistance = 1,
            ConnectEnds = false,
        });

        int outside = 0;
        foreach (var point in AllPoints(groups[0]))
            if (!InsideEvenOdd(outline, point)) outside++;
        Assert.True(outside == 0, $"{outside} Peano points outside the outline");
    }

    [Fact]
    public void Engraving_Is_Denser_Near_The_Boundary_Than_In_The_Middle()
    {
        var doc = Doc("<path d=\"M10,10 L90,10 L90,90 L10,90 Z\"/>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Engrave,
            HatchSpacing = 3,
            HatchAngle = 0,
            Inset = false,
            ConnectEnds = false,
        });

        // Sample along the drawn segments, not just at their ends: a horizontal hatch line starts and
        // ends on the boundary, so counting only its endpoints would say the ink is all at the edge.
        int inner = 0, outer = 0;
        foreach (var path in groups[0].Paths)
        {
            var points = Points(path).ToList();
            for (int i = 0; i + 1 < points.Count; i++)
            {
                for (int step = 1; step < 16; step++)
                {
                    double t = step / 16.0;
                    var point = new Vector(
                        points[i].X + (points[i + 1].X - points[i].X) * t,
                        points[i].Y + (points[i + 1].Y - points[i].Y) * t);
                    double edgeDistance = Math.Min(
                        Math.Min(point.X - 10, 90 - point.X),
                        Math.Min(point.Y - 10, 90 - point.Y));
                    if (edgeDistance < 0) continue;
                    if (edgeDistance < 10) outer++;
                    else if (edgeDistance > 30) inner++;
                }
            }
        }

        Assert.True(outer > 0 && inner > 0, $"expected ink in both bands: inner={inner} outer={outer}");
        Assert.True(outer > inner * 1.5,
            $"engraving should concentrate ink near the boundary: inner={inner} outer={outer}");
    }

    [Fact]
    public void Engraving_Draws_One_Family_Everywhere_And_More_Near_The_Edge()
    {
        // Layer 0 has threshold 0, so it must cover the whole shape; the extra families only appear
        // nearer the boundary, which is what makes the tone ramp instead of being flat.
        var doc = Doc("<path d=\"M10,10 L90,10 L90,90 L10,90 Z\"/>");
        var options = new HatchOptions
        {
            HatchSpacing = 3,
            HatchAngle = 0,
            Inset = false,
            ConnectEnds = false,
        };

        options.FillStyle = FillStyle.Parallel;
        var flat = HatchEngine.Generate(doc.Paths, options)[0].Paths.Count;

        options.FillStyle = FillStyle.Engrave;
        var layered = HatchEngine.Generate(doc.Paths, options)[0].Paths.Count;

        Assert.True(layered > flat, $"engraving should add layers: {layered} vs parallel {flat}");
    }

    private static long Round(double value, int places) => (long)Math.Round(value * Math.Pow(10, places));

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
}
