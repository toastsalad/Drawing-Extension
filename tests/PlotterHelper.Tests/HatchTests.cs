using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class HatchTests
{
    private static SvgDocument Doc(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false, canvasHeight: 100, transformOrigin: true);

    [Fact]
    public void Hatch_Fills_A_Rectangle()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            HatchSpacing = 5,
            HatchAngle = 0,
            Inset = false,
            ConnectEnds = false,
        });
        Assert.Single(groups);
        Assert.True(groups[0].Paths.Count > 5, "expected multiple hatch lines across the rect");
        // Horizontal hatches (angle 0 -> lines run horizontally in the hatch frame,
        // i.e. along the SVG x-axis after the 90-degree offset). Each path is a line.
        foreach (var path in groups[0].Paths)
            Assert.Single(path.Ops);
    }

    [Fact]
    public void CrossHatch_Doubles_The_Hatches()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var single = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false, CrossHatch = false });
        var cross = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false, CrossHatch = true });
        Assert.True(cross[0].Paths.Count > single[0].Paths.Count);
    }

    [Fact]
    public void ConnectEnds_Reduces_Pen_Lifts()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var loose = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 3, HatchAngle = 45, Inset = false, ConnectEnds = false });
        var joined = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 3, HatchAngle = 45, Inset = false, ConnectEnds = true, HatchScope = 3 });
        // Loose: one path per hatch segment. Joined: fewer paths (segments chained).
        Assert.True(loose[0].Paths.Count > joined[0].Paths.Count);
        // Joined paths are chained: at least one has more than a single op, and
        // (after the safety clip that keeps the pen inside the outline) every op is
        // a LineTo - the Bézier connectors are flattened for the clip, which is fine
        // since the compiler flattens Béziers for G-code anyway.
        Assert.Contains(joined[0].Paths, p => p.Ops.Count > 1);
        Assert.All(joined[0].Paths, p => Assert.All(p.Ops, o => Assert.True(o is LineTo)));
    }

    [Fact]
    public void Inset_Keeps_Hatches_Inside_The_Boundary()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = true, InsetDistance = 2, ConnectEnds = false });
        // After the y-flip the rect occupies x in [10,70], y in [40,90] (gcode coords).
        // No hatch endpoint should touch the boundary edges.
        foreach (var path in groups[0].Paths)
            foreach (var op in path.Ops)
            {
                if (op is not LineTo l) continue;
                Assert.True(l.P.X > 10 + 0.5 && l.P.X < 70 - 0.5, "hatch x outside inset range");
            }
    }

    [Theory]
    [InlineData(FillStyle.Parallel)]
    [InlineData(FillStyle.Wave)]
    [InlineData(FillStyle.Concentric)]
    [InlineData(FillStyle.Spiral)]
    [InlineData(FillStyle.Hilbert)]
    public void Inset_Applies_To_Every_Fill_Style(FillStyle style)
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = style,
            HatchSpacing = 3,
            HatchAngle = 0,
            Inset = true,
            InsetDistance = 2,
            ConnectEnds = false,
            WaveAmplitude = 0.5,
            WaveWavelength = 10,
        });
        Assert.Single(groups);
        Assert.NotEmpty(groups[0].Paths);
        // After the y-flip the rect is x [10,70], y [40,90]; a 2mm inset keeps every
        // hatch point inside x [12,68], y [42,88] (small tolerance for clipping).
        const double tol = 0.25;
        foreach (var path in groups[0].Paths)
            foreach (var op in path.Ops)
            {
                if (op is not LineTo l) continue;
                Assert.True(l.P.X >= 12 - tol && l.P.X <= 68 + tol, $"{style}: hatch x {l.P.X} outside inset range");
                Assert.True(l.P.Y >= 42 - tol && l.P.Y <= 88 + tol, $"{style}: hatch y {l.P.Y} outside inset range");
            }
    }

    [Fact]
    public void Wave_Fill_Produces_Sampled_Polylines()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Wave,
            HatchSpacing = 5,
            HatchAngle = 0,
            Inset = true,
            InsetDistance = 2,
            ConnectEnds = false,
            WaveAmplitude = 0.5,
            WaveWavelength = 10,
        });
        Assert.Single(groups);
        Assert.NotEmpty(groups[0].Paths);
        // Each wave hatch is a sampled sine polyline, not a single straight line.
        foreach (var path in groups[0].Paths)
        {
            Assert.True(path.Ops.Count > 2, "wave path should be a multi-point polyline");
            Assert.All(path.Ops, o => Assert.True(o is LineTo));
        }
        Assert.Contains(groups[0].Paths, p => p.Ops.Count >= 8);
    }

    // Repro for "parallel hatch sometimes draws outside the outlines".
    private static List<List<Vector>> ClosedSubpaths(SvgPath src, double tol)
    {
        var polys = new List<List<Vector>>();
        var sub = new List<Vector>();
        Vector? lastEnd = null;
        foreach (var c in src.Curves)
        {
            if (lastEnd.HasValue && Vector.DistanceSquared(lastEnd.Value, c.Start) > 1.0)
            {
                if (sub.Count >= 3 && Vector.DistanceSquared(sub[0], sub[^1]) < 1.0) polys.Add(sub);
                sub = new List<Vector>();
            }
            if (sub.Count == 0) sub.Add(c.Start);
            c.Flatten(sub, tol);
            lastEnd = c.End;
        }
        if (sub.Count >= 3 && Vector.DistanceSquared(sub[0], sub[^1]) < 1.0) polys.Add(sub);
        return polys;
    }

    private static List<Vector> FlattenHatchPath(HatchPath p, double tol)
    {
        var pts = new List<Vector> { p.Start };
        Vector pen = p.Start;
        foreach (var op in p.Ops)
        {
            if (op is LineTo l) { pts.Add(l.P); pen = l.P; }
            else if (op is CubicTo c)
            {
                var cb = new CubicBezier(pen, c.C1, c.C2, c.P);
                cb.Flatten(pts, tol);
                pen = c.P;
            }
        }
        return pts;
    }

    private static bool InsideEvenOdd(Vector p, List<List<Vector>> polys)
    {
        // Even-odd: inside iff an odd number of rings contain the point.
        int count = 0;
        foreach (var poly in polys)
            if (HatchClipper.PointInPolygon(p, poly)) count++;
        return (count & 1) == 1;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parallel_Hatch_Stays_Inside_Concave_Outline(bool connectEnds)
    {
        // A U-shaped (concave) outline: outer 40x60 box with a notch cut from the top.
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<path d=\"M0,0 L40,0 L40,60 L25,60 L25,10 L15,10 L15,60 L0,60 Z\"/></svg>");
        var src = doc.Paths[0];
        var polys = ClosedSubpaths(src, 0.02);
        Assert.NotEmpty(polys);

        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Parallel,
            HatchSpacing = 2,
            HatchAngle = 45,
            Inset = true,
            InsetDistance = 1,
            ConnectEnds = connectEnds,
            HatchScope = 4,
        });
        Assert.NotEmpty(groups);

        int outside = 0;
        foreach (var p in groups[0].Paths)
            foreach (var v in FlattenHatchPath(p, 0.05))
                if (!InsideEvenOdd(v, polys)) outside++;

        Assert.True(outside == 0, $"{(connectEnds ? "connectEnds" : "loose")}: {outside} hatch points outside the outline");
    }

    [Fact]
    public void Parallel_Respects_EvenOdd_Hole_And_Stays_Inside()
    {
        // Outer square with a concentric inner square subpath -> even/odd makes the
        // inner square a hole. Parallel hatch must (a) not hatch the hole and
        // (b) keep every point inside the outer outline. This is the portable
        // counterpart to the octopus regression: the old scanline even/odd sweep
        // miscounted on overlapping/degenerate subpaths and could hatch outside.
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<path d=\"M10,10 L90,10 L90,90 L10,90 Z M30,30 L70,30 L70,70 L30,70 Z\"/></svg>");
        var src = doc.Paths[0];
        var polys = ClosedSubpaths(src, 0.02);
        Assert.Equal(2, polys.Count); // outer + hole
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Parallel,
            HatchSpacing = 3,
            HatchAngle = 45,
            Inset = false,
            ConnectEnds = false,
        });
        Assert.NotEmpty(groups);
        foreach (var p in groups[0].Paths)
            foreach (var v in FlattenHatchPath(p, 0.05))
            {
                // Inside the outer square ...
                Assert.True(v.X > 9.5 && v.X < 90.5 && v.Y > 9.5 && v.Y < 90.5, $"hatch outside outer outline at {v.X:F2},{v.Y:F2}");
                // ... but NOT in the hole (x,y both in [30,70] is the hole interior).
                Assert.False(v.X > 30.5 && v.X < 69.5 && v.Y > 30.5 && v.Y < 69.5, $"hatch fills the even/odd hole at {v.X:F2},{v.Y:F2}");
            }
    }

    [Fact]
    public void Parallel_Overlapping_Subpaths_No_Outside_Hatch()
    {
        // Two overlapping filled squares as one path (non-overlapping-union region).
        // Even/odd cancels the overlap; the hatch must stay within the union and
        // never escape the combined outline.
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"100\">" +
                      "<path d=\"M10,10 L60,10 L60,60 L10,60 Z M40,40 L90,40 L90,90 L40,90 Z\"/></svg>");
        var src = doc.Paths[0];
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Parallel,
            HatchSpacing = 2,
            HatchAngle = 30,
            Inset = true,
            InsetDistance = 0.5,
            ConnectEnds = true,
        });
        Assert.NotEmpty(groups);
        foreach (var p in groups[0].Paths)
            foreach (var v in FlattenHatchPath(p, 0.05))
            {
                // Must be inside the even/odd union: in square A ([10,60]) or square B
                // ([40,90] x, [40,90] y) but NOT in the overlap alone (which even/odd cancels)
                // -- i.e. inside at least one square, and within the combined bbox.
                // After the y-flip (canvasHeight=100): A is x[10,60] y[40,90]; B is x[40,90] y[10,60].
                bool inA = v.X > 9.5 && v.X < 60.5 && v.Y > 39.5 && v.Y < 90.5;
                bool inB = v.X > 39.5 && v.X < 90.5 && v.Y > 9.5 && v.Y < 60.5;
                Assert.True(inA || inB, $"parallel hatch outside the union outline at {v.X:F2},{v.Y:F2}");
            }
    }

    [Fact]
    public void RoundedRect_Hatch_Stays_Inside_The_Rounded_Outline()
    {
        // Regression: <rect rx ry> used to be parsed as a sharp rectangle, so the
        // parallel hatch filled all the way out to the sharp corners - which sit
        // outside the rounded outline the user sees. The hatch (and outline) must
        // follow the rounded corners instead.
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
                      "<rect x=\"10\" y=\"10\" width=\"80\" height=\"60\" rx=\"15\" ry=\"15\"/></svg>");
        var src = doc.Paths[0];
        var polys = ClosedSubpaths(src, 0.02);
        Assert.NotEmpty(polys);

        // The polygon must be rounded: the sharp corner (90,30 in gcode) must NOT be
        // a vertex, and the rightmost extent at the corner rows must be inset.
        foreach (var poly in polys)
            foreach (var v in poly)
            {
                Assert.False(Math.Abs(v.X - 90) < 0.5 && Math.Abs(v.Y - 30) < 0.5, "sharp top-right corner present");
                Assert.False(Math.Abs(v.X - 90) < 0.5 && Math.Abs(v.Y - 90) < 0.5, "sharp bottom-right corner present");
            }

        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Parallel,
            HatchSpacing = 3,
            HatchAngle = 90,
            Inset = false,
            ConnectEnds = false,
        });
        Assert.NotEmpty(groups);
        // No hatch point may reach the cut-corner region: at y near the top/bottom
        // edges the rounded shape has curved inward, so x must be well under 90.
        foreach (var p in groups[0].Paths)
            foreach (var v in FlattenHatchPath(p, 0.05))
            {
                if (Math.Abs(v.Y - 30) < 5 || Math.Abs(v.Y - 90) < 5)
                    Assert.True(v.X < 90 - 5, $"hatch reaches cut-corner region at {v.X:F2},{v.Y:F2}");
            }
    }

    [Fact]
    public void RoundedRect_Parses_To_Arcs_Not_Sharp_Corners()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"40\" rx=\"10\" ry=\"10\"/></svg>");
        // A rounded rect yields elliptical-arc curves; a sharp rect yields only lines.
        Assert.Contains(doc.Paths[0].Curves, c => c is EllipticalArc);
    }

    [Fact]
    public void SharpRect_Still_Parses_To_Four_Lines()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"40\"/></svg>");
        Assert.All(doc.Paths[0].Curves, c => Assert.True(c is Line));
    }

    [Fact]
    public void Wave_Crosshatch_Doubles_The_Paths()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var one = HatchEngine.Generate(doc.Paths, new HatchOptions { FillStyle = FillStyle.Wave, HatchSpacing = 5, HatchAngle = 0, Inset = false, WaveWavelength = 10 });
        var two = HatchEngine.Generate(doc.Paths, new HatchOptions { FillStyle = FillStyle.Wave, HatchSpacing = 5, HatchAngle = 0, Inset = false, WaveWavelength = 10, CrossHatch = true });
        Assert.True(two[0].Paths.Count > one[0].Paths.Count);
    }

    [Fact]
    public void Jitter_Is_Deterministic_And_Disturbs_Points()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var clean = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 4, HatchAngle = 45, Inset = false, ConnectEnds = false });

        static HatchOptions Jittered(int seed) => new()
        {
            HatchSpacing = 4,
            HatchAngle = 45,
            Inset = false,
            ConnectEnds = false,
            Jitter = true,
            JitterAmount = 0.5,
            JitterSeed = seed,
        };
        var a = HatchEngine.Generate(doc.Paths, Jittered(42));
        var b = HatchEngine.Generate(doc.Paths, Jittered(42));
        var c = HatchEngine.Generate(doc.Paths, Jittered(7));

        Assert.Equal(Serialize(a), Serialize(b));                 // same seed => identical
        Assert.NotEqual(Serialize(clean), Serialize(a));          // jitter moves points
        Assert.NotEqual(Serialize(a), Serialize(c));              // different seed => different
    }

    [Theory]
    [InlineData(FillStyle.Concentric)]
    [InlineData(FillStyle.Spiral)]
    [InlineData(FillStyle.Hilbert)]
    public void ClipOffset_FillStyles_Produce_Inside_Fill(FillStyle style)
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = style,
            HatchSpacing = 5,
            Inset = false,
        });
        Assert.Single(groups);
        Assert.NotEmpty(groups[0].Paths);
        // After the y-flip the rect is x in [10,70], y in [40,90]. Every generated
        // point (including the spiral touching the boundary) must stay inside it.
        foreach (var path in groups[0].Paths)
        {
            Assert.InRange(path.Start.X, 9.5, 70.5);
            Assert.InRange(path.Start.Y, 39.5, 90.5);
            foreach (var op in path.Ops)
            {
                var p = op is LineTo l ? l.P : ((CubicTo)op).P;
                Assert.InRange(p.X, 9.5, 70.5);
                Assert.InRange(p.Y, 39.5, 90.5);
            }
        }
    }

    [Fact]
    public void Hilbert_Is_Order_Capped_And_Not_Explosive()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\"/></svg>");
        // Absurdly small spacing on a 100-unit rect: order is capped at 8, so the
        // generated polyline is at most 65536 points (no runaway).
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { FillStyle = FillStyle.Hilbert, HatchSpacing = 0.0001, Inset = false });
        Assert.NotEmpty(groups[0].Paths);
        int totalPoints = groups[0].Paths.Sum(p => 1 + p.Ops.Count);
        Assert.True(totalPoints <= 70_000, $"hilbert blew up: {totalPoints} points");
    }

    [Fact]
    public void Concentric_Produces_Multiple_Shrinking_Rings()
    {
        var doc = Doc("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
                      "<rect x=\"10\" y=\"10\" width=\"60\" height=\"50\"/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { FillStyle = FillStyle.Concentric, HatchSpacing = 5, Inset = false });
        // A 60x50 rect at spacing 5 should yield several nested rings, each smaller.
        Assert.True(groups[0].Paths.Count >= 3, "expected multiple concentric rings");
        double firstExtent = double.NaN, lastExtent = double.NaN;
        foreach (var path in groups[0].Paths)
        {
            double maxx = path.Start.X, minx = path.Start.X;
            foreach (var op in path.Ops)
            {
                if (op is LineTo l) { if (l.P.X > maxx) maxx = l.P.X; if (l.P.X < minx) minx = l.P.X; }
            }
            double ext = maxx - minx;
            if (double.IsNaN(firstExtent)) firstExtent = ext; lastExtent = ext;
        }
        Assert.True(lastExtent <= firstExtent, "rings should shrink inward");
    }

    private static string Serialize(List<HatchGroup> groups)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var g in groups)
        {
            sb.Append(g.Paths.Count).Append('|');
            foreach (var p in g.Paths)
            {
                sb.Append(p.Start.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                  .Append(',').Append(p.Start.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                foreach (var op in p.Ops)
                {
                    if (op is LineTo l)
                        sb.Append(l.P.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                          .Append(',').Append(l.P.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                    else if (op is CubicTo cu)
                        sb.Append('C').Append(cu.P.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                          .Append(',').Append(cu.P.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                }
            }
        }
        return sb.ToString();
    }
}
