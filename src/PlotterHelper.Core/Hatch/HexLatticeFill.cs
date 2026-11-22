using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Three hatch families at 60 degree steps: the engraver's cross-hatch, which no pair of directions can
/// imitate because no two of the three are parallel.
/// </summary>
/// <remarks>
/// The densest of the line fills for a given spacing, and the reason hand engraving uses three
/// directions rather than two: two families leave diamond-shaped holes between them, three leave
/// triangles that close up sooner.
/// </remarks>
internal sealed class HexLatticeFill : IHatchFill
{
    private static readonly double[] Steps = [0.0, 60.0, 120.0];

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double insetDist = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        double arcTol = Math.Max(spacing * 0.5, 0.25);
        var clip = HatchGeometry.BuildHatchClip(ctx.Polygons, insetDist, arcTol);
        if (clip == null) return;

        foreach (double step in Steps)
        {
            var segments = HatchGeometry.HatchSegmentList(ctx.BBox, opts, opts.HatchAngle + step, clip, spacing);
            HatchGeometry.EmitAlternating(ctx.Paths, segments);
        }
    }
}
