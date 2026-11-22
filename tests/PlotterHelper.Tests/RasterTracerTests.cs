using PlotterHelper.Trace;
using Xunit;

namespace PlotterHelper.Tests;

/// <summary>
/// End-to-end tracer tests on rasters drawn in code, so the expected geometry is known exactly.
/// These describe what the tracer is for: a line has to come back as ONE stroke running from its
/// beginning to its end, not as a pair of outlines or a pile of fragments.
///
/// The traced SVG is in pixels of the upscaled raster, so expected coordinates are written through
/// <see cref="Trace"/> to convert from the source pixels the drawings are made in.
/// </summary>
public class RasterTracerTests
{
    private const int Up = 3;

    private static double Trace(double sourcePixels) => sourcePixels * Up;

    private static GrayImage Blank(int width, int height)
    {
        var image = new GrayImage(width, height);
        for (int i = 0; i < image.Data.Length; i++) image.Data[i] = 255;
        return image;
    }

    /// <summary>Solid ink bar spanning x0..x1 on rows y0..y1.</summary>
    private static void Bar(GrayImage image, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                image[x, y] = 0;
    }

    private static void Disc(GrayImage image, double centerX, double centerY, double radius)
    {
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                double offsetX = x + 0.5 - centerX, offsetY = y + 0.5 - centerY;
                if (offsetX * offsetX + offsetY * offsetY <= radius * radius) image[x, y] = 0;
            }
    }

    private static List<TracePath> Strokes(TraceResult result) => TraceInspector.Strokes(result.Svg);

    [Fact]
    public void AThinLineBecomesOneStrokeNotTwoOutlines()
    {
        var image = Blank(120, 40);
        Bar(image, 10, 19, 109, 21); // a 3px horizontal line

        var result = RasterTracer.Trace(image, new TraceOptions { Upscale = Up });
        var strokes = Strokes(result);

        Assert.Equal(0, result.RegionCount);
        Assert.Single(strokes);
        var stroke = strokes[0];
        // One path, running the whole length of the line, staying near its middle. A region
        // tracer would have produced two parallel outlines instead.
        Assert.InRange(stroke.MinX, Trace(9), Trace(12));
        Assert.InRange(stroke.MaxX, Trace(107), Trace(111));
        Assert.InRange(stroke.MinY, Trace(18), Trace(22));
        Assert.InRange(stroke.MaxY, Trace(18), Trace(22));
    }

    [Fact]
    public void ALineChoppedByTheThresholdComesBackAsOneStroke()
    {
        var image = Blank(160, 40);
        Bar(image, 10, 19, 74, 21);
        Bar(image, 84, 19, 149, 21); // a 10px gap in the middle

        // Too small a join gap leaves the two halves alone; a large enough one fuses them.
        Assert.Equal(2, Strokes(RasterTracer.Trace(image, new TraceOptions { Upscale = Up, JoinGap = 1 })).Count);

        var bridged = Strokes(RasterTracer.Trace(image, new TraceOptions { Upscale = Up, JoinGap = 16 }));
        Assert.Single(bridged);
        Assert.InRange(bridged[0].MinX, Trace(9), Trace(12));
        Assert.InRange(bridged[0].MaxX, Trace(147), Trace(151));
    }

    [Fact]
    public void ALineCrossingAnotherIsCoveredEndToEnd()
    {
        var image = Blank(200, 200);
        Bar(image, 10, 98, 189, 101); // horizontal
        Bar(image, 98, 10, 101, 189); // vertical

        var result = RasterTracer.Trace(image, new TraceOptions { Upscale = Up });
        Assert.Equal(0, result.RegionCount);

        // Two lines, so at most four strokes; whichever way the crossing was resolved, both lines
        // must be covered end to end so the pen never jumps across the drawing.
        var strokes = Strokes(result);
        Assert.InRange(strokes.Count, 2, 4);
        Assert.InRange(strokes.Min(stroke => stroke.MinX), Trace(9), Trace(12));
        Assert.InRange(strokes.Max(stroke => stroke.MaxX), Trace(187), Trace(191));
        Assert.InRange(strokes.Min(stroke => stroke.MinY), Trace(9), Trace(12));
        Assert.InRange(strokes.Max(stroke => stroke.MaxY), Trace(187), Trace(191));
    }

    [Fact]
    public void ALineCrossingAnotherIsSplicedIntoOneStroke()
    {
        var image = Blank(200, 200);
        Bar(image, 10, 98, 189, 101);
        Bar(image, 98, 10, 101, 189);

        // A clean perpendicular crossing has no ambiguity, so each line should come out whole.
        var strokes = Strokes(RasterTracer.Trace(image, new TraceOptions { Upscale = Up }));
        Assert.Equal(2, strokes.Count);

        var horizontal = strokes.Single(stroke => stroke.MaxX - stroke.MinX > Trace(100));
        var vertical = strokes.Single(stroke => stroke.MaxY - stroke.MinY > Trace(100));
        Assert.InRange(horizontal.MinX, Trace(9), Trace(12));
        Assert.InRange(horizontal.MaxX, Trace(187), Trace(191));
        Assert.InRange(vertical.MinY, Trace(9), Trace(12));
        Assert.InRange(vertical.MaxY, Trace(187), Trace(191));
    }

    [Fact]
    public void ASolidShapeBecomesAHatchableRegionNotACenterline()
    {
        var image = Blank(200, 200);
        Disc(image, 60, 60, 40);       // far too wide to be a pen stroke
        Bar(image, 130, 58, 190, 61); // and an ordinary line beside it

        var result = RasterTracer.Trace(image, new TraceOptions { Upscale = Up });

        Assert.Equal(1, result.RegionCount);
        Assert.Equal(1, result.StrokeCount);

        var region = TraceInspector.Paths(result.Svg).Single(path => path.IsRegion);
        Assert.True(region.IsClosed, "a region must be a closed path or the hatch fill cannot use it");
        Assert.InRange(region.MinX, Trace(19), Trace(21));
        Assert.InRange(region.MaxX, Trace(99), Trace(101));
        Assert.InRange(region.MinY, Trace(19), Trace(21));
        Assert.InRange(region.MaxY, Trace(99), Trace(101));
    }

    [Fact]
    public void AShortTickTouchingAThickShapeSurvivesPruning()
    {
        var image = Blank(200, 200);
        Disc(image, 100, 60, 30);
        Bar(image, 99, 92, 101, 120); // a deliberate 28px tick hanging below it

        var result = RasterTracer.Trace(image, new TraceOptions { Upscale = Up, MinSpur = 3 });

        // The tick is longer than the spur limit and does not taper, so it must still be drawn.
        Assert.Contains(Strokes(result), stroke => stroke.MaxY > Trace(110));
    }

    [Fact]
    public void AnEmptyRasterProducesNoGeometry()
    {
        var result = RasterTracer.Trace(Blank(60, 60), new TraceOptions());
        Assert.Equal(0, result.StrokeCount);
        Assert.Equal(0, result.RegionCount);
        Assert.DoesNotContain("<path", result.Svg);
    }
}
