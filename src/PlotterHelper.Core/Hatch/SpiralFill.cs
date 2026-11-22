using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>A single Archimedean spiral over the bbox, clipped to the polygon.
/// Each turn advances <see cref="HatchOptions.HatchSpacing"/> in radius. The clipped
/// arcs become one HatchPath each (a few pen lifts where the spiral crosses holes).
/// </summary>
internal sealed class SpiralFill : IHatchFill
{
    // The spiral clip can use a coarser scale: 0.01 mm is far below a pen's line
    // width, and Clipper's sweep is dramatically slower at the fine 1e-6 scale.
    private const double SpiralClipScale = 100.0;

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        var bbox = ctx.BBox;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double width = bbox.Xmax - bbox.Xmin, height = bbox.Ymax - bbox.Ymin;
        double cx = bbox.Xmin + width / 2.0, cy = bbox.Ymin + height / 2.0;
        double rMax = Math.Sqrt(width * width + height * height) / 2.0 + spacing;
        double radialStep = spacing / (2.0 * Math.PI);   // radial step per radian

        // Sample to a chord tolerance rather than a fixed fraction of the spacing.
        // The old spacing/4 arc step oversampled long outer turns by ~50x at fine
        // spacings (density 15 -> ~20M points), which then made Clipper's open-path
        // clip take effectively forever. 0.05 mm is far below a pen's line width.
        double chordTol = Math.Max(opts.Tolerance / 100.0, 0.05);
        var poly = new List<Vector>();
        for (double theta = 0.0; ;)
        {
            double radius = radialStep * theta;
            poly.Add(new Vector(cx + radius * Math.Cos(theta), cy + radius * Math.Sin(theta)));
            if (radius > rMax) break;
            // Chord sagitta e ~ (r*dTheta)^2 / (8r), so solve dTheta for e = chordTol.
            double dTheta = Math.Sqrt(8.0 * chordTol / Math.Max(radius, 1e-9));
            if (dTheta > 0.2) dTheta = 0.2;
            theta += dTheta;
        }
        if (poly.Count < 2) return;

        // Resolve the even-odd union once (faster than clipping against thousands of
        // raw, possibly overlapping rings), then hold the clip back by the inset so the
        // spiral stops short of the boundary like the parallel/concentric fills do.
        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        var shapes = HatchGeometry.InsetPolys(ctx.Polygons, startInset, Math.Max(spacing * 0.5, 0.25));
        if (shapes.Count == 0) return;
        var clip = HatchClipper.ToPaths64(shapes, SpiralClipScale);
        if (clip.Count == 0) return;
        foreach (var sub in HatchClipper.ClipOpenToPolygon(clip, poly, SpiralClipScale))
            HatchGeometry.AddPolylinePath(ctx.Paths, sub);
    }
}
