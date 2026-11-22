namespace PlotterHelper.Hatch;

/// <summary>
/// Parallel straight hatch lines, the axidraw/eggbot_hatch behaviour: a grid of lines
/// at the requested angle, clipped to the (optionally inset) polygon, either connected
/// end-to-end with Bézier joins or emitted as alternating separate strokes.
/// </summary>
internal sealed class ParallelFill : IHatchFill
{
    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        // Build the clip polygon once and share it with the hatch-line
        // intersection and the post-connect safety clip.
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double insetDist = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        double arcTol = Math.Max(spacing * 0.5, 0.25);
        var clip = HatchGeometry.BuildHatchClip(ctx.Polygons, insetDist, arcTol);
        if (clip == null) return;

        var segments = HatchGeometry.HatchPolygonSet(ctx.BBox, opts, opts.HatchAngle, clip);
        if (opts.CrossHatch)
            segments.AddRange(HatchGeometry.HatchPolygonSet(ctx.BBox, opts, opts.HatchAngle + 90.0, clip));
        if (segments.Count == 0) return;
        if (opts.ConnectEnds)
        {
            PenLiftOptimizer.Connect(ctx.Paths, segments, opts);
            // Connect-ends Béziers can bow across a concave boundary by a
            // fraction of a mm; clip the assembled paths so the pen never
            // leaves the outline. (Without connect-ends the segments are
            // already Clipper-clipped straight runs.)
            HatchGeometry.ClipPathsToPolygon(ctx.Paths, clip, ctx.FlattenTolerance);
        }
        else
            HatchGeometry.EmitAlternating(ctx.Paths, segments);
    }
}
