using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using PlotterHelper.Cli;
using PlotterHelper.Cli.Pens;

namespace PlotterHelper.Cli.Features;

/// <summary>
/// Panels a drawing larger than the bed into a grid of tiles, emitting one
/// standalone G-code file per tile (each with its own header/footer/passes).
/// Strokes are flattened to polylines and clipped to each tile rectangle via
/// Clipper2 open-path intersection (a tile is just a closed rectangle polygon),
/// so the visible stroke segments inside each tile are preserved exactly.
///
/// Optional alignment marks (a small L at each tile corner) let the operator
/// register the page on the plotter. Optional tile overlap redraws the seam on
/// adjoining tiles so pages can be aligned/trimmed. Per-color grouping and the
/// M0 pen-swap pause apply within each tile.
/// </summary>
internal static class Tiling
{
    internal readonly struct FlatPath
    {
        public readonly List<Vector> Points;
        /// <summary>The pen color for this polyline: an outline's stroke, or a hatch's fill.</summary>
        public readonly string? Stroke;
        public FlatPath(List<Vector> points, string? stroke) { Points = points; Stroke = stroke; }
    }

    /// <summary>Flatten a document's outline paths into colored polylines.</summary>
    internal static List<FlatPath> FlattenOutlines(SvgDocument doc, double tolerance)
    {
        var flat = new List<FlatPath>();
        foreach (var path in doc.Paths)
        {
            foreach (var curve in path.Curves)
            {
                var chain = new LineSegmentChain();
                chain.AppendFlattened(curve, tolerance);
                if (chain.Count >= 2)
                    flat.Add(new FlatPath(chain.Points, path.Stroke));
            }
        }
        return flat;
    }

    /// <summary>Flatten hatch groups into colored polylines (color = source element's fill).
    /// When <see cref="Tiling.Emit"/> groups by color, a hatch whose fill matches an
    /// outline's stroke joins the same pen group so the operator never swaps pens
    /// mid-color.</summary>
    internal static List<FlatPath> FlattenHatches(List<HatchGroup> groups, double tolerance)
    {
        var flat = new List<FlatPath>();
        foreach (var group in groups)
        {
            foreach (var curve in HatchFlattener.ToCurves(new[] { group }))
            {
                var chain = new LineSegmentChain();
                chain.AppendFlattened(curve, tolerance);
                if (chain.Count >= 2)
                    flat.Add(new FlatPath(chain.Points, group.Source.Fill));
            }
        }
        return flat;
    }

    internal static bool Enabled(Options opts) =>
        opts.Gcode != null && opts.TileWidth > 0 && opts.TileHeight > 0;

    /// <summary>
    /// Tile the given flattened paths and write one G-code file per tile. Returns
    /// the number of tiles written (0 if the drawing is empty).
    /// </summary>
    internal static int Run(Options opts, List<FlatPath> paths)
    {
        if (paths.Count == 0) return 0;

        // Drawing bounding box (in output units).
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var flatPath in paths)
            foreach (var point in flatPath.Points)
            {
                if (point.X < minX) minX = point.X; if (point.X > maxX) maxX = point.X;
                if (point.Y < minY) minY = point.Y; if (point.Y > maxY) maxY = point.Y;
            }
        if (!(maxX > minX && maxY > minY)) return 0;

        double tileW = opts.TileWidth, tileH = opts.TileHeight;
        double overlap = Math.Min(Math.Max(opts.TileOverlap, 0), Math.Min(tileW, tileH) * 0.5);
        double stepX = tileW - overlap, stepY = tileH - overlap;
        int cols = (int)Math.Ceiling((maxX - minX) / stepX);
        int rows = (int)Math.Ceiling((maxY - minY) / stepY);
        if (cols < 1) cols = 1; if (rows < 1) rows = 1;

        var dir = Path.GetDirectoryName(opts.Gcode) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(opts.Gcode) ?? "";

        int written = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                double x0 = minX + col * stepX, y0 = minY + row * stepY;
                double x1 = x0 + tileW, y1 = y0 + tileH;
                // Expand the clip rect by a small epsilon so strokes lying exactly on a
                // tile seam (e.g. the drawing's bbox-min edge, which coincides with the
                // first tile origin) are included rather than dropped by Clipper as
                // degenerate on-boundary segments. Redraws seam strokes on both adjoining
                // tiles (minor duplication, no gaps) -- same idea as overlap.
                const double eps = 0.5;
                var rect = new List<Vector>
                {
                    new(x0 - eps, y0 - eps), new(x1 + eps, y0 - eps),
                    new(x1 + eps, y1 + eps), new(x0 - eps, y1 + eps),
                };
                var clip = new Paths64 { HatchClipper.ToPath64(rect) };

                var compiler = EngineBuilder.BuildCompiler(opts);
                var clipped = new List<FlatPath>();
                foreach (var flatPath in paths)
                {
                    foreach (var sub in HatchClipper.ClipOpenToPolygon(clip, flatPath.Points))
                        if (sub.Count >= 2) clipped.Add(new FlatPath(sub, flatPath.Stroke));
                }

                if (opts.TileAlignmentMarks)
                    clipped.InsertRange(0, AlignmentMarks(x0, y0, x1, y1, opts.TileMarkSize));

                Emit(compiler, clipped, opts);

                string file = Path.Combine(dir, $"{stem}_tile_{row}_{col}.gcode");
                File.WriteAllText(file, compiler.Compile(opts.Passes));
                written++;
                if (opts.Progress)
                    Progress.Report(opts, 8 + 91 * written / (rows * cols), $"Tile {written}/{rows * cols}");
            }
        }
        return written;
    }

    /// <summary>Emit the (already-clipped) paths to the compiler, optionally grouped by color with pauses.</summary>
    private static void Emit(GcodeCompiler compiler, List<FlatPath> paths, Options opts)
    {
        if (paths.Count == 0) return;
        if (!opts.GroupByColor)
        {
            foreach (var flatPath in paths) AppendChain(compiler, flatPath.Points);
            return;
        }

        // Group by color (outline stroke or hatch fill) in first-appearance order,
        // so a red outline and a red hatch run on the same pen without a swap.
        var order = new List<string>();
        var map = new Dictionary<string, List<FlatPath>>(StringComparer.OrdinalIgnoreCase);
        foreach (var flatPath in paths)
        {
            var key = ColorSort.NormalizeColor(flatPath.Stroke);
            if (!map.TryGetValue(key, out var list)) { list = new List<FlatPath>(); map[key] = list; order.Add(key); }
            list.Add(flatPath);
        }
        for (int group = 0; group < order.Count; group++)
        {
            compiler.AppendRaw($"; --- color: {order[group]} ---");
            foreach (var flatPath in map[order[group]]) AppendChain(compiler, flatPath.Points);
            if (opts.PauseBetweenColors && group < order.Count - 1)
                PenAnnounce.AppendPenSwap(opts, compiler, order[group + 1], group + 2, order.Count);
        }
    }

    private static void AppendChain(GcodeCompiler compiler, List<Vector> points)
    {
        var chain = new LineSegmentChain();
        foreach (var point in points) chain.Append(point);
        compiler.AppendLineChain(chain);
    }

    /// <summary>Four corner L-marks (drawn first) for page registration.</summary>
    private static List<FlatPath> AlignmentMarks(double x0, double y0, double x1, double y1, double markSize)
    {
        if (markSize <= 0) return new List<FlatPath>();
        return new List<FlatPath>
        {
            // bottom-left:  (x0+markSize,y0) -> (x0,y0) -> (x0,y0+markSize)
            new(new List<Vector> { new(x0+markSize, y0), new(x0, y0), new(x0, y0+markSize) }, null),
            // bottom-right: (x1-markSize,y0) -> (x1,y0) -> (x1,y0+markSize)
            new(new List<Vector> { new(x1-markSize, y0), new(x1, y0), new(x1, y0+markSize) }, null),
            // top-right:     (x1-markSize,y1) -> (x1,y1) -> (x1,y1-markSize)
            new(new List<Vector> { new(x1-markSize, y1), new(x1, y1), new(x1, y1-markSize) }, null),
            // top-left:      (x0+markSize,y1) -> (x0,y1) -> (x0,y1-markSize)
            new(new List<Vector> { new(x0+markSize, y1), new(x0, y1), new(x0, y1-markSize) }, null),
        };
    }
}