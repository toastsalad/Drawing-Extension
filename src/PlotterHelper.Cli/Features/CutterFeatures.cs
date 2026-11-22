using PlotterHelper.Geometry;
using PlotterHelper.Svg;

namespace PlotterHelper.Cli.Features;

/// <summary>
/// Drag-knife / vinyl-cutter helpers, less relevant to pen plotting but included
/// for parity with cutter workflows:
///   * <see cref="Overcut"/> — extend each closed subpath past its start so the
///     blade overlaps and corners close cleanly.
///   * <see cref="InnerBeforeOuter"/> — order closed paths by containment depth
///     (deepest first) so interior holes are cut before the outer outline and the
///     piece doesn't shift while still held by the surrounding material.
/// </summary>
internal static class Overcut
{
    /// <summary>Append an overcut extension to each closed subpath of a path, in place.</summary>
    internal static void ApplyToPath(SvgPath path, double overcut, double tolerance)
    {
        if (overcut <= 0 || path.Curves.Count == 0) return;
        var curves = path.Curves;
        var result = new List<Curve>(curves.Count + curves.Count / 4 + 1);
        int subpathStart = 0;
        while (subpathStart < curves.Count)
        {
            // Walk one continuous subpath [subpathStart..subpathEnd] and flatten it for the start tangent.
            int subpathEnd = subpathStart;
            var chain = new LineSegmentChain();
            chain.AppendFlattened(curves[subpathStart], tolerance);
            while (subpathEnd + 1 < curves.Count && Dist2(curves[subpathEnd].End, curves[subpathEnd + 1].Start) <= 1.0)
            {
                subpathEnd++;
                chain.AppendFlattened(curves[subpathEnd], tolerance);
            }

            for (int curve = subpathStart; curve <= subpathEnd; curve++) result.Add(curves[curve]);

            // If the subpath is closed, continue past the start along the first edge.
            Vector start = curves[subpathStart].Start, lastEnd = curves[subpathEnd].End;
            if (Dist2(start, lastEnd) < 1.0 && chain.Count >= 2)
            {
                Vector direction = chain[1] - chain[0];
                double len = direction.Length;
                if (len > 1e-9)
                {
                    Vector unit = direction / len;
                    result.Add(new Line(lastEnd, new Vector(lastEnd.X + unit.X * overcut, lastEnd.Y + unit.Y * overcut)));
                }
            }
            subpathStart = subpathEnd + 1;
        }

        if (result.Count != curves.Count)
        {
            curves.Clear();
            curves.AddRange(result);
        }
    }

    private static double Dist2(Vector a, Vector b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}

internal static class InnerBeforeOuter
{
    /// <summary>
    /// Reorder so open paths keep their relative order (emitted first), then closed
    /// paths in descending containment depth (deepest holes first, outer outlines last).
    /// </summary>
    internal static List<SvgPath> Reorder(IList<SvgPath> paths, double tolerance)
    {
        if (paths.Count < 2) return paths.ToList();

        var closed = new List<Entry>();
        var open = new List<SvgPath>();
        for (int idx = 0; idx < paths.Count; idx++)
        {
            var poly = FlattenClosed(paths[idx], tolerance);
            if (poly != null) closed.Add(new Entry { Path = paths[idx], Poly = poly, Rep = poly[0], Order = idx });
            else open.Add(paths[idx]);
        }

        if (closed.Count < 2)
        {
            var earlyResult = new List<SvgPath>(open);
            earlyResult.AddRange(closed.Select(entry => entry.Path));
            return earlyResult;
        }

        // depth = number of OTHER closed polygons that strictly contain this one.
        int[] depth = new int[closed.Count];
        for (int outerIndex = 0; outerIndex < closed.Count; outerIndex++)
            for (int innerIndex = 0; innerIndex < closed.Count; innerIndex++)
                if (outerIndex != innerIndex && Hatch.HatchClipper.PointInPolygon(closed[outerIndex].Rep, closed[innerIndex].Poly))
                    depth[outerIndex]++;

        var ordered = closed
            .Select((entry, idx) => (entry, depthValue: depth[idx]))
            .OrderByDescending(item => item.depthValue)
            .ThenBy(item => item.entry.Order) // stable: original document order as tie-break
            .Select(item => item.entry.Path)
            .ToList();

        var result = new List<SvgPath>(open);
        result.AddRange(ordered);
        return result;
    }

    private sealed class Entry
    {
        public SvgPath Path = null!;
        public List<Vector> Poly = null!;
        public Vector Rep;
        public int Order;
    }

    /// <summary>Flatten a path to a closed polygon, or null if it isn't a closed loop.</summary>
    internal static List<Vector>? FlattenClosed(SvgPath path, double tolerance)
    {
        if (path.Curves.Count == 0) return null;
        var chain = new LineSegmentChain();
        foreach (var curve in path.Curves) chain.AppendFlattened(curve, tolerance);
        if (chain.Count < 3) return null;
        if (Dist2(chain[0], chain[^1]) >= 1.0) return null;
        return chain.Points;
    }

    private static double Dist2(Vector a, Vector b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
