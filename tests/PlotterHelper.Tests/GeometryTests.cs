using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class GeometryTests
{
    [Fact]
    public void Vector_Arithmetic_IsCorrect()
    {
        var a = new Vector(3, 4);
        Assert.Equal(5, a.Length);
        Assert.Equal(25, a.LengthSquared);
        Assert.Equal(new Vector(5, 7), a + new Vector(2, 3));
        Assert.Equal(new Vector(6, 8), a * 2);
        Assert.Equal(6, Vector.Dot(new Vector(2, 3), new Vector(3, 0)));
    }

    [Fact]
    public void Affine_Compose_And_Inverse_RoundTrip()
    {
        var m = Affine.Translation(10, 20).Compose(Affine.Rotation(0.5)).Compose(Affine.Scale(2, 3));
        var p = new Vector(7, -3);
        var q = m.Apply(p);
        var inv = m.Inverse()!.Value;
        Assert.Equal(p.X, inv.Apply(q).X, 6);
        Assert.Equal(p.Y, inv.Apply(q).Y, 6);
    }

    [Fact]
    public void CubicBezier_Flatten_IsFrontToBack_And_WithinTolerance()
    {
        var b = new CubicBezier(new(0, 0), new(0, 100), new(100, 100), new(100, 0));
        var pts = new List<Vector> { b.Start };
        b.Flatten(pts, tolerance: 0.05);
        // Points must progress monotonically along the curve (front-to-back).
        for (int point = 1; point < pts.Count; point++)
            Assert.True(pts[point].X >= pts[point - 1].X - 1e-6, $"point {point} went backwards");
        // Each segment must stay within tolerance of the true curve (sample check).
        for (int point = 1; point < pts.Count; point++)
        {
            double t0 = (point - 1) / (double)(pts.Count - 1);
            double t1 = point / (double)(pts.Count - 1);
            double mid = b.Point((t0 + t1) / 2).Y;
            double chordMid = ((pts[point - 1] + pts[point]) / 2).Y;
            Assert.True(Math.Abs(mid - chordMid) <= 0.1, $"segment {point} deviates by {Math.Abs(mid - chordMid)}");
        }
    }

    [Fact]
    public void EllipticalArc_Circle_MatchesTrueCircle()
    {
        // A full circle as two 180-degree arcs (SVG-style).
        var arc = EllipticalArc.FromEndpoint(new(0, 0), new(10, 0), 5, 5, 0, 1, 1, Affine.Identity);
        Assert.NotNull(arc);
        // Semicircle from (0,0) to (10,0) with r=5 has center (5,0); points lie on it.
        for (int sample = 0; sample <= 20; sample++)
        {
            var p = arc.Point(sample / 20.0);
            Assert.Equal(5, Math.Sqrt((p.X - 5) * (p.X - 5) + p.Y * p.Y), 3);
        }
    }
}
