using PlotterHelper.Geometry;
using PlotterHelper.Trace;
using Xunit;

namespace PlotterHelper.Tests;

public class MarchingSquaresTests
{
    private static bool[] Block(int width, int height, int left, int top, int right, int bottom)
    {
        var mask = new bool[width * height];
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                mask[y * width + x] = true;
        return mask;
    }

    [Fact]
    public void SolidSquare_TracesOneClosedLoop()
    {
        var mask = Block(3, 3, 0, 0, 2, 2);
        var loops = MarchingSquares.Trace(mask, 3, 3, 0, 0, 3, 3);

        Assert.Single(loops);
        var loop = loops[0];
        Assert.Equal(loop[0], loop[^1]);
        // Perimeter 12, sampled at half-pixel steps.
        Assert.Equal(13, loop.Count);
    }

    [Fact]
    public void SquareWithHole_TracesOuterAndInnerLoops()
    {
        var mask = Block(5, 5, 1, 1, 3, 3);
        mask[2 * 5 + 2] = false; // punch a hole
        var loops = MarchingSquares.Trace(mask, 5, 5, 0, 0, 5, 5);

        Assert.Equal(2, loops.Count);
        foreach (var loop in loops) Assert.Equal(loop[0], loop[^1]);
    }

    [Fact]
    public void SinglePixel_IsAClosedFourSegmentLoop()
    {
        var mask = new bool[25];
        mask[2 * 5 + 2] = true;
        var loops = MarchingSquares.Trace(mask, 5, 5, 0, 0, 5, 5);

        Assert.Single(loops);
        Assert.Equal(5, loops[0].Count);
        Assert.Equal(loops[0][0], loops[0][^1]);
    }
}

public class DistanceTransformTests
{
    [Fact]
    public void MeasuresDistanceToTheNearestBackgroundPixel()
    {
        // A single ink pixel in the middle of 7x7, so only it has a non-zero distance.
        var ink = new bool[49];
        ink[3 * 7 + 3] = true;
        var distances = DistanceTransform.Compute(ink, 7, 7);

        Assert.Equal(0.0, distances[0]);
        Assert.Equal(1.0, distances[3 * 7 + 3]);
        Assert.Equal(0.0, distances[2 * 7 + 2]);
    }

    [Fact]
    public void HalfWidthOfAStripeIsHalfItsWidth()
    {
        // 6px-wide vertical stripe in a 12-wide image.
        var ink = new bool[12 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 3; x < 9; x++) ink[y * 12 + x] = true;
        var distances = DistanceTransform.Compute(ink, 12, 4);

        Assert.Equal(3.0, distances[0 * 12 + 5], 6);
        Assert.Equal(3.0, distances[0 * 12 + 3 + 2], 6);
    }
}

public class ThinningTests
{
    [Fact]
    public void ThinsAStripeToASinglePixelLine()
    {
        var ink = new bool[20 * 12];
        for (int y = 3; y < 9; y++)
            for (int x = 2; x < 18; x++) ink[y * 20 + x] = true;
        var skeleton = Thinning.Skeletonize(ink, 20, 12);

        int inkCount = 0, row = -1;
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 20; x++)
                if (skeleton[y * 20 + x])
                {
                    inkCount++;
                    if (row < 0) row = y;
                    Assert.Equal(row, y);
                }
        // One pixel tall, and shorter than the 16px stripe: thinning erodes the flat ends, which
        // GraphSurgery.ExtendEndpoints walks back out to the real ink edge.
        Assert.InRange(inkCount, 6, 16);
    }

    [Fact]
    public void TerminatesOnASolidBlock()
    {
        // Regression: a pass that deletes nothing must not refill the frontier.
        var ink = new bool[64 * 64];
        for (int i = 0; i < ink.Length; i++) ink[i] = true;
        var skeleton = Thinning.Skeletonize(ink, 64, 64);
        Assert.Contains(skeleton, p => p);
    }
}

public class CurveFitterTests
{
    [Fact]
    public void FitsASineWaveWithAFewCubics()
    {
        var points = new List<Vector>();
        for (int i = 0; i <= 400; i++)
        {
            double angle = i / 400.0 * Math.PI * 2;
            points.Add(new Vector(i, 100 + 40 * Math.Sin(angle)));
        }

        var curves = CurveFitter.Fit(points, 0.5);
        Assert.True(curves.Count is > 0 and < 20, $"expected a handful of curves, got {curves.Count}");
        Assert.Equal(points[0], curves[0].Start);
        Assert.Equal(points[^1], curves[^1].End);

        // The fit's own criterion is the distance at the chord-length parameter, which is a little
        // larger than the true distance to the curve, so allow some slack.
        foreach (var point in points)
        {
            double best = double.MaxValue;
            foreach (var curve in curves)
            {
                for (int sample = 0; sample <= 128; sample++) best = Math.Min(best, (curve.Point(sample / 128.0) - point).Length);
            }
            Assert.True(best < 1.0, $"point {point} is {best:0.###} from the fit");
        }
    }

    [Fact]
    public void SimplifyKeepsTheEndsAndDropsCollinearRuns()
    {
        var points = new List<Vector>();
        for (int i = 0; i <= 100; i++) points.Add(new Vector(i, 0));
        var simplified = CurveFitter.Simplify(points, 0.1);
        Assert.Equal(2, simplified.Count);
        Assert.Equal(points[0], simplified[0]);
        Assert.Equal(points[^1], simplified[^1]);
    }
}
