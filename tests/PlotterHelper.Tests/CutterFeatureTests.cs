using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class CutterFeatureTests
{
    private static SvgPath PathOf(params Curve[] curves)
    {
        var p = new SvgPath();
        foreach (var c in curves) p.Curves.Add(c);
        return p;
    }

    [Fact]
    public void Overcut_Appends_An_Extension_To_A_Closed_Subpath()
    {
        // A 10x10 square (closed), overcut 2 -> a 5th Line from (0,0) along +x by 2 -> (2,0).
        var path = PathOf(
            new Line(new(0, 0), new(10, 0)),
            new Line(new(10, 0), new(10, 10)),
            new Line(new(10, 10), new(0, 10)),
            new Line(new(0, 10), new(0, 0)));
        Assert.Equal(4, path.Curves.Count);
        Cli.Features.Overcut.ApplyToPath(path, overcut: 2, 0.01);
        Assert.Equal(5, path.Curves.Count);                 // one extension appended
        var ext = path.Curves[4];
        Assert.True(ext is Line);
        Assert.Equal(0.0, ext.Start.X, 3);                   // starts at the close point (0,0)
        Assert.Equal(0.0, ext.Start.Y, 3);
        Assert.Equal(2.0, ext.End.X, 3);                     // continues along first edge (+x)
        Assert.Equal(0.0, ext.End.Y, 3);
    }

    [Fact]
    public void Overcut_Ignores_Open_Subpaths()
    {
        var path = PathOf(new Line(new(0, 0), new(10, 5))); // not closed
        Cli.Features.Overcut.ApplyToPath(path, overcut: 2, 0.01);
        Assert.Single(path.Curves);                          // no extension added
    }

    [Fact]
    public void InnerBeforeOuter_Puts_Hole_Before_Outer()
    {
        // Outer square 0..100, inner hole 40..60. The outer's rep point (0,0) is NOT
        // inside the inner; the inner's rep point (40,40) IS inside the outer.
        // So inner depth=1, outer depth=0 -> inner first.
        var outer = PathOf(
            new Line(new(0, 0), new(100, 0)),
            new Line(new(100, 0), new(100, 100)),
            new Line(new(100, 100), new(0, 100)),
            new Line(new(0, 100), new(0, 0)));
        var hole = PathOf(
            new Line(new(40, 40), new(60, 40)),
            new Line(new(60, 40), new(60, 60)),
            new Line(new(60, 60), new(40, 60)),
            new Line(new(40, 60), new(40, 40)));
        // Document order: outer first, then hole. After reorder, hole must come first.
        var ordered = Cli.Features.InnerBeforeOuter.Reorder(new List<SvgPath> { outer, hole }, 0.01);
        Assert.Same(hole, ordered[0]);
        Assert.Same(outer, ordered[1]);
    }

    [Fact]
    public void InnerBeforeOuter_Nests_Three_Deep()
    {
        // Outer 0..90, mid 20..70, inner 35..55. Depths: outer=0, mid=1, inner=2.
        static Curve[] Square(double a, double b) => new Curve[]
        {
            new Line(new(a, a), new(b, a)),
            new Line(new(b, a), new(b, b)),
            new Line(new(b, b), new(a, b)),
            new Line(new(a, b), new(a, a)),
        };
        var outer = PathOf(Square(0, 90));
        var mid = PathOf(Square(20, 70));
        var inner = PathOf(Square(35, 55));
        var ordered = Cli.Features.InnerBeforeOuter.Reorder(new List<SvgPath> { outer, mid, inner }, 0.01);
        // Deepest first: inner, mid, outer.
        Assert.Same(inner, ordered[0]);
        Assert.Same(mid, ordered[1]);
        Assert.Same(outer, ordered[2]);
    }
}
