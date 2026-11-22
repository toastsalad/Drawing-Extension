using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Bridge between the project's geometry (<see cref="Vector"/>, <see cref="PathsD"/>) and the
/// region operations the hatch fills need.
/// </summary>
/// <remarks>
/// This is the only file that knows how a polygon region is stored or operated on. The four
/// operations it exposes -- <see cref="Union"/>, <see cref="Inflate"/>, <see cref="ClipOpenSubject"/>
/// and <see cref="PointInPolygon"/> -- are the whole dependency the hatch engine has on polygon
/// math, and they are implemented in this repository (<see cref="EvenOddRegion"/> and
/// <see cref="RegionOffset"/>) rather than in a library.
///
/// The design follows from one fact: a set of rings filled by the even-odd rule is already the
/// region. There is nothing to normalise, so <see cref="Union"/> has no sweep to run, and an offset
/// that folds back on itself is still correct, so <see cref="Inflate"/> has no boolean cleanup to
/// do. Those two removals are where the time went in the previous implementation.
/// </remarks>
public static class HatchClipper
{
    /// <summary>Doubles → int64 scale (6 decimal places; max coord ~1e6 → 1e12,
    /// safe in int64). Must stay in step with <c>HatchEngine.Scale</c>. Callers that can
    /// tolerate coarser geometry pass their own scale (see the spiral fill).</summary>
    private const double Scale = 1_000_000.0;

    // ---- conversions ---------------------------------------------------------------

    public static PathD ToPathD(List<Vector> poly)
    {
        var path = new PathD(poly.Count);
        foreach (var point in poly) path.Add(new PointD(point.X, point.Y));
        return path;
    }

    public static PathsD ToPathsD(List<List<Vector>> polys)
    {
        var result = new PathsD(polys.Count);
        foreach (var poly in polys) result.Add(ToPathD(poly));
        return result;
    }

    public static Path64 ToPath64(List<Vector> poly) => ToPath64(poly, Scale);

    public static Path64 ToPath64(List<Vector> poly, double scale)
    {
        var path = new Path64(poly.Count);
        foreach (var point in poly) path.Add(new Point64((long)Math.Round(point.X * scale), (long)Math.Round(point.Y * scale)));
        return path;
    }

    public static Paths64 ToPaths64(List<List<Vector>> polys) => ToPaths64(polys, Scale);

    public static Paths64 ToPaths64(List<List<Vector>> polys, double scale)
    {
        var result = new Paths64(polys.Count);
        foreach (var poly in polys) result.Add(ToPath64(poly, scale));
        return result;
    }

    /// <summary>PathsD → int64 Paths64 at the given scale (used after an inset).</summary>
    public static Paths64 ToPaths64(PathsD polys, double scale)
    {
        var result = new Paths64(polys.Count);
        foreach (var poly in polys)
        {
            var path = new Path64(poly.Count);
            foreach (var point in poly)
                path.Add(new Point64((long)Math.Round(point.x * scale), (long)Math.Round(point.y * scale)));
            result.Add(path);
        }
        return result;
    }

    public static List<Vector> FromPath64(Path64 path) => FromPath64(path, Scale);

    public static List<Vector> FromPath64(Path64 path, double scale)
    {
        var result = new List<Vector>(path.Count);
        foreach (var point in path) result.Add(new Vector(point.X / scale, point.Y / scale));
        return result;
    }

    // ---- the operations ------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="p"/> is strictly inside the closed polygon under the even-odd rule.
    /// </summary>
    public static bool PointInPolygon(Vector p, List<Vector> poly)
    {
        if (poly.Count < 3) return false;
        return new EvenOddRegion([poly]).Contains(p);
    }

    /// <summary>
    /// The region the rings describe. Returned as a ring set: a ring set <em>is</em> the region
    /// under even-odd, so there is nothing to merge, and rings that cross themselves or each other
    /// are already valid input for every other operation here.
    /// </summary>
    public static PathsD Union(PathsD subject)
    {
        var rings = new PathsD(subject.Count);
        foreach (var path in subject)
            if (path.Count >= 3) rings.Add(path);
        return rings;
    }

    /// <summary>The region the rings describe. See <see cref="Union(PathsD)"/>.</summary>
    public static PathsD Union(List<List<Vector>> polygons) => Union(ToPathsD(polygons));

    /// <summary>
    /// Offset the region by <paramref name="delta"/> (negative shrinks) with rounded joins, and
    /// return it as simple rings so it can also be drawn.
    /// </summary>
    public static PathsD Inflate(PathsD paths, double delta, double arcTolerance) =>
        RegionOffset.Inflate(paths, delta, arcTolerance);

    /// <summary>Like <see cref="Inflate"/>, but returns simple rings so each one can be drawn.</summary>
    public static PathsD InflateSimple(PathsD paths, double delta, double arcTolerance) =>
        RegionOffset.InflateSimple(paths, delta, arcTolerance);

    /// <summary>
    /// Clip a batch of <b>parallel</b> open segments to the region.
    /// </summary>
    /// <remarks>
    /// All the hatch lines at one angle are parallel, so they are one family of lines at constant
    /// distance along the normal. For each line, every crossing with the region boundary is found by
    /// solving along the normal, projected onto the line direction, sorted, and paired by even-odd.
    /// That is one pass over the edges per line with no grid, no per-interval point test and no
    /// temporary polygon sets -- and it is what the hatch grid does for every element.
    /// </remarks>
    public static List<(Vector A, Vector B)> ClipParallel(Paths64 clip,
        IReadOnlyList<(Vector A, Vector B)> lines, double scale, int maxParallelism = -1)
    {
        var result = new List<(Vector A, Vector B)>(lines.Count);
        if (lines.Count == 0 || clip.Count == 0) return result;

        // Work in the clip's integer units, like the clip itself.
        int ringCount = 0;
        foreach (var ring in clip) ringCount += ring.Count;
        if (ringCount == 0) return result;

        var ax = new double[ringCount];
        var ay = new double[ringCount];
        var bx = new double[ringCount];
        var by = new double[ringCount];
        int edge = 0;
        foreach (var ring in clip)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Point64 p = ring[i], q = ring[(i + 1) % ring.Count];
                ax[edge] = p.X; ay[edge] = p.Y;
                bx[edge] = q.X; by[edge] = q.Y;
                edge++;
            }
        }

        // Every line in the batch shares this direction and normal.
        Vector direction = lines[0].B - lines[0].A;
        if (direction.Length < 1e-12) return result;
        direction /= direction.Length;
        var normal = new Vector(-direction.Y, direction.X);

        // A drawing is typically a handful of paths on a machine with more cores than that, so the
        // outer per-element loop (HatchEngine's Parallel.For) leaves most of them idle. The lines are
        // independent, so the batch is split by line range and the pieces are concatenated in order,
        // which keeps the output identical to the sequential result.
        int workers = maxParallelism <= 0 ? Environment.ProcessorCount : Math.Min(maxParallelism, Environment.ProcessorCount);
        int chunks = Math.Min(workers, lines.Count);

        if (chunks <= 1)
        {
            Process(0, lines.Count, result);
            return result;
        }

        var parts = new List<(Vector A, Vector B)>[chunks];
        Parallel.For(0, chunks, chunk =>
        {
            int from = (int)((long)lines.Count * chunk / chunks);
            int to = (int)((long)lines.Count * (chunk + 1) / chunks);
            parts[chunk] = new List<(Vector A, Vector B)>(to - from);
            Process(from, to, parts[chunk]);
        });

        for (int chunk = 0; chunk < chunks; chunk++)
            if (parts[chunk] is { } part) result.AddRange(part);
        return result;

        void Process(int from, int to, List<(Vector A, Vector B)> output)
        {
            var crossings = new List<double>(16);
            for (int index = from; index < to; index++)
            {
                var line = lines[index];
                double startAlong = (line.A.X * direction.X + line.A.Y * direction.Y) * scale;
                double endAlong = (line.B.X * direction.X + line.B.Y * direction.Y) * scale;
                double lowAlong = Math.Min(startAlong, endAlong);
                double highAlong = Math.Max(startAlong, endAlong);
                double offset = (line.A.X * normal.X + line.A.Y * normal.Y) * scale;

                crossings.Clear();
                for (int e = 0; e < edge; e++)
                {
                    double da = ax[e] * normal.X + ay[e] * normal.Y - offset;
                    double db = bx[e] * normal.X + by[e] * normal.Y - offset;

                    // Half-open test on the side of the line: a vertex exactly on the line is counted once.
                    if (da > 0 == db > 0) continue;

                    double t = da / (da - db);
                    double x = ax[e] + (bx[e] - ax[e]) * t;
                    double y = ay[e] + (by[e] - ay[e]) * t;
                    crossings.Add(x * direction.X + y * direction.Y);
                }

                if (crossings.Count < 2) continue;
                crossings.Sort();

                for (int c = 0; c + 1 < crossings.Count; c += 2)
                {
                    double start = Math.Max(crossings[c], lowAlong);
                    double end = Math.Min(crossings[c + 1], highAlong);
                    if (end - start < 1e-9 * scale) continue;

                    output.Add((
                        new Vector((start * direction.X + offset * normal.X) / scale,
                                   (start * direction.Y + offset * normal.Y) / scale),
                        new Vector((end * direction.X + offset * normal.X) / scale,
                                   (end * direction.Y + offset * normal.Y) / scale)));
                }
            }
        }
    }

    /// <summary>The region a clip polygon describes, or null when it has no usable rings.</summary>
    public static EvenOddRegion? RegionOf(Paths64 clip)
    {
        var rings = new List<Vector[]>(clip.Count);
        foreach (var path in clip)
        {
            if (path.Count < 3) continue;
            var ring = new Vector[path.Count];
            for (int i = 0; i < path.Count; i++) ring[i] = new Vector(path[i].X, path[i].Y);
            rings.Add(ring);
        }
        return rings.Count == 0 ? null : new EvenOddRegion(rings, indexed: true);
    }

    /// <summary>
    /// Intersect open polylines with a closed clip polygon and return the parts inside. Batched:
    /// all the polylines go through one region, which is far cheaper than one test per line.
    /// </summary>
    public static Paths64 ClipOpenSubject(Paths64 clip, Paths64 openSubjects)
    {        if (openSubjects.Count == 0 || clip.Count == 0) return [];

        var rings = new List<Vector[]>(clip.Count);
        foreach (var path in clip)
        {
            if (path.Count < 3) continue;
            var ring = new Vector[path.Count];
            for (int i = 0; i < path.Count; i++) ring[i] = new Vector(path[i].X, path[i].Y);
            rings.Add(ring);
        }
        if (rings.Count == 0) return [];

        var region = new EvenOddRegion(rings, indexed: true);
        var result = new Paths64();

        var polyline = new List<Vector>(8);
        foreach (var open in openSubjects)
        {
            if (open.Count < 2) continue;
            polyline.Clear();
            foreach (var point in open) polyline.Add(new Vector(point.X, point.Y));

            foreach (var run in region.ClipOpen(polyline))
            {
                if (run.Count < 2) continue;
                var path = new Path64(run.Count);
                foreach (var point in run)
                    path.Add(new Point64((long)Math.Round(point.X), (long)Math.Round(point.Y)));
                result.Add(path);
            }
        }
        return result;
    }

    /// <summary>
    /// Clip an open polyline to the interior of a (possibly multi-ring, even-odd) polygon,
    /// returning the inside sub-polylines. Each returned sub-polyline becomes one continuous
    /// HatchPath.
    /// </summary>
    public static List<List<Vector>> ClipOpenToPolygon(Paths64 polygon, List<Vector> openPoly)
        => ClipOpenToPolygon(polygon, openPoly, Scale);

    public static List<List<Vector>> ClipOpenToPolygon(Paths64 polygon, List<Vector> openPoly, double scale)
    {
        var result = new List<List<Vector>>();
        if (openPoly.Count < 2 || polygon.Count == 0) return result;

        var openSolution = ClipOpenSubject(polygon, [ToPath64(openPoly, scale)]);
        foreach (var path in openSolution)
        {
            if (path.Count < 2) continue;
            result.Add(FromPath64(path, scale));
        }
        return result;
    }
}
