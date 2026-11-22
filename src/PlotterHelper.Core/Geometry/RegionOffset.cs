namespace PlotterHelper.Geometry;

/// <summary>
/// Polygon offsetting (inflating and eroding) for even-odd ring sets.
/// </summary>
/// <remarks>
/// The previous implementation offset every ring and then ran a general boolean union to clean up
/// the self-intersections the offset created. That cleanup is the expensive half of offsetting, and
/// for an even-odd region it is unnecessary: the fold-backs a naive offset produces are exactly the
/// places where the offset curve crosses itself, and the even-odd rule already treats those as the
/// holes they are. So an erosion of a neck that pinches comes out right without any boolean work.
///
/// Join rule, which is the only subtle part:
/// <list type="bullet">
/// <item><b>Eroding</b> (<c>delta &lt; 0</c>): every corner is cut to the intersection of the two
/// offset edges. Erosion of a corner always has a sharp corner, whether the original corner was
/// convex or reflex, and a sharp corner is that intersection.</item>
/// <item><b>Dilating</b> (<c>delta &gt; 0</c>): a convex corner becomes an arc of radius
/// <c>delta</c>, because dilation adds a disc there; a reflex corner becomes the intersection.</item>
/// </list>
///
/// Because the result feeds an even-odd test, folds are left in place. Callers that need to draw
/// the rings (<see cref="SimpleLoops"/>) get the loops the fold-backs carve out, classified by the
/// same even-odd rule.
/// </remarks>
public static class RegionOffset
{
    /// <summary>Offset the region and return it as rings. The rings may fold back on themselves.</summary>
    /// <remarks>
    /// This is what the clipping paths want: they only ever ask "is this point/line inside", and even-odd
    /// answers that correctly for a folded ring. Splitting is not done because it costs O(n²) per ring and
    /// is only worth paying when the rings are drawn.
    /// </remarks>
    public static PathsD Inflate(PathsD paths, double delta, double tolerance) =>
        Inflate(paths, delta, tolerance, split: false);

    /// <summary>Offset the region and return simple rings, so each one can be drawn as a closed path.</summary>
    public static PathsD InflateSimple(PathsD paths, double delta, double tolerance) =>
        ToPaths(InflateRings(ToRingList(paths), delta, tolerance, split: true));

    /// <summary>
    /// Offset these polygons and return the rings, without a round trip through <see cref="PathsD"/>.
    /// </summary>
    /// <remarks>
    /// The hatch clip is built per element, so the copy into PathsD and back out again was three
    /// passes over the same vertices to hand the result to the next stage.
    /// </remarks>
    public static List<Vector[]> InflateRings(List<List<Vector>> polygons, double delta, double tolerance, bool split = false) =>
        InflateRings(ToRingList(polygons), delta, tolerance, split);

    private static PathsD Inflate(PathsD paths, double delta, double tolerance, bool split) =>
        ToPaths(InflateRings(ToRingList(paths), delta, tolerance, split));

    /// <summary>Offset an existing ring set, for callers that erode repeatedly.</summary>
    internal static List<Vector[]> InflateRings(List<Vector[]> rings, double delta, double tolerance, bool split)
    {
        if (rings.Count == 0) return rings;

        // A zero offset changes nothing except that the rings get cleaned up.
        if (Math.Abs(delta) < 1e-12) return split ? SimpleLoops(rings) : rings;

        // One indexed region for the whole input, used once per ring to find out which side of that
        // ring the region lies on. The previous version built a fresh region for every ring pair to
        // count containment depth, which is quadratic region construction and dominated the whole
        // hatch when a drawing has many subpaths. Most elements are a single ring, and that case
        // needs no region at all.
        //
        // Tried and rejected: replacing these probes with smarter nesting decisions -- a verified
        // source-winding fast path and an exact pairwise containment scan pruned by bounding box.
        // Both were correct and both were slower on the two benchmark drawings (fish.svg 5.6 -> 8.3 ms,
        // octopus.svg 19.5 -> 20.0 ms), because the winding check needs the same region build these
        // probes use, and the pairwise scan's setup costs more than the probes it removes. A probe per
        // ring is cheap next to that; do not replace it without measuring the whole benchmark.
        EvenOddRegion? region = null;
        if (rings.Count > 1) region = new EvenOddRegion(rings, indexed: true);

        // Normalise the winding first, before anything reads it. InteriorProbe finds a point inside a
        // ring by stepping to the left of one of its edges, which is the interior only for a
        // counter-clockwise ring; called on a clockwise ring it returns a point outside, the nesting
        // probe then says "not an outer boundary", and the offset is applied in the wrong direction.
        // SVG exported from Inkscape is clockwise, so an "inset" grew the drawing instead of shrinking
        // it, and hatches landed outside the outline.
        foreach (var ring in rings)
        {
            if (ring.Length < 3) continue;
            if (!Polygons.IsCounterClockwise(ring)) Array.Reverse(ring);
        }

        // Pass one, serial: which side of each ring the region is on. Inside the probe means every
        // ring enclosing this one contributes an even count, so this is an outer boundary and eroding
        // moves it along the outgoing normal; a hole boundary gets the opposite sign. A single ring is
        // always an outer boundary. This has to stay serial because Contains keeps a stamp array and
        // is not thread safe.
        var outer = new bool[rings.Count];
        for (int index = 0; index < rings.Count; index++)
        {
            bool isOuter = region is null;
            if (region is not null && InteriorProbe(rings[index]) is Vector probe) isOuter = region.Contains(probe);
            outer[index] = isOuter;
        }

        // Pass two: offset each ring. The rings are independent, and a drawing has thousands of them
        // -- a fish path here is 337 rings, an octopus path 1766, at about 20 vertices each -- so this
        // is the one place in the hatch with a large amount of genuinely parallel work. Nothing shared
        // is touched: each ring normalises and offsets only itself.
        var offset = new List<Vector[]>(rings.Count);
        if (rings.Count >= 32)
        {
            var slots = new Vector[]?[rings.Count];
            Parallel.For(0, rings.Count, index => slots[index] = OffsetOne(rings[index], outer[index], delta, tolerance));
            foreach (var ring in slots) if (ring is not null) offset.Add(ring);
        }
        else
        {
            for (int index = 0; index < rings.Count; index++)
                if (OffsetOne(rings[index], outer[index], delta, tolerance) is Vector[] ring) offset.Add(ring);
        }

        return split
            ? SimpleLoops(offset, region ?? new EvenOddRegion(rings), Math.Max(tolerance, 0) * FoldTouchSlack)
            : offset;
    }

    /// <summary>Offset one ring, normalising its winding first. Null when the offset collapses.</summary>
    private static Vector[]? OffsetOne(Vector[] ring, bool outer, double delta, double tolerance)
    {
        if (ring.Length < 3) return null;

        // The ring arrives counter-clockwise; see the note in InflateRings.
        double signed = outer ? delta : -delta;
        PathD? offsetRing = OffsetRing(ring, signed, tolerance);
        return offsetRing is not null && offsetRing.Count >= 3 ? ToVectors(offsetRing) : null;
    }

    private static Vector[] ToVectors(PathD path)
    {
        var ring = new Vector[path.Count];
        for (int i = 0; i < path.Count; i++) ring[i] = new Vector(path[i].x, path[i].y);
        return ring;
    }

    /// <summary>Ring sets with no self-intersections, ready to be drawn one ring at a time.</summary>
    private static PathsD SimpleLoops(PathsD paths) => ToPaths(SimpleLoops(ToRingList(paths)));

    private static List<Vector[]> SimpleLoops(List<Vector[]> rings, double tolerance = 0)
    {
        if (rings.Count == 0) return rings;
        // A fold can be crossed twice at a sharp corner, so the allowance for a loop that only touches the
        // outline is a few times the offset's own error. It has to stay far below any real gap: on a drawing of
        // 1766 self-overlapping facets the loops that wrongly survive reach 33 to 107 units outside, while the
        // ones that only touch the outline stay under 1.
        double slack = Math.Max(tolerance, 0) * FoldTouchSlack;
        return SimpleLoops(rings, new EvenOddRegion(rings), slack);
    }

    private const double FoldTouchSlack = 4.0;

    private static List<Vector[]> SimpleLoops(List<Vector[]> rings, EvenOddRegion classification, double slack = 0)
    {
        var result = new List<Vector[]>(rings.Count);
        foreach (var ring in rings) Split(ring, classification, result, slack);
        return result;
    }

    // ---- offsetting a single ring --------------------------------------------------

    /// <summary>Offset one counter-clockwise ring by <paramref name="delta"/>, positive outward.</summary>
    /// <remarks>
    /// One join is emitted per vertex; the offset edges are the straight lines between consecutive
    /// joins. Emitting the first edge's start as well would leave a spike from the closing join back
    /// to it, which is exactly the bug that made an inset region reach the original boundary.
    /// </remarks>
    private static PathD? OffsetRing(Vector[] ring, double delta, double tolerance)
    {
        int count = ring.Length;
        if (count < 3) return null;

        var result = new PathD(count + 8);
        double arcStep = ArcStep(delta, tolerance);
        double miterLimit = Math.Abs(delta) * 1000 + 1e-12;

        for (int i = 0; i < count; i++)
        {
            Vector previous = ring[(i + count - 1) % count];
            Vector vertex = ring[i];
            Vector next = ring[(i + 1) % count];

            Vector incoming = vertex - previous;
            Vector outgoing = next - vertex;
            if (incoming.Length < 1e-12 || outgoing.Length < 1e-12) continue;
            incoming /= incoming.Length;
            outgoing /= outgoing.Length;

            // Outward normals of a counter-clockwise edge.
            var before = new Vector(incoming.Y, -incoming.X);
            var after = new Vector(outgoing.Y, -outgoing.X);
            Vector endOfPreviousEdge = vertex + before * delta;
            Vector startOfNextEdge = vertex + after * delta;

            double cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;

            if (delta > 0 && cross > 1e-12)
            {
                // Dilation at a convex corner: the boundary follows an arc around the vertex.
                AppendArc(result, vertex, endOfPreviousEdge, startOfNextEdge, arcStep);
            }
            else
            {
                Vector? miter = Intersect(endOfPreviousEdge, incoming, startOfNextEdge, outgoing);
                if (miter.HasValue && (miter.Value - vertex).Length <= miterLimit)
                {
                    result.Add(new PointD(miter.Value.X, miter.Value.Y));
                }
                else
                {
                    // No usable intersection (parallel edges, or a fold so deep the point is far
                    // away): keep both offset ends. The gap they leave is a fold, which even-odd
                    // resolves with the rest of the ring.
                    result.Add(new PointD(endOfPreviousEdge.X, endOfPreviousEdge.Y));
                    result.Add(new PointD(startOfNextEdge.X, startOfNextEdge.Y));
                }
            }
        }

        return result.Count >= 3 ? result : null;
    }

    /// <summary>Intersection of the line through <paramref name="p"/> along <paramref name="dp"/> with the line through q.</summary>
    private static Vector? Intersect(Vector p, Vector dp, Vector q, Vector dq)
    {
        double denominator = dp.X * dq.Y - dp.Y * dq.X;
        if (Math.Abs(denominator) < 1e-12) return null;
        double t = ((q.X - p.X) * dq.Y - (q.Y - p.Y) * dq.X) / denominator;
        return new Vector(p.X + dp.X * t, p.Y + dp.Y * t);
    }

    private static void AppendArc(PathD path, Vector centre, Vector from, Vector to, double step)
    {
        double a0 = Math.Atan2(from.Y - centre.Y, from.X - centre.X);
        double a1 = Math.Atan2(to.Y - centre.Y, to.X - centre.X);
        double sweep = a1 - a0;
        while (sweep <= -Math.PI) sweep += 2 * Math.PI;
        while (sweep > Math.PI) sweep -= 2 * Math.PI;

        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / step));
        double radius = (from - centre).Length;
        for (int i = 1; i <= steps; i++)
        {
            double angle = a0 + sweep * i / steps;
            path.Add(new PointD(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle)));
        }
    }

    /// <summary>Largest angle between arc segments that stays within <paramref name="tolerance"/> of the true arc.</summary>
    private static double ArcStep(double radius, double tolerance)
    {
        radius = Math.Abs(radius);
        if (radius < 1e-12) return Math.PI / 2;
        double ratio = 1 - Math.Min(tolerance, radius * 0.5) / radius;
        return Math.Clamp(2 * Math.Acos(Math.Clamp(ratio, -1, 1)), 0.05, Math.PI / 2);
    }

    // ---- splitting a self-intersecting ring into simple loops -----------------------

    /// <summary>
    /// Append the simple loops of <paramref name="ring"/>, keeping the ones the even-odd rule fills.
    /// </summary>
    private static void Split(Vector[] ring, EvenOddRegion classification, List<Vector[]> result,
        double slack = 0, int depth = 0)
    {
        if (ring.Length < 3 || depth > 24) return;

        // A fold loop can be wound either way, and InteriorProbe reads "interior is left of the edge",
        // which is only true for a counter-clockwise ring. Without this the probe of a clockwise loop
        // lands outside it, classification accepts a piece that sits outside the shape, and the clip
        // grows past the outline.
        if (!Polygons.IsCounterClockwise(ring)) Array.Reverse(ring);

        if (!FirstSelfIntersection(ring, out int edgeA, out int edgeB, out Vector crossing))
        {
            if (Math.Abs(Polygons.SignedArea2(ring)) > 1e-9 && InteriorProbe(ring) is Vector probe
                && classification.Contains(probe) && Inside(ring, classification, slack))
            {
                result.Add(ring);
            }
            return;
        }

        Split(FirstLoop(ring, edgeA, edgeB, crossing), classification, result, slack, depth + 1);
        Split(SecondLoop(ring, edgeA, edgeB, crossing), classification, result, slack, depth + 1);
    }

    /// <summary>
    /// True when every vertex of the loop is inside the classification region, or on its outline.
    /// </summary>
    /// <remarks>
    /// A probe test alone is not enough to decide this. Splitting a folded offset at its first crossing leaves,
    /// for every fold, the loop on the other side of the crossing as well, and that loop is the complement of the
    /// fold: a quadrilateral that spans the whole drawing and contains the shape. Its probe point is inside the
    /// shape because the shape is inside it, so it was accepted, and an even-odd clip then counted the whole
    /// drawing as filled -- which is how an inset managed to put ink 19 units outside a 1766-facet drawing with
    /// nothing near it. An erosion is a subset of the region it came from, so a loop with vertices well outside
    /// that region cannot be part of one.
    /// </remarks>
    private static bool Inside(Vector[] ring, EvenOddRegion classification, double slack)
    {
        foreach (var point in ring)
            if (!classification.ContainsWithin(point, slack)) return false;
        return true;
    }

    /// <summary>The first pair of non-adjacent edges that cross, and where.</summary>
    private static bool FirstSelfIntersection(Vector[] ring, out int edgeA, out int edgeB, out Vector crossing)
    {
        int count = ring.Length;
        for (int i = 0; i < count; i++)
        {
            Vector a = ring[i], b = ring[(i + 1) % count];
            for (int j = i + 2; j < count; j++)
            {
                if (i == 0 && j == count - 1) continue;   // adjacent through the wrap
                Vector c = ring[j], d = ring[(j + 1) % count];
                if (SegmentCrossing(a, b, c, d) is Vector point)
                {
                    edgeA = i; edgeB = j; crossing = point;
                    return true;
                }
            }
        }
        edgeA = edgeB = -1;
        crossing = default;
        return false;
    }

    private static Vector? SegmentCrossing(Vector a, Vector b, Vector c, Vector d)
    {
        double rx = b.X - a.X, ry = b.Y - a.Y;
        double sx = d.X - c.X, sy = d.Y - c.Y;
        double denominator = rx * sy - ry * sx;
        if (Math.Abs(denominator) < 1e-12) return null;

        double t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / denominator;
        double u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / denominator;
        if (t < 1e-9 || t > 1 - 1e-9 || u < 1e-9 || u > 1 - 1e-9) return null;
        return new Vector(a.X + rx * t, a.Y + ry * t);
    }

    private static Vector[] FirstLoop(Vector[] ring, int edgeA, int edgeB, Vector crossing)
    {
        var loop = new List<Vector>(edgeB - edgeA + 2) { crossing };
        for (int i = edgeA + 1; i <= edgeB; i++) loop.Add(ring[i]);
        return loop.ToArray();
    }

    private static Vector[] SecondLoop(Vector[] ring, int edgeA, int edgeB, Vector crossing)
    {
        int count = ring.Length;
        var loop = new List<Vector>(count - (edgeB - edgeA) + 2) { crossing };
        for (int i = edgeB + 1; i < count; i++) loop.Add(ring[i]);
        for (int i = 0; i <= edgeA; i++) loop.Add(ring[i]);
        return loop.ToArray();
    }

    /// <summary>A point just inside a counter-clockwise ring, from its longest edge.</summary>
    private static Vector? InteriorProbe(Vector[] ring)
    {
        int best = -1;
        double longest = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            double length = (ring[(i + 1) % ring.Length] - ring[i]).LengthSquared;
            if (length > longest) { longest = length; best = i; }
        }
        if (best < 0 || longest <= 1e-18) return null;

        Vector a = ring[best], b = ring[(best + 1) % ring.Length];
        Vector direction = b - a;
        direction /= direction.Length;
        var normal = new Vector(-direction.Y, direction.X);   // interior is left of a counter-clockwise edge
        double step = Math.Max(Math.Sqrt(longest) * 1e-2, 1e-6);
        return (a + b) * 0.5 + normal * step;
    }

    private static PathsD ToPaths(List<Vector[]> rings)
    {
        var paths = new PathsD(rings.Count);
        foreach (var ring in rings)
        {
            var path = new PathD(ring.Length);
            foreach (var point in ring) path.Add(new PointD(point.X, point.Y));
            paths.Add(path);
        }
        return paths;
    }

    // ---- helpers -------------------------------------------------------------------

    private static List<Vector[]> ToRingList(List<List<Vector>> polygons)
    {
        var rings = new List<Vector[]>(polygons.Count);
        foreach (var polygon in polygons)
            if (Normalise(polygon, polygon.Count) is Vector[] ring) rings.Add(ring);
        return rings;
    }

    private static List<Vector[]> ToRingList(PathsD paths)
    {
        var rings = new List<Vector[]>(paths.Count);
        foreach (var path in paths)
            if (Normalise(path.Select(p => new Vector(p.x, p.y)), path.Count) is Vector[] ring) rings.Add(ring);
        return rings;
    }

    /// <summary>
    /// A ring must not repeat its closing point: the edge into and out of the first vertex would then
    /// be zero-length, its join would be skipped, and the offset lines on either side would mitre
    /// against the wrong neighbours. Collinear repeats are dropped for the same reason.
    /// </summary>
    /// <remarks>
    /// Presized from the caller's count: a ring in a detailed drawing can have hundreds of points, and
    /// starting at 8 made this grow and copy several times per ring.
    /// </remarks>
    private static Vector[]? Normalise(IEnumerable<Vector> points, int capacity)
    {
        var kept = new List<Vector>(capacity);
        foreach (var point in points)
        {
            if (kept.Count > 0 && (kept[^1] - point).Length < 1e-9) continue;
            kept.Add(point);
        }
        while (kept.Count > 1 && (kept[0] - kept[^1]).Length < 1e-9) kept.RemoveAt(kept.Count - 1);
        return kept.Count < 3 ? null : kept.ToArray();
    }
}
