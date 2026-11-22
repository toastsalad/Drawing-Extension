using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Tests;

/// <summary>
/// The fills added from the plotter-art repertoire: serpentine (lawnmower), Fermat spiral and moiré.
/// Each is checked for the property it exists for, not just for producing output.
/// </summary>
public class HatchStyleTests
{
    private static SvgDocument Doc(string body) => SvgParser.ParseString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" + body + "</svg>",
        handleViewBox: false, canvasHeight: 100, transformOrigin: true);

    /// <summary>A square with a square hole: one hatch line through it arrives as two runs.</summary>
    private static SvgDocument Donut() => Doc(
        "<path d=\"M10,10 L90,10 L90,90 L10,90 Z M40,40 L60,40 L60,60 L40,60 Z\"/>");

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

    private static List<List<Vector>> Outline(SvgDocument doc) =>
        HatchEngine.BuildPolygons(doc.Paths[0], 0.02, autoClose: false);

    private static IEnumerable<Vector> Points(HatchPath path)
    {
        yield return path.Start;
        foreach (var op in path.Ops) if (op is LineTo line) yield return line.P;
    }

    [Theory]
    [InlineData(FillStyle.Serpentine)]
    [InlineData(FillStyle.Fermat)]
    [InlineData(FillStyle.Moire)]
    public void New_Styles_Fill_The_Shape_Without_Leaving_It(FillStyle style)
    {
        var doc = Doc("<path d=\"M10,10 L90,10 L90,90 L10,90 Z\"/>");
        var outline = Outline(doc)[0];

        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = style,
            HatchSpacing = 4,
            HatchAngle = 45,
            Inset = true,
            InsetDistance = 1,
            ConnectEnds = false,
        });

        Assert.Single(groups);
        Assert.NotEmpty(groups[0].Paths);

        int outside = 0;
        foreach (var path in groups[0].Paths)
            foreach (var point in Points(path))
                if (!InsideEvenOdd(outline, point)) outside++;
        Assert.True(outside == 0, $"{style}: {outside} points outside the outline");
    }

    [Fact]
    public void Serpentine_Uses_Fewer_Strokes_Than_Parallel()
    {
        // The whole point of the lawnmower order: the same hatch lines, welded into long strokes.
        var doc = Doc("<path d=\"M10,10 L90,10 L90,90 L10,90 Z\"/>");
        var options = new HatchOptions
        {
            HatchSpacing = 4,
            HatchAngle = 45,
            Inset = true,
            InsetDistance = 1,
            ConnectEnds = false,
        };

        options.FillStyle = FillStyle.Parallel;
        int parallel = HatchEngine.Generate(doc.Paths, options)[0].Paths.Count;

        options.FillStyle = FillStyle.Serpentine;
        var joined = HatchEngine.Generate(doc.Paths, options)[0].Paths;

        Assert.True(joined.Count < parallel,
            $"serpentine should use fewer strokes: {joined.Count} vs parallel {parallel}");
        Assert.Contains(joined, path => path.Ops.Count > 1);
    }

    [Fact]
    public void Serpentine_Does_Not_Draw_Across_A_Hole()
    {
        // Chaining runs per line rather than per line index is what keeps the stroke out of the hole:
        // the pair welded together must be the ends of one run, never the two sides of the hole.
        var doc = Donut();
        var outline = Outline(doc);

        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Serpentine,
            HatchSpacing = 4,
            HatchAngle = 0,
            Inset = false,
            ConnectEnds = false,
        });

        int inHole = 0;
        foreach (var path in groups[0].Paths)
        {
            var points = Points(path).ToList();
            for (int i = 0; i + 1 < points.Count; i++)
            {
                // Sample along each drawn segment; the hole is x,y in (40,60) after the transform.
                for (int step = 1; step < 8; step++)
                {
                    double t = step / 8.0;
                    var p = new Vector(
                        points[i].X + (points[i + 1].X - points[i].X) * t,
                        points[i].Y + (points[i + 1].Y - points[i].Y) * t);
                    if (p.X > 42 && p.X < 58 && p.Y > 42 && p.Y < 58 && !InsideEvenOdd(outline[0], p)) inHole++;
                }
            }
        }
        Assert.True(inHole == 0, $"{inHole} samples drawn inside the hole");
    }

    [Fact]
    public void Fermat_And_Spiral_Are_Different_Shapes()
    {
        // Both are single clipped spirals, so guard against one silently becoming the other.
        var doc = Doc("<path d=\"M10,10 L90,10 L90,90 L10,90 Z\"/>");
        var options = new HatchOptions
        {
            HatchSpacing = 5,
            HatchAngle = 0,
            Inset = false,
            ConnectEnds = false,
        };

        options.FillStyle = FillStyle.Spiral;
        long archimedean = HatchEngine.Generate(doc.Paths, options).Sum(g => g.Paths.Sum(p => p.Ops.Count));

        options.FillStyle = FillStyle.Fermat;
        long fermat = HatchEngine.Generate(doc.Paths, options).Sum(g => g.Paths.Sum(p => p.Ops.Count));

        Assert.True(fermat > 0);
        Assert.NotEqual(archimedean, fermat);
    }

    [Fact]
    public void Moire_Roughly_Doubles_The_Ink_Of_One_Screen()
    {
        // Two screens over one shape: about twice the line count, but not exactly, because the second
        // family is at a slightly different pitch and angle and lands differently on the boundaries.
        var doc = Doc("<path d=\"M10,10 L90,10 L90,90 L10,90 Z\"/>");
        var options = new HatchOptions
        {
            HatchSpacing = 5,
            HatchAngle = 30,
            Inset = false,
            ConnectEnds = false,
        };

        options.FillStyle = FillStyle.Parallel;
        int single = HatchEngine.Generate(doc.Paths, options)[0].Paths.Count;

        options.FillStyle = FillStyle.Moire;
        int both = HatchEngine.Generate(doc.Paths, options)[0].Paths.Count;

        Assert.InRange(both, (int)(single * 1.7), (int)(single * 2.4));
    }
}
