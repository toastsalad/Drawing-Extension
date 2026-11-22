using PlotterHelper.Geometry;

namespace PlotterHelper.Cli.Features;

/// <summary>
/// Reorders flattened outline strokes (one <see cref="LineSegmentChain"/> per curve)
/// to minimize pen-up travel with a greedy nearest-neighbor sweep: from the current
/// tool position, pick the undrawn stroke whose start (or end, when reversal is
/// allowed) is closest, emit it — reversed if the end was closer — and continue from
/// its far endpoint. A 2-opt-free TSP heuristic; not optimal, but collapses the big
/// document-order jumps to local hops.
///
/// Reversal is on by default (cutting a polyline backwards is geometrically
/// identical, and approaching a shape from either end roughly halves residual
/// travel). Connected chains within one subpath stay in order — their endpoints
/// coincide, so the nearest neighbour is always the natural next segment.
///
/// Above ~2048 strokes (e.g. a fine pooled hatch fill of tens of thousands of rings)
/// the O(n²) scan doesn't scale, so the search switches to a spatial grid of
/// endpoints with expanding-ring lookup — ~O(n) for the dense, locally-clustered
/// endpoint sets hatch fills produce.
/// </summary>
internal static class TravelOptimizer
{
    /// <summary>
    /// Greedy nearest-neighbor reorder. <paramref name="startPos"/> is the tool
    /// position before the first stroke (machine home / the previous group's
    /// end). Returns a new ordered list of chains; chains with fewer than two
    /// points (degenerate) are dropped.
    /// </summary>
    internal static List<LineSegmentChain> Optimize(
        IList<LineSegmentChain> input, Vector startPos, bool allowReverse)
    {
        var chains = new List<LineSegmentChain>(input.Count);
        foreach (var chain in input)
            if (chain.Count >= 2) chains.Add(chain);

        int count = chains.Count;
        if (count == 0) return new List<LineSegmentChain>();
        if (count == 1) return new List<LineSegmentChain> { chains[0] };

        // Small n: the brute O(n²) scan is cheaper than grid setup and trivially
        // exact (so existing behaviour/tests are unchanged).
        if (count <= BruteThreshold)
            return OptimizeBrute(chains, startPos, allowReverse);

        return OptimizeGrid(chains, startPos, allowReverse);
    }

    private const int BruteThreshold = 2048;

    /// <summary>Exact O(n²) greedy nearest-neighbor -- used for small stroke counts.</summary>
    private static List<LineSegmentChain> OptimizeBrute(List<LineSegmentChain> chains, Vector startPos, bool allowReverse)
    {
        int count = chains.Count;
        var result = new List<LineSegmentChain>(count);
        var used = new bool[count];
        Vector cur = startPos;
        for (int step = 0; step < count; step++)
        {
            int best = -1;
            double bestD = double.PositiveInfinity;
            bool bestRev = false;
            for (int candidate = 0; candidate < count; candidate++)
            {
                if (used[candidate]) continue;
                var chain = chains[candidate];
                double distSqStart = Vector.DistanceSquared(cur, chain[0]);
                if (distSqStart < bestD) { bestD = distSqStart; best = candidate; bestRev = false; }
                if (allowReverse)
                {
                    double distSqEnd = Vector.DistanceSquared(cur, chain[^1]);
                    if (distSqEnd < bestD) { bestD = distSqEnd; best = candidate; bestRev = true; }
                }
            }
            used[best] = true;
            var chosen = chains[best];
            if (bestRev)
            {
                var rev = new LineSegmentChain();
                for (int point = chosen.Count - 1; point >= 0; point--) rev.Append(chosen[point]);
                result.Add(rev);
                cur = chosen[0];
            }
            else
            {
                result.Add(chosen);
                cur = chosen[^1];
            }
        }
        return result;
    }

    /// <summary>
    /// Spatial-grid greedy nearest-neighbor for large stroke counts. Endpoints
    /// (each chain's start, plus its end when reversal is allowed) are bucketed
    /// by cell; each step searches cells in expanding Chebyshev rings from the
    /// current tool position until the nearest unused endpoint is found and no
    /// farther ring could contain a closer one. Bounded and ~O(n) for the dense,
    /// locally-clustered endpoint sets that hatch fills produce.
    /// </summary>
    private static List<LineSegmentChain> OptimizeGrid(List<LineSegmentChain> chains, Vector startPos, bool allowReverse)
    {
        int count = chains.Count;
        int epCount = allowReverse ? 2 * count : count;
        var epChain = new int[epCount];
        var epRev = new bool[epCount];
        var epX = new double[epCount];
        var epY = new double[epCount];

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Bbox(Vector point)
        {
            if (point.X < minX) minX = point.X; if (point.X > maxX) maxX = point.X;
            if (point.Y < minY) minY = point.Y; if (point.Y > maxY) maxY = point.Y;
        }

        int endpointIndex = 0;
        for (int chain = 0; chain < count; chain++)
        {
            var startPt = chains[chain][0];
            var endPt = chains[chain][^1];
            epChain[endpointIndex] = chain; epRev[endpointIndex] = false; epX[endpointIndex] = startPt.X; epY[endpointIndex] = startPt.Y; endpointIndex++;
            Bbox(startPt);
            if (allowReverse)
            {
                epChain[endpointIndex] = chain; epRev[endpointIndex] = true; epX[endpointIndex] = endPt.X; epY[endpointIndex] = endPt.Y; endpointIndex++;
                Bbox(endPt);
            }
            else Bbox(endPt);
        }
        Bbox(startPos);

        double span = Math.Max(maxX - minX, maxY - minY);
        if (!(span > 0)) span = 1;
        double cell = span / Math.Max(1.0, Math.Sqrt(count));
        if (!(cell > 0)) cell = 1;
        double originX = minX, originY = minY;

        var grid = new Dictionary<(int, int), List<int>>();
        for (int epIdx = 0; epIdx < epCount; epIdx++)
        {
            var key = ((int)Math.Floor((epX[epIdx] - originX) / cell),
                       (int)Math.Floor((epY[epIdx] - originY) / cell));
            if (!grid.TryGetValue(key, out var list)) { list = new List<int>(); grid[key] = list; }
            list.Add(epIdx);
        }

        int gMinX = (int)Math.Floor((minX - originX) / cell), gMaxX = (int)Math.Floor((maxX - originX) / cell);
        int gMinY = (int)Math.Floor((minY - originY) / cell), gMaxY = (int)Math.Floor((maxY - originY) / cell);
        int maxR = Math.Max(gMaxX - gMinX, gMaxY - gMinY) + 1;

        var used = new bool[count];
        Vector cur = startPos;
        var result = new List<LineSegmentChain>(count);

        for (int step = 0; step < count; step++)
        {
            int cgx = (int)Math.Floor((cur.X - originX) / cell);
            int cgy = (int)Math.Floor((cur.Y - originY) / cell);
            double ox = cur.X - (cgx * cell + originX);
            double oy = cur.Y - (cgy * cell + originY);
            double oMax = Math.Max(ox, oy);

            int bestEp = -1;
            double bestD = double.PositiveInfinity;

            for (int ring = 0; ring <= maxR; ring++)
            {
                ScanRing(ring, cgx, cgy, grid, used, epChain, epX, epY, cur, ref bestEp, ref bestD);
                if (bestEp >= 0)
                {
                    double minNext = (ring + 1) * cell - oMax;   // closest any point in ring >= ring+1 can be
                    if (minNext <= 0 || bestD <= minNext * minNext) break;
                }
            }

            if (bestEp < 0) bestEp = FirstUnusedEndpoint(used, epChain, epCount);

            used[epChain[bestEp]] = true;
            var chosen = chains[epChain[bestEp]];
            if (epRev[bestEp])
            {
                var rev = new LineSegmentChain();
                for (int point = chosen.Count - 1; point >= 0; point--) rev.Append(chosen[point]);
                result.Add(rev);
                cur = chosen[0];
            }
            else
            {
                result.Add(chosen);
                cur = chosen[^1];
            }
        }
        return result;
    }

    /// <summary>Scan every grid cell at Chebyshev distance exactly <paramref name="radius"/>
    /// from (cx,cy) and update the nearest unused endpoint found.</summary>
    private static void ScanRing(int radius, int cx, int cy,
        Dictionary<(int, int), List<int>> grid, bool[] used,
        int[] epChain, double[] epX, double[] epY, Vector cur,
        ref int bestEp, ref double bestD)
    {
        if (radius == 0)
        {
            if (grid.TryGetValue((cx, cy), out var list)) ScanCell(list, used, epChain, epX, epY, cur, ref bestEp, ref bestD);
            return;
        }
        // top and bottom rows
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (grid.TryGetValue((cx + dx, cy - radius), out var list)) ScanCell(list, used, epChain, epX, epY, cur, ref bestEp, ref bestD);
            if (grid.TryGetValue((cx + dx, cy + radius), out var list2)) ScanCell(list2, used, epChain, epX, epY, cur, ref bestEp, ref bestD);
        }
        // left and right columns (exclude corners already done)
        for (int dy = -radius + 1; dy <= radius - 1; dy++)
        {
            if (grid.TryGetValue((cx - radius, cy + dy), out var list)) ScanCell(list, used, epChain, epX, epY, cur, ref bestEp, ref bestD);
            if (grid.TryGetValue((cx + radius, cy + dy), out var list2)) ScanCell(list2, used, epChain, epX, epY, cur, ref bestEp, ref bestD);
        }
    }

    private static void ScanCell(List<int> eps, bool[] used, int[] epChain, double[] epX, double[] epY,
        Vector cur, ref int bestEp, ref double bestD)
    {
        foreach (var epIdx in eps)
        {
            if (used[epChain[epIdx]]) continue;
            double dx = epX[epIdx] - cur.X, dy = epY[epIdx] - cur.Y;
            double distSq = dx * dx + dy * dy;
            if (distSq < bestD) { bestD = distSq; bestEp = epIdx; }
        }
    }

    private static int FirstUnusedEndpoint(bool[] used, int[] epChain, int epCount)
    {
        for (int epIdx = 0; epIdx < epCount; epIdx++)
            if (!used[epChain[epIdx]]) return epIdx;
        return 0;
    }
}