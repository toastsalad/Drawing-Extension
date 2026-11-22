using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;

namespace PlotterHelper.Tests;

/// <summary>
/// Parallel hatch on a real, dense, self-overlapping drawing: 1766 closed facets, the hardest shape in the tree.
/// </summary>
/// <remarks>
/// The outline of this drawing is where hatch containment breaks if it breaks at all, and these two tests are the
/// only ones that run it. They were skipped for two years: the drawing was named by an absolute path on one
/// developer's desktop and the test returned early when the file was absent, so on every other machine they
/// asserted true and passed. Turning them back on found a real defect immediately -- the inset pass was emitting
/// loops that reached 19 units outside the drawing, into empty space, and the fill drew there.
///
/// They measure against <see cref="EvenOddRegion"/> built from the same polygons the engine clips to, rather
/// than through the old reference helper, which was written against a different clipper and reported every point
/// as outside once that clipper was replaced.
/// </remarks>
public class OctopusTests
{
    /// <summary>
    /// How far outside the outline a pen point may land, in user units, for hatch lines only.
    /// </summary>
    /// <remarks>
    /// Tied to the hatch spacing rather than picked out of the air: the fill clips against an inset region whose
    /// boundary is rounded to 1e-6 and built with a 0.25 arc tolerance, and this drawing's facets are small
    /// enough that those two effects add up near the outline. Measured worst case 0.8 at a 3 unit spacing.
    /// Before the inset fix a point could land 19.4 units out, in empty space with no outline near it.
    /// </remarks>
    private const double LineAllowance = 1.0;

    /// <summary>
    /// The same allowance for a fill whose ends are joined, which is wider because the joins are Bezier curves
    /// and bulge: that is the ported algorithm's own bezier overshoot behaviour, not an error in the clip.
    /// Measured worst case 1.6.
    /// </summary>
    private const double JoinAllowance = 2.0;

    private static (int Points, int Outside, double Worst) HatchOvershoot(bool connectEnds, double allowance)
    {
        var doc = SvgParser.ParseFile(TestAssets.Drawing("octopus.svg"), handleViewBox: false,
            canvasHeight: null, transformOrigin: true);

        int points = 0, outside = 0;
        double worst = 0;

        foreach (var source in doc.Paths)
        {
            var polygons = HatchEngine.BuildPolygons(source, 0.02);
            if (polygons.Count == 0) continue;
            var region = new EvenOddRegion(polygons.Select(p => p.ToArray()).ToList(), true);

            var groups = HatchEngine.Generate(new[] { source }, new HatchOptions
            {
                FillStyle = FillStyle.Parallel,
                HatchSpacing = 3,
                HatchAngle = 45,
                Inset = true,
                InsetDistance = 0.5,
                ConnectEnds = connectEnds,
                HatchScope = 3,
                Tolerance = 2.0,
            });
            if (groups.Count == 0) continue;

            foreach (var path in groups[0].Paths)
                foreach (var point in PointOf(path))
                {
                    points++;
                    if (region.ContainsWithin(point, allowance)) continue;
                    outside++;
                    worst = Math.Max(worst, DistanceOutside(region, point));
                }
        }

        return (points, outside, worst);
    }

    private static List<Vector> PointOf(HatchPath path)
    {
        var points = new List<Vector> { path.Start };
        foreach (var op in path.Ops) if (op is LineTo line) points.Add(line.P);
        return points;
    }

    /// <summary>How far outside the region a point is, by stepping until the region contains it.</summary>
    private static double DistanceOutside(EvenOddRegion region, Vector point)
    {
        for (double step = 0.05; step <= 8.0; step *= 2)
            if (region.ContainsWithin(point, step)) return step;
        return double.PositiveInfinity;
    }

    [Fact]
    public void Octopus_Parallel_Hatch_Stays_Inside_The_Outline()
    {
        var (points, outside, worst) = HatchOvershoot(connectEnds: false, LineAllowance);
        Assert.True(points > 500, $"expected a filled drawing, got {points} pen points");
        Assert.True(outside == 0,
            $"{outside} of {points} hatch points outside the outline by more than {LineAllowance}; worst {worst}");
    }

    [Fact]
    public void Octopus_Parallel_With_Connect_Ends_Stays_Inside_The_Outline()
    {
        var (points, outside, worst) = HatchOvershoot(connectEnds: true, JoinAllowance);
        Assert.True(points > 500, $"expected a filled drawing, got {points} pen points");
        Assert.True(outside == 0,
            $"{outside} of {points} connected hatch points outside the outline by more than {JoinAllowance}; " +
            $"worst {worst}");
    }
}
