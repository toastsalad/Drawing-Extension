// Ported from EggBot's eggbot_hatch.py, (c) Windell H. Oskay and contributors,
// licensed GPL-2.0-or-later; see LICENSE in the repository root.
using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Reduces pen lifts by joining the ends of nearby hatch segments with smooth
/// cubic Bézier curves. This is a faithful port of eggbot_hatch.py's
/// "Connect Nearby Ends" logic (the pre-qualify scan + recursivelyAppendNearbySegments),
/// with two performance changes:
///   * the per-step O(N²) nearest-segment-end search is replaced by a uniform
///     spatial hash over segment endpoints (O(1) amortized per query);
///   * the Python recursion (capped at 500, a real limit) is replaced by an
///     iterative loop, so chain length is unbounded and there is no recursion cost.
/// Output is a list of <see cref="HatchPath"/> using <see cref="LineTo"/> and
/// <see cref="CubicTo"/> ops, ready to flatten for G-code or serialize for SVG.
/// </summary>
internal static class PenLiftOptimizer
{
    private const double BezierOvershootMultiplier = 0.75;
    private const double RadianToleranceForColinear = 0.1;
    private const double RadianToleranceForAlternating = 0.1;
    private const int ChainLimit = 100_000; // safety cap; original RECURSION_LIMIT was 500

    private struct Seg
    {
        public Vector P1; // directed start (alternating orientation already applied)
        public Vector P2; // directed end
        public bool Drawn;
    }

    public static void Connect(List<HatchPath> paths, List<(Vector A, Vector B)> raw, HatchOptions opts)
    {
        if (raw.Count == 0) return;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double radius = spacing * opts.HatchScope;
        double radiusSq = radius * radius;
        double cell = Math.Max(radius, 1e-6);

        // Build segment array with alternating orientation (every other segment reversed),
        // exactly as the original does when assembling abs_line_segments.
        var segs = new Seg[raw.Count];
        for (int segment = 0; segment < raw.Count; segment++)
        {
            bool flip = (segment & 1) == 1;
            segs[segment].P1 = flip ? raw[segment].B : raw[segment].A;
            segs[segment].P2 = flip ? raw[segment].A : raw[segment].B;
        }

        // Spatial hash of segment endpoints, in flat arrays. A Dictionary of tuples whose values are
        // Lists allocated one per cell cost two List allocations per segment and a tuple hash on every
        // query; this is two int arrays built in two passes.
        var grid = new EndpointGrid();
        grid.Build(segs, cell);

        for (int refCount = 0; refCount < segs.Length; refCount++)
        {
            if (segs[refCount].Drawn) continue;

            // Pre-qualify: find which end of this segment has the closest connectable
            // partner. Determines the end we extend from (nRefEndAtClosest).
            bool found = false;
            int nRefEndAtClosest = 0;
            double closestSq = double.PositiveInfinity;
            for (int endIdx = 0; endIdx < 2; endIdx++)
            {
                Vector tip = endIdx == 0 ? segs[refCount].P1 : segs[refCount].P2;
                Vector other = endIdx == 0 ? segs[refCount].P2 : segs[refCount].P1;
                double refDir = Math.Atan2(other.Y - tip.Y, other.X - tip.X);
                var nearest = grid.Find(segs, radiusSq, tip, refCount, refDir);
                if (nearest.HasValue && nearest.Value.DistSq < closestSq)
                {
                    closestSq = nearest.Value.DistSq;
                    nRefEndAtClosest = endIdx;
                    found = true;
                }
            }

            var path = new HatchPath();
            if (!found)
            {
                // Solitary segment: draw P1 -> P2 as stored.
                path.Start = segs[refCount].P1;
                path.Ops.Add(new LineTo(segs[refCount].P2));
                segs[refCount].Drawn = true;
                paths.Add(path);
                continue;
            }

            // Start at the far end, draw toward the close end (the one with a partner).
            Vector startFar = nRefEndAtClosest == 0 ? segs[refCount].P2 : segs[refCount].P1;
            Vector nearEnd = nRefEndAtClosest == 0 ? segs[refCount].P1 : segs[refCount].P2;
            path.Start = startFar;
            paths.Add(path);

            Vector pen = startFar;
            Vector heldDelta = nearEnd - startFar; // the pending incoming line to emit
            segs[refCount].Drawn = true;

            int refSeg = refCount;
            int steps = 0;
            while (steps++ < ChainLimit)
            {
                Vector tip = pen + heldDelta;
                double refDir = Math.Atan2(-heldDelta.Y, -heldDelta.X); // reverse travel direction
                var nearest = grid.Find(segs, radiusSq, tip, refSeg, refDir);

                if (!nearest.HasValue)
                {
                    // Close out the pending line and finish this chain.
                    path.Ops.Add(new LineTo(tip));
                    break;
                }

                int count = nearest.Value.SegIdx;
                int end1 = nearest.Value.EndIdx; // connecting end
                int end2 = 1 - end1;
                Vector target = end1 == 0 ? segs[count].P1 : segs[count].P2;     // connector target
                Vector targetOther = end1 == 0 ? segs[count].P2 : segs[count].P1;

                // Incoming direction (travel) and outgoing reverse direction.
                double inDx = heldDelta.X, inDy = heldDelta.Y;
                double outDx = target.X - targetOther.X, outDy = target.Y - targetOther.Y; // points back toward the joiner
                double lenIn = Math.Sqrt(inDx * inDx + inDy * inDy);
                double lenOut = Math.Sqrt(outDx * outDx + outDy * outDy);
                if (lenIn == 0 || lenOut == 0)
                {
                    path.Ops.Add(new LineTo(tip));
                    break;
                }

                double smoothest = spacing * BezierOvershootMultiplier;
                double divIn = 2.0 * smoothest / lenIn;
                double divOut = 2.0 * smoothest / lenOut;
                double divLargest = Math.Max(divIn, divOut);
                double div = Math.Max(1.0, divLargest);
                double shorten = smoothest / div;

                Vector subtract = RelativeControlPoint(shorten, inDx, inDy, 0, 0);
                Vector addOut = RelativeControlPoint(shorten, outDx, outDy, 0, 0);

                heldDelta -= subtract;

                // Connector from the (untrimmed) tip to the outgoing start.
                double deltaCx = target.X - tip.X;
                double deltaCy = target.Y - tip.Y;

                Vector cpIn = RelativeControlPoint(spacing / div, inDx, inDy, 0, 0);
                Vector cpOut = RelativeControlPoint(spacing / div, outDx, outDy, deltaCx, deltaCy);

                // Close out the (trimmed) incoming line.
                Vector lineEnd = pen + heldDelta;
                path.Ops.Add(new LineTo(lineEnd));
                pen = lineEnd;

                // Cubic Bézier join to the outgoing start.
                Vector control1 = pen + cpIn;
                Vector control2 = pen + cpOut;
                Vector bezierEnd = new(pen.X + deltaCx, pen.Y + deltaCy);
                path.Ops.Add(new CubicTo(control1, control2, bezierEnd));
                pen = bezierEnd;

                segs[count].Drawn = true;

                // The outgoing line becomes the next pending line, shifted by addOut.
                Vector outVec = (end2 == 0 ? segs[count].P1 : segs[count].P2) - target;
                heldDelta = outVec + addOut;
                refSeg = count;
            }

            if (steps >= ChainLimit)
            {
                // Safety: close out whatever is pending.
                path.Ops.Add(new LineTo(pen + heldDelta));
            }
        }
    }

    // ---- spatial hash + nearest-neighbour query -------------------------------

    /// <summary>
    /// Segment endpoints bucketed by a uniform grid, in compressed-row form.
    /// </summary>
    /// <remarks>
    /// Each endpoint belongs to exactly one cell (the one containing it) and a query looks at the
    /// three-by-three block around the point, which covers the connect radius because the cell size is
    /// the radius. That is the behaviour the dictionary version had, kept deliberately.
    /// </remarks>
    private struct EndpointGrid
    {
        private int[] _cellStart;
        private int[] _items;       // encoded as segment * 2 + end
        private int _cellsX, _cellsY;
        private double _cell, _originX, _originY;

        internal void Build(Seg[] segs, double cell)
        {
            _cell = Math.Max(cell, 1e-6);
            int count = segs.Length;
            if (count == 0)
            {
                _cellStart = [0];
                _items = [];
                _cellsX = _cellsY = 1;
                return;
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < count; i++)
            {
                Vector a = segs[i].P1, b = segs[i].P2;
                minX = Math.Min(minX, Math.Min(a.X, b.X)); minY = Math.Min(minY, Math.Min(a.Y, b.Y));
                maxX = Math.Max(maxX, Math.Max(a.X, b.X)); maxY = Math.Max(maxY, Math.Max(a.Y, b.Y));
            }

            _originX = minX;
            _originY = minY;
            _cellsX = (int)Math.Floor((maxX - minX) / _cell) + 1;
            _cellsY = (int)Math.Floor((maxY - minY) / _cell) + 1;

            int cells = _cellsX * _cellsY;
            _cellStart = new int[cells + 1];

            for (int segment = 0; segment < count; segment++)
            {
                _cellStart[CellOf(segs[segment].P1) + 1]++;
                _cellStart[CellOf(segs[segment].P2) + 1]++;
            }
            for (int c = 1; c <= cells; c++) _cellStart[c] += _cellStart[c - 1];

            _items = new int[_cellStart[cells]];
            var cursor = new int[cells];
            Array.Copy(_cellStart, cursor, cells);
            for (int segment = 0; segment < count; segment++)
            {
                _items[cursor[CellOf(segs[segment].P1)]++] = segment * 2;
                _items[cursor[CellOf(segs[segment].P2)]++] = segment * 2 + 1;
            }
        }

        private readonly int CellOf(Vector point)
        {
            int column = (int)((point.X - _originX) / _cell);
            int row = (int)((point.Y - _originY) / _cell);
            if (column < 0) column = 0; else if (column >= _cellsX) column = _cellsX - 1;
            if (row < 0) row = 0; else if (row >= _cellsY) row = _cellsY - 1;
            return row * _cellsX + column;
        }

        internal readonly Nearest? Find(Seg[] segs, double radiusSq, Vector point, int excludeSeg, double refDir)
        {
            int cellIndex = CellOf(point);
            int column = cellIndex % _cellsX;
            int row = cellIndex / _cellsX;

            Nearest? best = null;
            double bestSq = radiusSq;

            for (int gy = row - 1; gy <= row + 1; gy++)
            {
                if (gy < 0 || gy >= _cellsY) continue;
                for (int gx = column - 1; gx <= column + 1; gx++)
                {
                    if (gx < 0 || gx >= _cellsX) continue;
                    int cell = gy * _cellsX + gx;
                    int end = _cellStart[cell + 1];
                    for (int k = _cellStart[cell]; k < end; k++)
                    {
                        int encoded = _items[k];
                        int segIdx = encoded >> 1;
                        int endIdx = encoded & 1;
                        if (segs[segIdx].Drawn) continue;
                        if (segIdx == excludeSeg) continue;

                        Vector cand = endIdx == 0 ? segs[segIdx].P1 : segs[segIdx].P2;
                        Vector other = endIdx == 0 ? segs[segIdx].P2 : segs[segIdx].P1;
                        double ddx = cand.X - point.X, ddy = cand.Y - point.Y;
                        double distSq = ddx * ddx + ddy * ddy;
                        if (distSq > bestSq) continue;

                        double newDir = Math.Atan2(cand.Y - other.Y, cand.X - other.X);
                        if (!WouldBeAlternating(refDir, newDir)) continue;
                        double joinerDir = Math.Atan2(cand.Y - point.Y, cand.X - point.X);
                        if (AreColinear(refDir, joinerDir)) continue;

                        bestSq = distSq;
                        best = new Nearest(segIdx, endIdx, distSq);
                    }
                }
            }
            return best;
        }
    }

    private readonly struct Nearest
    {
        public readonly int SegIdx; public readonly int EndIdx; public readonly double DistSq;
        public Nearest(int segIdx, int endIdx, double distSq) { SegIdx = segIdx; EndIdx = endIdx; DistSq = distSq; }
    }

    // ---- helpers (ported verbatim from eggbot_hatch.py) -----------------------

    private static Vector RelativeControlPoint(double distance, double fdx, double fdy, double dx, double dy)
    {
        if (fdx == 0) return new(dx, Math.CopySign(distance, fdy) + dy);
        if (fdy == 0) return new(Math.CopySign(distance, fdx) + dx, dy);
        double slope = Math.Atan2(fdy, fdx);
        return new(distance * Math.Cos(slope) + dx, distance * Math.Sin(slope) + dy);
    }

    private static bool WouldBeAlternating(double refDir, double newDir)
    {
        double diff = refDir - newDir;
        if (diff < 0) diff += 2 * Math.PI;
        diff -= Math.PI;
        return Math.Abs(diff) < RadianToleranceForAlternating;
    }

    private static bool AreColinear(double dir1, double dir2)
    {
        double absDiff = Math.Abs(dir1 - dir2);
        if (absDiff < RadianToleranceForColinear) return true;
        if (Math.Abs(absDiff - Math.PI) < RadianToleranceForColinear) return true;
        return false;
    }
}
