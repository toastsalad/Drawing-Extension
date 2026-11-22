using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class TravelOptimizerTests
{
    private static LineSegmentChain Chain(params double[] pts)
    {
        var c = new LineSegmentChain();
        foreach (var p in Pairs(pts)) c.Append(new Vector(p[0], p[1]));
        return c;
    }
    private static IEnumerable<double[]> Pairs(double[] pts)
    {
        for (int pair = 0; pair + 1 < pts.Length; pair += 2) yield return new[] { pts[pair], pts[pair + 1] };
    }
    private static double TravelDist(List<LineSegmentChain> ordered)
    {
        // Sum of pen-up gaps between consecutive chains (end[i] -> start[i+1]).
        double t = 0;
        for (int chain = 1; chain < ordered.Count; chain++)
            t += Vector.Distance(ordered[chain - 1][^1], ordered[chain][0]);
        return t;
    }

    [Fact]
    public void NearestNeighbor_Collapses_Travel_Vs_DocumentOrder()
    {
        // Four disjoint short strokes far apart in document order:
        //   A at x~0, B at x~100, C at x~1, D at x~101.
        // Document order jumps 0->100->1->101; NN should visit A,C then B,D.
        var chains = new List<LineSegmentChain>
        {
            Chain(0, 0, 0, 1),    // A
            Chain(100, 0, 100, 1),// B
            Chain(1, 0, 1, 1),    // C
            Chain(101, 0, 101, 1),// D
        };
        var docTravel = TravelDist(chains);                 // ~99 + ~99 + ~100 = 298
        var ordered = Cli.Features.TravelOptimizer.Optimize(chains, Vector.Zero, allowReverse: true);
        Assert.Equal(4, ordered.Count);
        var optTravel = TravelDist(ordered);
        Assert.True(optTravel < docTravel / 2, $"NN travel {optTravel} not much better than doc {docTravel}");
        // Starts near the origin (the closest endpoint to (0,0) is A’s start).
        Assert.Equal(0, ordered[0][0].X, 6);
    }

    [Fact]
    public void Reversal_Used_When_End_Is_Closer()
    {
        // Stroke 1 ends at (10,0). Stroke 2 runs (20,0)->(11,0): its END (11,0)
        // is closer to (10,0) than its START (20,0), so it should be reversed.
        var chains = new List<LineSegmentChain>
        {
            Chain(0, 0, 10, 0),
            Chain(20, 0, 11, 0),
        };
        var ordered = Cli.Features.TravelOptimizer.Optimize(chains, Vector.Zero, allowReverse: true);
        Assert.Equal(2, ordered.Count);
        Assert.Equal(10, ordered[0][^1].X, 6);               // first stroke ends at 10
        Assert.Equal(11, ordered[1][0].X, 6);                // second stroke approached from its end (11)
        Assert.Equal(20, ordered[1][^1].X, 6);               // ...and drawn backwards to 20
    }

    [Fact]
    public void No_Reversal_Respects_Direction()
    {
        var chains = new List<LineSegmentChain>
        {
            Chain(0, 0, 10, 0),
            Chain(20, 0, 11, 0),
        };
        var ordered = Cli.Features.TravelOptimizer.Optimize(chains, Vector.Zero, allowReverse: false);
        // Without reversal, stroke 2 keeps its (20,0)->(11,0) direction.
        Assert.Equal(20, ordered[1][0].X, 6);
        Assert.Equal(11, ordered[1][^1].X, 6);
    }

    [Fact]
    public void Connected_Subpath_Stays_In_Order()
    {
        // A rectangle as 4 connected line chains (each shares its end with the
        // next’s start). NN must keep them in document order (distance ~0 between
        // connected ends), not reorder the sides.
        var chains = new List<LineSegmentChain>
        {
            Chain(0, 0, 10, 0),
            Chain(10, 0, 10, 10),
            Chain(10, 10, 0, 10),
            Chain(0, 10, 0, 0),
        };
        var ordered = Cli.Features.TravelOptimizer.Optimize(chains, Vector.Zero, allowReverse: true);
        Assert.Equal(4, ordered.Count);
        for (int chain = 0; chain < 4; chain++)
            Assert.Equal(chains[chain][0].X, ordered[chain][0].X, 6);   // same starts, same order
        Assert.Equal(0, TravelDist(ordered), 6);               // zero travel (closed loop)
    }

    [Fact]
    public void Degenerate_Chains_Are_Dropped()
    {
        var chains = new List<LineSegmentChain>
        {
            Chain(0, 0, 1, 1),
            new(),                            // empty
            Chain(50, 50, 51, 51),
        };
        var ordered = Cli.Features.TravelOptimizer.Optimize(chains, Vector.Zero, allowReverse: true);
        Assert.Equal(2, ordered.Count);
    }
}
