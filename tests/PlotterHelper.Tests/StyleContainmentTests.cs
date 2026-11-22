using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Tests;

/// <summary>
/// Every fill style, on a shape whose outline folds back on itself.
/// </summary>
/// <remarks>
/// The other containment tests use a square, where the outline is convex and every clip bug that matters --
/// a chord crossing a reflex corner, an interval that starts outside and ends inside -- is invisible. A star
/// has ten edges, five of them reflex, and no two parallel, so a hole in the clipper shows up as ink in a
/// notch.
///
/// The point test here is written out rather than borrowed from <see cref="EvenOddRegion"/>: checking the
/// engine's output with the engine's own geometry would pass whatever the region code believed.
/// </remarks>
public class StyleContainmentTests
{
    /// <summary>The star's outline, clockwise in the SVG's y-down frame.</summary>
    private static readonly Vector[] Star =
    {
        new(0.0, -46.0), new(10.6, -14.6), new(43.8, -14.2), new(17.1, 5.6), new(27.0, 37.2),
        new(0.0, 18.0), new(-27.0, 37.2), new(-17.1, 5.6), new(-43.8, -14.2), new(-10.6, -14.6),
    };

    /// <summary>A point on an edge is inside the shape; only ink past this counts as outside.</summary>
    private const double Tolerance = 0.02;

    public static IEnumerable<object[]> EveryStyle() =>
        Enum.GetValues<FillStyle>().Select(style => new object[] { style });

    /// <summary>Even-odd crossing test, independent of the engine's own point-in-region. */
    private static bool Inside(IReadOnlyList<Vector> polygon, Vector p)
    {
        bool hit = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector a = polygon[i], b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                hit = !hit;
        }
        return hit;
    }

    private static double DistanceToOutline(IReadOnlyList<Vector> polygon, Vector p)
    {
        double best = double.MaxValue;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector a = polygon[i], b = polygon[j];
            Vector span = b - a;
            double lengthSquared = span.X * span.X + span.Y * span.Y;
            double t = lengthSquared <= 0
                ? 0
                : Math.Clamp(((p.X - a.X) * span.X + (p.Y - a.Y) * span.Y) / lengthSquared, 0, 1);
            double dx = p.X - (a.X + t * span.X);
            double dy = p.Y - (a.Y + t * span.Y);
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    private static List<Vector> Ink(FillStyle style)
    {
        var doc = SvgParser.ParseString(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"95.5\" height=\"91.2\">" +
            "<path d=\"M0.0,-46.0 L10.6,-14.6 L43.8,-14.2 L17.1,5.6 L27.0,37.2 " +
            "L0.0,18.0 L-27.0,37.2 L-17.1,5.6 L-43.8,-14.2 L-10.6,-14.6 Z\"/></svg>",
            handleViewBox: false, canvasHeight: 91.2, transformOrigin: false);

        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = style,
            HatchSpacing = 4,
            HatchAngle = 30,
            Inset = false,
            ConnectEnds = false,
        });

        var points = new List<Vector>();
        foreach (var group in groups)
        foreach (var path in group.Paths)
        {
            points.Add(path.Start);
            foreach (var op in path.Ops) if (op is LineTo line) points.Add(line.P);
        }
        return points;
    }

    [Theory]
    [MemberData(nameof(EveryStyle))]
    public void Every_Style_Stays_Inside_A_Concave_Shape(FillStyle style)
    {
        var points = Ink(style);
        Assert.True(points.Count > 0, $"{style}: drew nothing");

        // The engine may work in either y direction depending on the frame it was handed, and this test is
        // about the shape rather than the convention: use whichever mirror contains the ink's own centre.
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        var centre = new Vector((minX + maxX) / 2, (minY + maxY) / 2);
        var mirrored = Star.Select(p => new Vector(p.X, -p.Y)).ToArray();
        var outline = Inside(Star, centre) || !Inside(mirrored, centre) ? Star : mirrored;

        var escapes = points
            .Where(p => !Inside(outline, p))
            .Select(p => DistanceToOutline(outline, p))
            .Where(distance => distance > Tolerance)
            .ToList();

        Assert.True(escapes.Count == 0,
            $"{style}: {escapes.Count} of {points.Count} points outside the star, worst {escapes.DefaultIfEmpty(0).Max():F3}");
    }
}
