using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// The geometry the fills share with each other and with
/// <see cref="HatchEngine.BuildPolygons"/>: the (optionally inset) clip polygon, the
/// per-angle hatch-line sweep, the post-connect safety clip, and the two ways of
/// turning geometry into <see cref="HatchPath"/>s.
/// </summary>
internal static class HatchGeometry
{
    /// <summary>Min hatch length as a fraction of spacing (eggbot_hatch's MinHatchFraction).</summary>
    public const double MinHatchFraction = 0.25;

    /// <summary>double-&gt;int64 scale (matches <see cref="HatchClipper"/>).</summary>
    public const double Scale = 1_000_000.0;

    /// <summary>The (optionally inset) even-odd clip polygon used by Parallel/Wave:
    /// the even-odd union of <paramref name="polygons"/>, shrunk inward by
    /// <paramref name="insetDist"/> when > 0, else the raw polygons. Null when the inset
    /// collapses the shape. Built once per element and shared by the hatch-line
    /// intersection and the post-connect safety clip.
    /// </summary>
    public static Paths64? BuildHatchClip(List<List<Vector>> polygons, double insetDist, double arcTol)
    {
        if (insetDist > 0)
        {
            // Straight from the polygons to the integer clip, with no PathsD copy in between: this runs
            // once per element and was three passes over the same vertices.
            //
            // Split into simple loops. A naive offset folds back wherever the shape is narrower than twice
            // the inset, and past that point the fold-backs stop describing the erosion: the even-odd fill
            // of the folded ring can reach outside the shape, which is how an inset ended up drawing
            // hatches beyond the outline. Splitting costs more but is the only way the region is right.
            var inset = RegionOffset.InflateRings(polygons, -insetDist, arcTol, split: true);
            if (inset.Count == 0) return null;
            var clip = new Paths64(inset.Count);
            foreach (var ring in inset)
            {
                var p64 = new Path64(ring.Length);
                foreach (var point in ring)
                    p64.Add(new Point64((long)Math.Round(point.X * Scale), (long)Math.Round(point.Y * Scale)));
                clip.Add(p64);
            }
            return clip;
        }
        else
        {
            var clip = HatchClipper.ToPaths64(polygons);
            return clip.Count == 0 ? null : clip;
        }
    }

    /// <summary>Even-odd union of the element polygons, pulled inward by
    /// <paramref name="insetDist"/> when &gt; 0. Shared by the curve-clipping fills
    /// (spiral/hilbert) so they honour the same inset as the parallel/concentric fills.</summary>
    public static PathsD InsetPolys(List<List<Vector>> polygons, double insetDist, double arcTol)
    {
        var rings = RegionOffset.InflateRings(polygons, -insetDist, arcTol, split: false);
        var result = new PathsD(rings.Count);
        foreach (var source in rings)
        {
            var path = new PathD(source.Length);
            foreach (var point in source) path.Add(new PointD(point.X, point.Y));
            result.Add(path);
        }
        return result;
    }

    /// <summary>
    /// The same clip polygon <see cref="BuildHatchClip"/> produces, as a region a generator can ask questions
    /// of. Generators need "am I inside", not "here are the polygons".
    /// </summary>
    internal static EvenOddRegion? BuildHatchRegion(List<List<Vector>> polygons, double insetDist, double arcTol)
    {
        var clip = BuildHatchClip(polygons, insetDist, arcTol);
        return clip is null ? null : HatchClipper.RegionOf(clip);
    }

    /// <summary>
    /// Build the hatch grid for the given angle and return the within-polygon hatch segments.
    /// </summary>
    public static List<(Vector A, Vector B)> HatchPolygonSet(HatchBBox bbox, HatchOptions opts, double angle, Paths64 clip)
        => HatchSegmentList(bbox, opts, angle, clip, opts.HatchSpacing);

    /// <summary>
    /// The hatch segments at an explicit spacing, so a style can run more than one screen over the
    /// same shape. The moiré fill uses two pitches to make the two families beat against each other.
    /// </summary>
    public static List<(Vector A, Vector B)> HatchSegmentList(HatchBBox bbox, HatchOptions opts,
        double angle, Paths64 clip, double spacing)
    {
        spacing = Math.Abs(spacing);
        if (spacing < 0.1) spacing = 0.1;

        double width = bbox.Xmax - bbox.Xmin;
        double height = bbox.Ymax - bbox.Ymin;
        double radius = Math.Sqrt(width * width + height * height) / 2.0;
        double cx = bbox.Xmin + width / 2.0;
        double cy = bbox.Ymin + height / 2.0;

        // Rotation: hatch-line direction d = (-sa, ca), perpendicular n = (ca, sa).
        double rad = (90.0 - angle) * Math.PI / 180.0;
        double cosA = Math.Cos(rad), sinA = Math.Sin(rad);

        // Generate hatch lines (original frame).
        var hatchLines = new List<(Vector A, Vector B)>();
        for (double offset = -radius; offset <= radius + 1e-9; offset += spacing)
        {
            double x1 = cx + offset * cosA + radius * sinA;
            double y1 = cy + offset * sinA - radius * cosA;
            double x2 = cx + offset * cosA - radius * sinA;
            double y2 = cy + offset * sinA + radius * cosA;
            // Drop lines entirely outside the bounding box.
            if ((x1 < bbox.Xmin && x2 < bbox.Xmin) || (x1 > bbox.Xmax && x2 > bbox.Xmax)) continue;
            if ((y1 < bbox.Ymin && y2 < bbox.Ymin) || (y1 > bbox.Ymax && y2 > bbox.Ymax)) continue;
            hatchLines.Add((new Vector(x1, y1), new Vector(x2, y2)));
        }
        if (hatchLines.Count == 0) return [];

        // One sweep: every line in the batch is parallel, so they are clipped together against the
        // even-odd region, which cannot let a segment escape the (optionally inset) outline.
        double minAllowed = spacing * MinHatchFraction;
        var segments = HatchClipper.ClipParallel(clip, hatchLines, Scale, opts.MaxParallelism);
        segments.RemoveAll(segment => Vector.Distance(segment.A, segment.B) < minAllowed);
        return segments;
    }

    /// <summary>
    /// Clip every hatch path (line + cubic-Bézier connector) to the polygon interior so
    /// the pen can never leave the outline. Used by Parallel after connect-ends joins
    /// segments with Béziers that can bow across a concave boundary by a fraction of
    /// mm. Paths are flattened to polylines for the clip (the compiler flattens Béziers
    /// for G-code anyway). Batched: all paths are one set of open subjects in a single
    /// Clipper intersection (was one Execute per path).
    /// </summary>
    public static void ClipPathsToPolygon(List<HatchPath> paths, Paths64 clip, double flattenTol, int maxParallelism = -1)
    {
        if (paths.Count == 0 || clip.Count == 0) return;

        var rings = new List<Vector[]>(clip.Count);
        foreach (var ring in clip)
        {
            if (ring.Count < 3) continue;
            var points = new Vector[ring.Count];
            for (int i = 0; i < ring.Count; i++) points[i] = new Vector(ring[i].X, ring[i].Y);
            rings.Add(points);
        }
        if (rings.Count == 0) return;

        var region = new EvenOddRegion(rings, indexed: true);

        // Kept sequential. Parallelising this was measured on a loaded machine and showed nothing
        // either way, and it runs inside HatchEngine's per-element Parallel.For, so nesting it is a
        // coin flip on whether the pool gives it cores. See the note in BenchProgram about needing the
        // calibration ratio before believing a difference this small.
        var result = new List<HatchPath>(paths.Count);
        var shared = region.NewQuery();
        foreach (var hatchPath in paths) ClipOne(hatchPath, region, shared, flattenTol, result);

        paths.Clear();
        paths.AddRange(result);
    }

    /// <summary>Clip one connected path into <paramref name="result"/>, splitting it only where it must.</summary>
    private static void ClipOne(HatchPath hatchPath, EvenOddRegion region, EvenOddRegion.Query query,
        double flattenTol, List<HatchPath> result)
    {
        var flattened = new List<Vector>(32);
        var current = new HatchPath { Start = hatchPath.Start };
        Vector pen = hatchPath.Start;
        bool pending = false;   // true when `current` holds at least one op

        void Keep(Vector point)
        {
            current.Ops.Add(new LineTo(point));
            pending = true;
        }

        void Flush()
        {
            if (pending) result.Add(current);
            pending = false;
        }

        foreach (var op in hatchPath.Ops)
        {
            if (op is LineTo line)
            {
                // A straight hatch run came out of the clip, and the connect step only trims it
                // along its own line, so it is inside. Re-clipping it is what made this stage
                // cost more than the whole rest of the hatch on a large drawing.
                Keep(line.P);
                pen = line.P;
                continue;
            }

            if (op is not CubicTo cubic) continue;

            flattened.Clear();
            flattened.Add(pen);
            new CubicBezier(pen, cubic.C1, cubic.C2, cubic.P).Flatten(flattened, flattenTol);

            // The usual case: the connector bows but stays inside, so it only needs flattening.
            // The end points are skipped: they sit on the clipped hatch runs, which is the boundary
            // itself, and a strict point test calls a boundary point outside.
            bool escapes = false;
            for (int i = 1; i < flattened.Count - 1; i++)
            {
                if (query.Contains(flattened[i])) continue;
                escapes = true;
                break;
            }

            if (!escapes)
            {
                for (int i = 1; i < flattened.Count; i++) Keep(flattened[i]);
                pen = cubic.P;
                continue;
            }

            // The connector bows outside. Cutting it out would leave a gap, and a gap is a pen
            // lift -- the very thing connect-ends exists to remove. Both ends of the connector
            // are inside the region by construction, so the straight chord between them is the
            // cheap way to stay in: no clipping, no split, and no lost piece. The safety nets in
            // the hatch tests check that the pen still stays inside the outline.
            Keep(cubic.P);
            pen = cubic.P;
        }

        Flush();
    }

    /// <summary>Emit each segment as its own pen path, alternating direction (matches the non-connecting original).</summary>
    public static void EmitAlternating(List<HatchPath> paths, List<(Vector A, Vector B)> segments)
    {
        bool dir = true;
        foreach (var (start, end) in segments)
        {
            var path = new HatchPath { Start = dir ? start : end };
            path.Ops.Add(new LineTo(dir ? end : start));
            paths.Add(path);
            dir = !dir;
        }
    }

    /// <summary>
    /// Ink the parts of a straight segment where <paramref name="keep"/> holds, as maximal runs.
    /// </summary>
    /// <remarks>
    /// The obvious gate -- keep or drop the whole segment by the tone at its midpoint -- is wrong for a
    /// line that spans the shape: its midpoint is its most interior point, so a line running from one dark
    /// edge to the other is judged on the one place it is guaranteed to be light. An engraver breaks the
    /// stroke instead, and so does this: walk it in short steps and draw only the steps that qualify.
    ///
    /// A run of qualifying steps is straight, so it contributes its far end when it closes. Emitting a
    /// point per step would turn one line into a hundred tiny moves.
    /// </remarks>
    /// <summary>
    /// How much ink this tone asks for, as a fraction: 0 for paper-white and 1 for black.
    /// </summary>
    /// <remarks>
    /// The threshold is where ink starts, not a cliff to fall off. Comparing a tone to it directly gives a
    /// hard edge -- every part of a gradient on one side inked, every part on the other bare -- which is what
    /// a photograph's mid-tones look like when they are thresholded rather than shaded. Rescaling the band
    /// above the threshold to 0..1 keeps the threshold as "this is paper" and makes everything darker than it
    /// a matter of degree, which <see cref="Dither"/> then turns into a density.
    /// </remarks>
    internal static double Level(double tone, double threshold)
    {
        double start = Math.Clamp(threshold, 0, 0.999);
        return Math.Clamp((tone - start) / (1 - start), 0, 1);
    }

    /// <summary>
    /// A stable threshold in [0,1) for one cell of the drawing, so a level can be turned into a density.
    /// </summary>
    /// <remarks>
    /// This is ordered dithering: a cell is inked when its level exceeds the cell's own threshold, so a level
    /// of 0.5 inks half the cells and a level of 0.25 a quarter, spread evenly instead of clumped. Hashing the
    /// quantised position (rather than walking a fixed grid pattern) keeps the pattern from lining up with the
    /// hatch and stays put when the settings change, because the seed comes from the options.
    /// </remarks>
    internal static double Dither(Vector p, double cell, uint seed = 0x9E3779B9u)
    {
        double size = Math.Max(cell, 1e-6);
        unchecked
        {
            uint x = (uint)(long)Math.Floor(p.X / size);
            uint y = (uint)(long)Math.Floor(p.Y / size);
            uint h = seed ^ (x * 73856093u) ^ (y * 19349663u);
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216.0;
        }
    }

    /// <summary>As <see cref="EmitGraded"/>, deciding each step from the tone and the dither instead.</summary>
    internal static void EmitDensity(List<HatchPath> paths, (Vector A, Vector B) segment, double step,
        IToneField tone, double threshold, uint seed)
    {
        EmitGraded(paths, segment, step,
            point => Level(tone.Sample(point), threshold) > Dither(point, step, seed));
    }

    internal static void EmitGraded(List<HatchPath> paths, (Vector A, Vector B) segment, double step,
        Func<Vector, bool> keep)
    {
        Vector a = segment.A, b = segment.B;
        double length = (b - a).Length;
        if (length <= 0) return;

        int parts = Math.Max(1, (int)Math.Ceiling(length / Math.Max(step, 1e-6)));
        HatchPath? run = null;
        Vector runEnd = a;

        for (int i = 0; i < parts; i++)
        {
            double t0 = (double)i / parts, t1 = (double)(i + 1) / parts;
            var middle = a + (b - a) * ((t0 + t1) * 0.5);

            if (keep(middle))
            {
                if (run is null) run = new HatchPath { Start = a + (b - a) * t0 };
                runEnd = a + (b - a) * t1;
            }
            else if (run is not null)
            {
                run.Ops.Add(new LineTo(runEnd));
                paths.Add(run);
                run = null;
            }
        }

        if (run is not null)
        {
            run.Ops.Add(new LineTo(runEnd));
            paths.Add(run);
        }
    }

    /// <summary>Append one polyline as a single continuous <see cref="HatchPath"/>.</summary>
    public static void AddPolylinePath(List<HatchPath> paths, List<Vector> poly)
    {
        if (poly.Count < 2) return;
        var hatchPath = new HatchPath { Start = poly[0] };
        for (int point = 1; point < poly.Count; point++)
            hatchPath.Ops.Add(new LineTo(poly[point]));
        paths.Add(hatchPath);
    }
}
