using PlotterHelper.Geometry;
using PlotterHelper.Svg;

namespace PlotterHelper.Cli.Features;

/// <summary>
/// Ballpoint-pen "dot" helper: re-trace short closed subpaths a second time without
/// lifting the pen, so tiny dots deposit enough ink to be visible.
///
/// This is a transform on a path's curve list (like <see cref="Overcut"/>), not a
/// compiler step, because the compiler flattens one curve per chain — a dot (an SVG
/// &lt;circle&gt; is two arcs) would be split into two open semicircles and never look
/// closed. Duplicating the subpath's curves here makes the per-curve emission
/// retrace the loop as one continuous pen-down stroke (the duplicate's first curve
/// starts exactly where the original ended — it's closed — so the compiler keeps
/// the pen down and re-emits the moves).
///
/// "Closed" uses the curves' exact endpoints (no flattening drift); "short" uses
/// the flattened subpath length. Both in output (G-code) units.
/// </summary>
internal static class DoublePass
{
    /// <summary>
    /// Append a second copy of every closed subpath whose length is below
    /// <paramref name="threshold"/> (in output units). No-op when threshold ≤ 0.
    /// </summary>
    internal static void ApplyToPath(SvgPath path, double threshold, double tolerance)
    {
        if (threshold <= 0 || path.Curves.Count == 0) return;
        var curves = path.Curves;
        var additions = new List<Curve>();
        int subpathStart = 0;
        while (subpathStart < curves.Count)
        {
            // Walk one continuous subpath [subpathStart..subpathEnd] (endpoints coincide => connected).
            int subpathEnd = subpathStart;
            var chain = new LineSegmentChain();
            chain.AppendFlattened(curves[subpathStart], tolerance);
            while (subpathEnd + 1 < curves.Count && Dist2(curves[subpathEnd].End, curves[subpathEnd + 1].Start) <= 1.0)
            {
                subpathEnd++;
                chain.AppendFlattened(curves[subpathEnd], tolerance);
            }

            // Closed = the subpath genuinely returns to its start. Use the exact curve
            // endpoints (Tolerances.Input = "two points identical"), not the flattened
            // chain's first/last (arc flattening leaves a small gap that would wrongly
            // reject real dots, and the loose 1-unit cutter convention would wrongly
            // accept short open arcs).
            bool closed = Dist2(curves[subpathStart].Start, curves[subpathEnd].End) <= Tolerances.Input * Tolerances.Input;

            double len = 0;
            for (int point = 1; point < chain.Count; point++)
                len += Vector.Distance(chain[point - 1], chain[point]);

            if (closed && len > 0 && len < threshold)
                for (int curve = subpathStart; curve <= subpathEnd; curve++) additions.Add(curves[curve]);

            subpathStart = subpathEnd + 1;
        }

        if (additions.Count > 0) curves.AddRange(additions);
    }

    private static double Dist2(Vector a, Vector b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
