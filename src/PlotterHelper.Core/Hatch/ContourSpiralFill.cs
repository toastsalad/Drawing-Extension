using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// The same nested rings as <see cref="ConcentricFill"/>, joined into a single continuous stroke.
/// </summary>
/// <remarks>
/// This is the fill a plotter wants most: it follows the shape like concentric rings, but the pen never
/// lifts. Each ring is entered at the vertex nearest where the pen already is, and the short move from
/// the previous ring to that vertex becomes part of the stroke instead of a lift.
///
/// It also fixes what the Archimedean and Fermat spirals cannot do. Those are fixed curves clipped by the
/// shape, so they ignore the boundary and waste ink outside it; this one is built from the shape's own
/// eroding contour, so the spacing is even everywhere and the ink stops exactly at the inset.
/// </remarks>
internal sealed class ContourSpiralFill : IHatchFill
{
    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;

        var levels = ContourRings.Levels(ctx.Polygons, ctx.BBox, startInset, spacing,
            Math.Max(spacing * 0.5, 0.25));
        if (levels.Count == 0) return;

        HatchPath? current = null;
        Vector pen = default;

        foreach (var level in levels)
        {
            foreach (var ring in ByDistanceFromPen(level, pen, current is not null))
            {
                // Enter at the vertex nearest the pen, so the join is the shortest move available.
                var ordered = Rotated(ring, pen, current is not null);

                if (current is null)
                {
                    current = new HatchPath { Start = ordered[0] };
                }
                else
                {
                    // A joining move across one ring gap is fine; a long one means a different island,
                    // which reads better as its own stroke than as a line across the drawing.
                    if ((ordered[0] - pen).Length > spacing * 3)
                    {
                        ctx.Paths.Add(current);
                        current = new HatchPath { Start = ordered[0] };
                    }
                    else
                    {
                        current.Ops.Add(new LineTo(ordered[0]));
                    }
                }

                for (int i = 1; i < ordered.Count; i++) current.Ops.Add(new LineTo(ordered[i]));
                current.Ops.Add(new LineTo(ordered[0]));   // close the ring
                pen = ordered[0];
            }
        }

        if (current is not null) ctx.Paths.Add(current);
    }

    /// <summary>Rings ordered by how close their nearest vertex is to the pen, so the joins stay short.</summary>
    private static IEnumerable<Vector[]> ByDistanceFromPen(List<Vector[]> level, Vector pen, bool havePen)
    {
        if (!havePen || level.Count < 2) return level;

        return level
            .Select(ring => (ring, distance: NearestDistance(ring, pen)))
            .OrderBy(entry => entry.distance)
            .Select(entry => entry.ring)
            .ToList();
    }

    private static double NearestDistance(Vector[] ring, Vector pen)
    {
        double best = double.MaxValue;
        foreach (var point in ring)
        {
            double d = (point - pen).LengthSquared;
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>The ring rotated to begin at its vertex nearest <paramref name="pen"/>.</summary>
    private static List<Vector> Rotated(Vector[] ring, Vector pen, bool havePen)
    {
        int start = 0;
        if (havePen)
        {
            double best = double.MaxValue;
            for (int i = 0; i < ring.Length; i++)
            {
                double d = (ring[i] - pen).LengthSquared;
                if (d < best)
                {
                    best = d;
                    start = i;
                }
            }
        }

        var ordered = new List<Vector>(ring.Length);
        for (int i = 0; i < ring.Length; i++) ordered.Add(ring[(start + i) % ring.Length]);
        return ordered;
    }
}
