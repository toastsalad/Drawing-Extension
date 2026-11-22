using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using PlotterHelper.Trace;

namespace PlotterHelper.Tests;

/// <summary>
/// The tone-driven fills and the tone plumbing under them: a raster mapped onto the element's box, and
/// the shape's own boundary as the fallback when there is no raster.
/// </summary>
public class ToneFillTests
{
    private static SvgDocument Square() => SvgParser.ParseString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
        "<path d=\"M20,20 L180,20 L180,180 L20,180 Z\"/></svg>",
        handleViewBox: false, canvasHeight: 200, transformOrigin: true);

    /// <summary>A ramp, dark on the left and light on the right, as a grayscale image.</summary>
    private static GrayImage Ramp(int width = 64, int height = 64)
    {
        var data = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                data[y * width + x] = (byte)Math.Round(255.0 * x / (width - 1));
        return new GrayImage(width, height, data);
    }

    private static IEnumerable<Vector> Points(HatchPath path)
    {
        yield return path.Start;
        foreach (var op in path.Ops) if (op is LineTo line) yield return line.P;
    }

    private static List<Vector> Ink(FillStyle style, GrayImage? tone, double threshold = 0.5, double toneDepth = 6.0)
        => HatchEngine.Generate(Square().Paths, new HatchOptions
        {
            FillStyle = style,
            HatchSpacing = 4,
            HatchAngle = 0,
            Inset = false,
            ConnectEnds = false,
            ToneImage = tone,
            ToneThreshold = threshold,
            ToneDepth = toneDepth,
        })[0].Paths.SelectMany(Points).ToList();

    /// <summary>
    /// The boundary tone has to reach as far as it is asked to.
    /// </summary>
    /// <remarks>
    /// The distance field behind this used to search boundary samples in the 3x3 block of buckets around each
    /// cell, which could only see one cell away. Every distance was capped at about a cell, so the gradient
    /// spanned a couple of units however deep the tone was asked to go, and --tone-depth did nothing beyond
    /// about one spacing. A chamfer sweep replaced it; this checks the depth has an effect now.
    /// </remarks>
    [Fact]
    public void A_Deeper_Tone_Reaches_Further_Into_The_Shape()
    {
        double DeepestInk(double depth) => Ink(FillStyle.Shade, tone: null, toneDepth: depth)
            .Select(p => Math.Min(
                Math.Min(p.X - 20, 180 - p.X),
                Math.Min(p.Y - 20, 180 - p.Y)))
            .DefaultIfEmpty(0)
            .Max();

        double shallow = DeepestInk(1);
        double deep = DeepestInk(8);

        Assert.True(shallow > 0, $"shallow: expected ink at all, got {shallow}");
        Assert.True(deep > shallow * 2,
            $"a depth of 8 spacings should reach more than twice as far as 1: shallow={shallow:F2} deep={deep:F2}");
    }

    /// <summary>
    /// A gradient has to come out as a gradient of ink, not as a cliff at the threshold.
    /// </summary>
    /// <remarks>
    /// The fills used to keep a step when its tone cleared the threshold and drop it otherwise, so a ramp came
    /// out half solid and half bare with a hard edge in the middle -- the thing a photograph shows when it is
    /// thresholded instead of shaded. Density now follows the tone through a dither, and this checks the shape
    /// of that: ink falls band by band from dark to light, no band jumps from full to empty, and the light end
    /// really is light.
    /// </remarks>
    [Theory]
    [InlineData(FillStyle.Shade)]
    [InlineData(FillStyle.Stipple)]
    public void A_Gradient_Becomes_A_Gradient_Of_Ink(FillStyle style)
    {
        const int bands = 8;
        var paths = HatchEngine.Generate(Square().Paths, new HatchOptions
        {
            FillStyle = style,
            HatchSpacing = 3,
            HatchAngle = 0,
            Inset = false,
            ConnectEnds = false,
            ToneImage = Ramp(),
            ToneThreshold = 0.05,
        })[0].Paths;

        var ink = new double[bands];
        foreach (var path in paths)
        {
            var points = Points(path).ToList();
            for (int i = 1; i < points.Count; i++)
            {
                Vector a = points[i - 1], b = points[i];
                double length = (b - a).Length;
                if (length <= 0) continue;
                // Spread a segment across the bands it crosses, so a long run is not credited to one band.
                int steps = Math.Max(1, (int)Math.Ceiling(length));
                for (int step = 0; step < steps; step++)
                {
                    double t = (step + 0.5) / steps;
                    double x = a.X + (b.X - a.X) * t;
                    int band = Math.Clamp((int)((x - 20) / (160.0 / bands)), 0, bands - 1);
                    ink[band] += length / steps;
                }
            }
        }

        double total = ink.Sum();
        Assert.True(total > 0, $"{style}: no ink at all");
        var fractions = ink.Select(value => value / total).ToArray();

        Assert.True(fractions[0] > 0.1, $"{style}: the dark end should be inked, got {fractions[0]:F2}");
        Assert.True(fractions[^1] < 0.06, $"{style}: the light end should be nearly bare, got {fractions[^1]:F2}");

        for (int band = 1; band < bands; band++)
        {
            Assert.True(fractions[band] <= fractions[band - 1] + 0.04,
                $"{style}: ink should fall from dark to light, got {string.Join(' ', fractions.Select(f => f.ToString("F2")))}");
            Assert.True(fractions[band - 1] - fractions[band] < 0.12,
                $"{style}: band {band} drops {fractions[band - 1] - fractions[band]:F2}, which is a cutoff rather " +
                $"than a gradient: {string.Join(' ', fractions.Select(f => f.ToString("F2")))}");
        }
    }

    [Theory]
    [InlineData(FillStyle.Shade)]
    [InlineData(FillStyle.Stipple)]
    [InlineData(FillStyle.Scribble)]
    public void An_Image_Tone_Inks_The_Dark_Side_And_Leaves_The_Light_Side(FillStyle style)
    {
        var points = Ink(style, Ramp());
        Assert.NotEmpty(points);

        // The ramp is dark on the left, so nearly all the ink belongs there.
        int dark = points.Count(p => p.X < 100);
        int light = points.Count(p => p.X > 100);

        Assert.True(dark > 10, $"{style}: expected ink on the dark side, got {dark}");
        Assert.True(dark > light * 5, $"{style}: ink should follow the tone: dark={dark} light={light}");
    }

    /// <summary>
    /// A line fill has no other tone source, so without a raster the boundary stands in and the ink hugs
    /// the outline. Only the fills behave this way: a generator deliberately ignores the boundary tone.
    /// </summary>
    [Fact]
    public void Without_An_Image_A_Line_Fill_Shades_By_The_Shape_Boundary()
    {
        var points = Ink(FillStyle.Shade, tone: null);
        Assert.NotEmpty(points);

        double DistanceToEdge(Vector p) => Math.Min(
            Math.Min(p.X - 20, 180 - p.X),
            Math.Min(p.Y - 20, 180 - p.Y));

        int nearEdge = points.Count(p => DistanceToEdge(p) < 40);
        Assert.True(nearEdge > points.Count * 0.9,
            $"boundary tone should keep nearly all ink within 40 units of the edge ({nearEdge} of {points.Count})");
    }

    /// <summary>
    /// A generator asked for on a shape means the whole shape. Gating its seeds on the boundary tone would
    /// ink a rim and leave the middle bare, which is not what "stipple this shape" means.
    /// </summary>
    [Theory]
    [InlineData(FillStyle.Stipple)]
    [InlineData(FillStyle.Scribble)]
    public void A_Generator_Without_An_Image_Covers_The_Whole_Shape(FillStyle style)
    {
        var points = Ink(style, tone: null);
        Assert.NotEmpty(points);

        // The middle fifth of the square, well away from every edge.
        int middle = points.Count(p => p.X is > 64 and < 136 && p.Y is > 64 and < 136);
        Assert.True(middle > 0, $"{style}: the middle of the shape should have ink, got {middle} points");
    }

    [Theory]
    [InlineData(FillStyle.Shade)]
    [InlineData(FillStyle.Stipple)]
    [InlineData(FillStyle.Scribble)]
    public void Neither_Tone_Fill_Leaves_The_Shape(FillStyle style)
    {
        foreach (var tone in new GrayImage?[] { Ramp(), null })
        {
            foreach (var point in Ink(style, tone))
            {
                Assert.True(point.X >= 20 - 1e-6 && point.X <= 180 + 1e-6
                            && point.Y >= 20 - 1e-6 && point.Y <= 180 + 1e-6,
                    $"{style}: ink at ({point.X:0.###},{point.Y:0.###}) outside the shape");
            }
        }
    }

    [Fact]
    public void A_Higher_Threshold_Inks_Less()
    {
        // Length, not point count: a run of inked steps is one segment however long it is, so counting
        // points would say two thresholds ink the same when one covers half the line and the other all of it.
        double low = InkLength(FillStyle.Shade, threshold: 0.3);
        double high = InkLength(FillStyle.Shade, threshold: 0.8);
        Assert.True(high < low * 0.9, $"a higher threshold should ink less: {high:0.#} vs {low:0.#}");
    }

    private static double InkLength(FillStyle style, double threshold)
    {
        double total = 0;
        foreach (var path in HatchEngine.Generate(Square().Paths, new HatchOptions
        {
            FillStyle = style, HatchSpacing = 4, HatchAngle = 0, Inset = false,
            ToneImage = Ramp(), ToneThreshold = threshold,
        })[0].Paths)
        {
            var points = Points(path).ToList();
            for (int i = 0; i + 1 < points.Count; i++) total += (points[i + 1] - points[i]).Length;
        }
        return total;
    }

    [Fact]
    public void Inverting_The_Tone_Swaps_Which_Side_Is_Inked()
    {
        var normal = HatchEngine.Generate(Square().Paths, new HatchOptions
        {
            FillStyle = FillStyle.Shade, HatchSpacing = 4, HatchAngle = 0, Inset = false,
            ToneImage = Ramp(), ToneThreshold = 0.5,
        })[0].Paths.SelectMany(Points).ToList();

        var inverted = HatchEngine.Generate(Square().Paths, new HatchOptions
        {
            FillStyle = FillStyle.Shade, HatchSpacing = 4, HatchAngle = 0, Inset = false,
            ToneImage = Ramp(), ToneThreshold = 0.5, ToneInvert = true,
        })[0].Paths.SelectMany(Points).ToList();

        Assert.True(normal.Count(p => p.X < 100) > normal.Count(p => p.X > 100),
            "the dark side should be inked without inversion");
        Assert.True(inverted.Count(p => p.X > 100) > inverted.Count(p => p.X < 100),
            "inversion should ink the light side");
    }
}
