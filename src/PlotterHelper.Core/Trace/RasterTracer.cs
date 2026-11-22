using System.Globalization;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;

namespace PlotterHelper.Trace;

/// <summary>What one trace produced: an SVG plus the diagnostics the CLI reports.</summary>
public sealed class TraceResult
{
    public required string Svg { get; init; }
    public int TraceWidth { get; init; }
    public int TraceHeight { get; init; }
    public int StrokeCount { get; init; }
    public int RegionCount { get; init; }
    public double MeanStrokeWidth { get; init; }
    public double OutputWidth { get; init; }
    public double OutputHeight { get; init; }
}

/// <summary>
/// Raster line art to drawable SVG, by centerline rather than by outline. The order of
/// operations is the whole trick and none of it is optional: upscale, threshold locally,
/// despeckle, distance transform, thin, graph the skeleton, repair the graph, then fit curves.
/// Skipping the graph repair is what makes naive skeleton tracers produce a pile of broken
/// fragments instead of the lines a person sees.
/// </summary>
public static class RasterTracer
{
    public static TraceResult Trace(GrayImage source, TraceOptions options)
    {
        if (source.Width < 3 || source.Height < 3)
            throw new ArgumentException("source image is too small to trace", nameof(source));

        // Hold the trace inside a pixel budget: a 2048px scan at 3x is a 31-megapixel working
        // raster, which cost seconds and a gigabyte for no visible gain. Never below 1x, since
        // downscaling would throw away exactly the thin lines this stage exists to keep.
        double scale = Math.Clamp(options.Upscale, 1, 8);
        long sourcePixels = (long)source.Width * source.Height;
        if (options.MaxPixels > 0 && sourcePixels > options.MaxPixels)
            scale = 1.0;
        else if (options.MaxPixels > 0)
            scale = Math.Min(scale, Math.Sqrt(options.MaxPixels / (double)sourcePixels));

        int traceWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        int traceHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
        var gray = traceWidth != source.Width || traceHeight != source.Height
            ? source.Resample(traceWidth, traceHeight, options.MaxParallelism)
            : source;
        int width = gray.Width, height = gray.Height;

        int window = Math.Max(3, (int)Math.Round(options.ThresholdWindow * scale));
        if (window % 2 == 0) window++;
        var mask = Binarizer.Threshold(gray, window, options.ThresholdK, options.GlobalThreshold, options.MaxParallelism);
        if (options.Invert) Binarizer.Invert(mask);
        var label = Binarizer.DespeckleAndLabel(mask, width, height, options.MinComponentArea * scale * scale, out _);

        var padded = Binarizer.Pad(mask, width, height, false, out int paddedWidth, out int paddedHeight);
        var paddedLabel = Binarizer.Pad(label, width, height, 0, out _, out _);

        var distance = DistanceTransform.Compute(padded, paddedWidth, paddedHeight, options.MaxParallelism);
        var skeleton = Thinning.Skeletonize(padded, paddedWidth, paddedHeight, options.MaxParallelism);
        var graph = SkeletonGraph.Build(skeleton, paddedWidth, paddedHeight, distance, options.MaxParallelism);
        GraphSurgery.Clean(graph, padded, paddedWidth, paddedHeight, options, scale);

        var (edgeComponent, componentWidth) = MeasureComponents(graph, padded, paddedLabel, distance, paddedWidth, paddedHeight);
        double thickLimit = options.ThickWidth > 0 ? options.ThickWidth * scale : double.PositiveInfinity;
        var regionComponents = new HashSet<int>();
        foreach (var (id, thickness) in componentWidth)
            if (thickness > thickLimit) regionComponents.Add(id);

        var output = Emit(graph, edgeComponent, regionComponents, label, width, height, options, scale);

        double outputWidth = options.TargetWidth > 0 ? options.TargetWidth : 200;
        double outputHeight = options.TargetHeight > 0
            ? options.TargetHeight
            : outputWidth * height / (double)width;
        string units = options.OutputUnits is "mm" or "in" ? options.OutputUnits : "mm";
        string svg = TraceSvgWriter.Write(output.Paths, width, height, outputWidth, outputHeight, units);

        return new TraceResult
        {
            Svg = svg,
            TraceWidth = width,
            TraceHeight = height,
            StrokeCount = output.Strokes,
            RegionCount = output.Regions,
            MeanStrokeWidth = output.Strokes > 0 ? output.StrokeWidthSum / output.Strokes / scale : 0,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
        };
    }

    /// <summary>
    /// Which mask component each edge sits on, and how thick every component is.
    ///
    /// Width is measured over the component's own pixels rather than over its skeleton: a solid
    /// disc thins to a point and has no edges at all, so classifying from edges alone makes the
    /// whole shape silently disappear instead of being handed to the hatch fill. Averaging the
    /// distance transform over the pixels gives the mean thickness directly.
    /// </summary>
    private static (int[] EdgeComponent, Dictionary<int, double> ComponentWidth) MeasureComponents(
        SkeletonGraph graph, bool[] padded, int[] paddedLabel, double[] distance, int paddedWidth, int paddedHeight)
    {
        var widthSum = new Dictionary<int, double>();
        var pixelCount = new Dictionary<int, double>();
        for (int i = 0; i < padded.Length; i++)
        {
            if (!padded[i]) continue;
            int id = paddedLabel[i];
            if (id <= 0) continue;
            widthSum[id] = widthSum.GetValueOrDefault(id) + distance[i];
            pixelCount[id] = pixelCount.GetValueOrDefault(id) + 1;
        }

        var componentWidth = new Dictionary<int, double>();
        foreach (var (id, sum) in widthSum) componentWidth[id] = 2.0 * sum / pixelCount[id];

        var edgeComponent = new int[graph.Edges.Count];
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            edgeComponent[i] = -1;
            var edge = graph.Edges[i];
            if (edge.Removed || edge.Points.Count == 0) continue;
            var middle = edge.Points[edge.Points.Count / 2];
            int midX = (int)Math.Round(middle.X), midY = (int)Math.Round(middle.Y);
            if (midX < 0 || midY < 0 || midX >= paddedWidth || midY >= paddedHeight) continue;
            edgeComponent[i] = paddedLabel[midY * paddedWidth + midX];
        }

        return (edgeComponent, componentWidth);
    }

    private readonly record struct Emitted(List<SvgPath> Paths, int Strokes, int Regions, double StrokeWidthSum);

    private static Emitted Emit(SkeletonGraph graph, int[] edgeComponent, HashSet<int> regionComponents,
        int[] label, int width, int height, TraceOptions options, double scale)
    {
        var paths = new List<SvgPath>();
        int strokes = 0, regions = 0;
        double strokeWidthSum = 0;
        double minLength = options.MinLength * scale;

        // Curve fitting is the expensive half of this loop and each edge is independent, so fit
        // them in parallel and then assemble the paths in edge order to keep the SVG reproducible.
        var fitting = new List<int>();
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.Removed || edge.Length < minLength) continue;
            int id = edgeComponent[i];
            if (id <= 0 || regionComponents.Contains(id)) continue;
            fitting.Add(i);
        }

        var fitted = new List<Curve>[fitting.Count];
        int workers = Parallelism.Workers(options.MaxParallelism, fitting.Count);
        if (workers > 1)
        {
            Parallel.For(0, fitting.Count, Parallelism.Options(workers),
                slot => fitted[slot] = FitOpen(graph.Edges[fitting[slot]].Points, options, scale));
        }
        else
        {
            for (int slot = 0; slot < fitting.Count; slot++)
                fitted[slot] = FitOpen(graph.Edges[fitting[slot]].Points, options, scale);
        }

        for (int slot = 0; slot < fitting.Count; slot++)
        {
            var edge = graph.Edges[fitting[slot]];
            if (fitted[slot].Count == 0) continue;
            var path = new SvgPath { Stroke = "#000000" };
            path.Curves.AddRange(Shift(fitted[slot], -1, -1));
            path.StrokeWidth = (2 * edge.Radius).ToString("0.###", CultureInfo.InvariantCulture);
            paths.Add(path);
            strokes++;
            strokeWidthSum += 2 * edge.Radius;
        }

        if (regionComponents.Count > 0)
        {
            var bounds = ComponentBounds(label, width, height, regionComponents);
            foreach (var (id, box) in bounds)
            {
                int boxWidth = box.Right - box.Left + 1, boxHeight = box.Bottom - box.Top + 1;
                var component = new bool[boxWidth * boxHeight];
                for (int y = 0; y < boxHeight; y++)
                    for (int x = 0; x < boxWidth; x++)
                        component[y * boxWidth + x] = label[(box.Top + y) * width + box.Left + x] == id;

                var path = new SvgPath { Fill = "#000000", Stroke = "#000000" };
                foreach (var loop in MarchingSquares.Trace(component, boxWidth, boxHeight, 0, 0, boxWidth, boxHeight))
                {
                    var curves = FitClosed(loop, options, scale);
                    path.Curves.AddRange(Shift(curves, box.Left, box.Top));
                }
                if (path.Curves.Count == 0) continue;
                paths.Add(path);
                regions++;
            }
        }

        return new Emitted(paths, strokes, regions, strokeWidthSum);
    }

    private static Dictionary<int, (int Left, int Top, int Right, int Bottom)> ComponentBounds(
        int[] label, int width, int height, HashSet<int> wanted)
    {
        var bounds = new Dictionary<int, (int, int, int, int)>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int id = label[y * width + x];
                if (id <= 0 || !wanted.Contains(id)) continue;
                if (bounds.TryGetValue(id, out var box))
                    bounds[id] = (Math.Min(box.Item1, x), Math.Min(box.Item2, y), Math.Max(box.Item3, x), Math.Max(box.Item4, y));
                else
                    bounds[id] = (x, y, x, y);
            }
        }
        return bounds;
    }

    private static List<Curve> FitOpen(IReadOnlyList<Vector> points, TraceOptions options, double scale)
    {
        var smoothed = CurveFitter.Smooth(points, options.Smooth);
        var simplified = CurveFitter.Simplify(smoothed, options.Simplify * scale);
        return simplified.Count < 2 ? new List<Curve>() : CurveFitter.Fit(simplified, options.FitError * scale);
    }

    /// <summary>
    /// Fit a closed contour. Starting the ring at its straightest vertex puts the seam where the
    /// curve is least bent, so the discontinuity between the fit's first and last tangent lands
    /// on a straight run instead of kinking a corner.
    /// </summary>
    private static List<Curve> FitClosed(IReadOnlyList<Vector> loop, TraceOptions options, double scale)
    {
        var ring = new List<Vector>(loop);
        if (ring.Count < 3) return new List<Curve>();
        if (ring[0] == ring[^1]) ring.RemoveAt(ring.Count - 1);

        int best = 0;
        double straightest = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            Vector previous = ring[(i - 1 + ring.Count) % ring.Count];
            Vector next = ring[(i + 1) % ring.Count];
            Vector incoming = ring[i] - previous, outgoing = next - ring[i];
            if (incoming.Length < 1e-9 || outgoing.Length < 1e-9) continue;
            double turn = 1 - Vector.Dot(incoming / incoming.Length, outgoing / outgoing.Length);
            if (turn < straightest) { straightest = turn; best = i; }
        }

        var rotated = new List<Vector>(ring.Count + 1);
        for (int i = 0; i < ring.Count; i++) rotated.Add(ring[(best + i) % ring.Count]);
        rotated.Add(rotated[0]);

        var smoothed = CurveFitter.Smooth(rotated, options.Smooth);
        smoothed[0] = rotated[0];
        smoothed[^1] = rotated[0];
        var simplified = CurveFitter.Simplify(smoothed, options.Simplify * scale);
        if (simplified.Count < 3) return new List<Curve>();
        simplified[0] = rotated[0];
        simplified[^1] = rotated[0];
        return CurveFitter.Fit(simplified, options.FitError * scale);
    }

    private static List<Curve> Shift(IReadOnlyList<Curve> curves, double shiftX, double shiftY)
    {
        var shifted = new List<Curve>(curves.Count);
        var offset = new Vector(shiftX, shiftY);
        foreach (var curve in curves)
        {
            shifted.Add(curve switch
            {
                Line line => new Line(line.Start + offset, line.End + offset),
                CubicBezier cubic => new CubicBezier(cubic.Start + offset, cubic.C1 + offset, cubic.C2 + offset, cubic.End + offset),
                QuadraticBezier quadratic => new QuadraticBezier(quadratic.Start + offset, quadratic.C1 + offset, quadratic.End + offset),
                _ => curve,
            });
        }
        return shifted;
    }
}
