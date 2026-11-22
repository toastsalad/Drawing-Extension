using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Layered hatch families at increasing angles, each family inked only where the element is dark enough.
/// With no image supplied, darkness is closeness to the shape's own boundary.
/// </summary>
/// <remarks>
/// A copperplate engraving builds tone by layering families and leaving the highlights bare. The tone
/// comes from <see cref="HatchFillContext.Tone"/>, so the same fill serves a vector shape (its boundary
/// tone) and a raster element (its luminance) without knowing which it is.
/// </remarks>
internal sealed class EngravingFill : IHatchFill
{
    /// <summary>How many families. Four is the classic engraver's count and keeps the darkest band legible.</summary>
    private const int Layers = 4;

    /// <summary>Angle between successive families, in degrees.</summary>
    private const double LayerAngle = 45.0;

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double insetDist = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        double arcTol = Math.Max(spacing * 0.5, 0.25);
        var clip = HatchGeometry.BuildHatchClip(ctx.Polygons, insetDist, arcTol);
        if (clip == null) return;

        var tone = ctx.Tone;

        for (int layer = 0; layer < Layers; layer++)
        {
            // Layer 0 always draws. Each later family needs a darker tone to appear, and the ramp only
            // reaches halfway so the families overlap over a wide band near the boundary instead of each
            // one claiming a thin ring.
            double threshold = Layers == 1 ? 0.0 : 0.5 * layer / (Layers - 1);
            double angle = opts.HatchAngle + layer * LayerAngle;

            var segments = HatchGeometry.HatchSegmentList(ctx.BBox, opts, angle, clip, spacing);
            foreach (var segment in segments)
                HatchGeometry.EmitGraded(ctx.Paths, segment, spacing * 0.5, point => tone.Sample(point) >= threshold);
        }
    }
}
