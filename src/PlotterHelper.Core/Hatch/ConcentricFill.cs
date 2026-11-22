using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>Nested inset rings of the shape, one closed stroke per ring. The erosion levels and their
/// bounds live in <see cref="ContourRings"/>.</summary>
internal sealed class ConcentricFill : IHatchFill
{
    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;

        foreach (var level in ContourRings.Levels(ctx.Polygons, ctx.BBox, startInset, spacing,
                     Math.Max(spacing * 0.5, 0.25)))
        {
            foreach (var ring in level) HatchGeometry.AddPolylinePath(ctx.Paths, CloseLoop(ring));
        }
    }

    /// <summary>Repeat the first vertex so the ring is drawn closed.</summary>
    internal static List<Vector> CloseLoop(Vector[] ring)
    {
        var points = new List<Vector>(ring.Length + 1);
        points.AddRange(ring);
        points.Add(ring[0]);
        return points;
    }
}
