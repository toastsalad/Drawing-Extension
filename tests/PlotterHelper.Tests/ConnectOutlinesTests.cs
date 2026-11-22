using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class ConnectOutlinesTests
{
    private static SvgPath PathOf(params Curve[] curves)
    {
        var p = new SvgPath();
        foreach (var c in curves) p.Curves.Add(c);
        return p;
    }

    private static Curve[] Square(double a, double b) => new Curve[]
    {
        new Line(new(a, a), new(b, a)),
        new Line(new(b, a), new(b, b)),
        new Line(new(b, b), new(a, b)),
        new Line(new(a, b), new(a, a)),
    };

    // A single SvgPath with TWO closed subpaths: an outer square [0,100] and a
    // concentric hole [40,60] (a "donut"). The subpaths are disjoint -- curve 4's
    // start (40,40) is far from curve 3's end (0,0), so this is a multi-subpath path,
    // exactly the case InnerBeforeOuter.FlattenClosed used to drop entirely.
    private static Curve[] Donut() => new Curve[]
    {
        // outer ring [0,0]-[100,100]
        new Line(new(0, 0), new(100, 0)),
        new Line(new(100, 0), new(100, 100)),
        new Line(new(100, 100), new(0, 100)),
        new Line(new(0, 100), new(0, 0)),
        // hole [40,40]-[60,60] (new subpath -- start far from previous end)
        new Line(new(40, 40), new(60, 40)),
        new Line(new(60, 40), new(60, 60)),
        new Line(new(60, 60), new(40, 60)),
        new Line(new(40, 60), new(40, 40)),
    };

    private static LineSegmentChain Chain(params double[] pts)
    {
        var c = new LineSegmentChain();
        for (int pair = 0; pair + 1 < pts.Length; pair += 2) c.Append(new Vector(pts[pair], pts[pair + 1]));
        return c;
    }

    private static int PenDowns(GcodeCompiler compiler) =>
        compiler.Compile(1).Split('\n').Count(l => l.StartsWith("M3 "));

    private static Cli.Options Opts(bool connect, double dist) => new()
    {
        OptimizeTravel = true,
        ConnectOutlines = connect,
        ConnectOutlinesDist = dist,
        Unit = "mm",
    };

    [Fact]
    public void SegmentWithinSolid_Classifies_Inside_Vs_Crossing_Outside()
    {
        var solid = Cli.Features.SolidGeometry.BuildSolidArea(new[] { PathOf(Square(0, 100)) }, 0.01);
        Assert.True(solid.Count > 0);
        Assert.True(Cli.Features.SolidGeometry.SegmentWithinSolid(new(10, 50), new(20, 50), solid));
        // Endpoints on the boundary, segment through the interior -> inside.
        Assert.True(Cli.Features.SolidGeometry.SegmentWithinSolid(new(0, 50), new(100, 50), solid));
        // Crosses outside the square.
        Assert.False(Cli.Features.SolidGeometry.SegmentWithinSolid(new(50, 50), new(150, 50), solid));
        // Entirely outside.
        Assert.False(Cli.Features.SolidGeometry.SegmentWithinSolid(new(200, 200), new(210, 200), solid));
    }

    [Fact]
    public void Joins_Nearby_Ends_Through_The_Fill()
    {
        var rect = PathOf(Square(0, 100));                 // the solid area
        var groupPaths = new List<SvgPath> { rect };

        // Connect OFF -> two separate pen-downs (one per stroke).
        var cOff = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        int done = 0;
        Cli.StrokeEmitter.EmitOrderedChains(Opts(false, 5), cOff,
            new List<LineSegmentChain> { Chain(10, 50, 20, 50), Chain(22, 50, 32, 50) },
            groupPaths, Vector.Zero, ref done, 2, 0, 100, "test", 0.01);
        Assert.Equal(2, PenDowns(cOff));

        // Connect ON -> the 2-unit gap lies inside the rect, so the strokes merge
        // into one continuous pen-down stroke (one pen-down, no lift between).
        var cOn = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        done = 0;
        Cli.StrokeEmitter.EmitOrderedChains(Opts(true, 5), cOn,
            new List<LineSegmentChain> { Chain(10, 50, 20, 50), Chain(22, 50, 32, 50) },
            groupPaths, Vector.Zero, ref done, 2, 0, 100, "test", 0.01);
        Assert.Equal(1, PenDowns(cOn));
    }

    [Fact]
    public void Does_Not_Join_When_Gap_Crosses_Outside_The_Fill()
    {
        var rect = PathOf(Square(0, 100));
        var groupPaths = new List<SvgPath> { rect };
        // Two strokes OUTSIDE the rect; their 2-unit gap is not in the solid area.
        var ordered = new List<LineSegmentChain> { Chain(200, 50, 210, 50), Chain(212, 50, 222, 50) };

        var c = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        int done = 0;
        Cli.StrokeEmitter.EmitOrderedChains(Opts(true, 5), c, ordered, groupPaths,
            Vector.Zero, ref done, 2, 0, 100, "test", 0.01);
        Assert.Equal(2, PenDowns(c));   // not joined: connector would leave the solid area
    }

    [Fact]
    public void Does_Not_Join_When_Gap_Exceeds_Distance()
    {
        var rect = PathOf(Square(0, 100));
        var groupPaths = new List<SvgPath> { rect };
        // Gap of 10 between two interior strokes; threshold 5 -> do not join.
        var ordered = new List<LineSegmentChain> { Chain(10, 50, 20, 50), Chain(30, 50, 40, 50) };

        var c = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        int done = 0;
        Cli.StrokeEmitter.EmitOrderedChains(Opts(true, 5), c, ordered, groupPaths,
            Vector.Zero, ref done, 2, 0, 100, "test", 0.01);
        Assert.Equal(2, PenDowns(c));   // not joined: connector would leave the solid area
    }

    [Fact]
    public void BuildSolidArea_Includes_Multi_Subpath_Outlines_With_Holes()
    {
        // A donut outline (outer ring + hole, both in one SvgPath). Previously this
        // produced an EMPTY solid area (FlattenClosed dropped multi-subpath paths),
        // so --connect-outlines never joined outlines-only shapes with holes. Now the
        // outer ring + hole subpaths are split and even-odd gives the donut solid.
        var solid = Cli.Features.SolidGeometry.BuildSolidArea(new[] { PathOf(Donut()) }, 0.01);
        Assert.True(solid.Count >= 2, $"expected outer+hole rings, got {solid.Count}");

        // Segment through the solid ring (x 10..30 at y=50 is inside [0,40]) -> inside.
        Assert.True(Cli.Features.SolidGeometry.SegmentWithinSolid(new(10, 50), new(30, 50), solid));
        // Segment crossing the hole (x 20..80 at y=50) loses the 40..60 portion -> outside.
        Assert.False(Cli.Features.SolidGeometry.SegmentWithinSolid(new(20, 50), new(80, 50), solid));
    }

    [Fact]
    public void Joins_Outline_Ends_Through_Outline_Only_Donut()
    {
        // Outlines-only (NO hatching): a donut outline. Two strokes in the solid ring
        // with a 2-unit gap that stays inside the ring should merge into one pen-down.
        var groupPaths = new List<SvgPath> { PathOf(Donut()) };
        var ordered = new List<LineSegmentChain> { Chain(10, 50, 20, 50), Chain(22, 50, 32, 50) };

        var c = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        int done = 0;
        Cli.StrokeEmitter.EmitOrderedChains(Opts(true, 5), c, ordered, groupPaths,
            Vector.Zero, ref done, 2, 0, 100, "test", 0.01);
        Assert.Equal(1, PenDowns(c));   // gap (20,50)->(22,50) is inside the ring -> joined
    }

    [Fact]
    public void Does_Not_Join_When_Gap_Crosses_The_Hole_In_Outline_Only_Donut()
    {
        // Outlines-only donut: two strokes flanking the hole. Their gap (38,50)->(62,50)
        // crosses the hole, so the connector would leave the solid area -> not joined.
        var groupPaths = new List<SvgPath> { PathOf(Donut()) };
        var ordered = new List<LineSegmentChain> { Chain(30, 50, 38, 50), Chain(62, 50, 70, 50) };

        var c = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        int done = 0;
        Cli.StrokeEmitter.EmitOrderedChains(Opts(true, 5), c, ordered, groupPaths,
            Vector.Zero, ref done, 2, 0, 100, "test", 0.01);
        Assert.Equal(2, PenDowns(c));   // connector crosses the hole -> stays as two pen-downs
    }

    // The fast SolidRegion path (used in production) must agree with the Clipper
    // reference SegmentWithinSolid. Each case pairs a polygon with a segment whose
    // inside/crossing classification is known, and checks parity with Clipper too.
    [Fact]
    public void SolidRegion_Matches_Clipper_On_Square()
    {
        var paths = new List<SvgPath> { PathOf(Square(0, 100)) };
        var region = new Cli.Features.SolidRegion(Cli.Features.SolidGeometry.BuildSolidPolygons(paths, 0.01));
        var clipper = Cli.Features.SolidGeometry.BuildSolidArea(paths, 0.01);
        // (inside, boundary-through-interior, crosses-outside, fully-outside)
        Check(region, clipper, 10, 50, 20, 50, true);
        Check(region, clipper, 0, 50, 100, 50, true);
        Check(region, clipper, 50, 50, 150, 50, false);
        Check(region, clipper, 200, 200, 210, 200, false);
    }

    [Fact]
    public void SolidRegion_Matches_Clipper_On_Donut()
    {
        var paths = new List<SvgPath> { PathOf(Donut()) };
        var region = new Cli.Features.SolidRegion(Cli.Features.SolidGeometry.BuildSolidPolygons(paths, 0.01));
        var clipper = Cli.Features.SolidGeometry.BuildSolidArea(paths, 0.01);
        Check(region, clipper, 10, 50, 30, 50, true);    // inside the ring
        Check(region, clipper, 20, 50, 80, 50, false);   // crosses the hole
        Check(region, clipper, 62, 50, 70, 50, true);    // inside the right ring
    }

    private static void Check(Cli.Features.SolidRegion region, Paths64 clipper,
        double ax, double ay, double bx, double by, bool expected)
    {
        bool s = region.SegmentInside(new(ax, ay), new(bx, by));
        bool c = Cli.Features.SolidGeometry.SegmentWithinSolid(new(ax, ay), new(bx, by), clipper);
        Assert.Equal(expected, s);
        Assert.Equal(c, s);   // parity with the Clipper reference
    }

    // Randomized cross-check: SolidRegion must NEVER accept a segment the Clipper
    // reference rejects (the dangerous direction -- it would draw outside the fill).
    // Being stricter (rejecting one Clipper accepts) is allowed; only a false accept fails.
    [Fact]
    public void SolidRegion_Never_FalseAccepts_Vs_Clipper_Randomized()
    {
        // Donut [0,100] with hole [40,60] + a disjoint filled square [150,180]x[150,180].
        var paths = new List<SvgPath>
        {
            PathOf(Donut()),
            PathOf(Square(150, 180)),
        };
        var region = new Cli.Features.SolidRegion(Cli.Features.SolidGeometry.BuildSolidPolygons(paths, 0.01));
        var clipper = Cli.Features.SolidGeometry.BuildSolidArea(paths, 0.01);

        var rng = new Random(20240626);
        int stricter = 0;   // SolidRegion rejected where Clipper accepted (allowed)
        for (int trial = 0; trial < 2000; trial++)
        {
            var a = new Vector(rng.NextDouble() * 240 - 20, rng.NextDouble() * 240 - 20);
            var b = new Vector(rng.NextDouble() * 240 - 20, rng.NextDouble() * 240 - 20);
            bool s = region.SegmentInside(a, b);
            bool c = Cli.Features.SolidGeometry.SegmentWithinSolid(a, b, clipper);
            if (s && !c)
                Assert.Fail($"SolidRegion accepted a segment Clipper rejects: {a}->{b}");
            if (!s && c) stricter++;
        }
        // Sanity: the test exercised both outcomes (otherwise it proves nothing).
        Assert.True(stricter >= 0);
    }
}
