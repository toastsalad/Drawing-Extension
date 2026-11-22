using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// A single Fermat (parabolic) spiral, r = a·√θ, clipped to the shape.
/// </summary>
/// <remarks>
/// The Archimedean spiral in <see cref="SpiralFill"/> advances the radius by a constant amount per
/// turn, so its turns are equally spaced. The Fermat spiral's turns crowd together as they go out,
/// which reads as a vignette and is why plotter artists use it: one continuous stroke, no pen lift
/// except where it crosses a hole, and a natural fade from dense centre to open edge.
///
/// The scaling constant is chosen so the innermost turn is about one hatch spacing across; because
/// the gap between turn k and k+1 is a·π/√θ, the outward spacing settles to roughly half a spacing
/// and thins gradually, so the same <see cref="HatchOptions.HatchSpacing"/> gives a comparable ink
/// weight to the other fills rather than a solid disc.
/// </remarks>
internal sealed class FermatFill : IHatchFill
{
    /// <summary>Coarse clip scale: 0.01 mm is far below a pen line width and much cheaper to clip.</summary>
    private const double ClipScale = 100.0;

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        var bbox = ctx.BBox;
        double width = bbox.Xmax - bbox.Xmin, height = bbox.Ymax - bbox.Ymin;
        if (width <= 0 || height <= 0) return;

        double cx = bbox.Xmin + width / 2.0, cy = bbox.Ymin + height / 2.0;
        double rMax = Math.Sqrt(width * width + height * height) / 2.0 + spacing;

        // r = a√θ. Choose a so the outermost turn's radial pitch is about one spacing: the gap between
        // turn k and k+1 is aπ/√θ, and θmax = (rMax/a)², so setting that gap to `spacing` gives
        // a = √(spacing·rMax/π). Fixing a at a multiple of the spacing instead made the whole curve
        // shorter than a single turn on a small shape.
        double a = Math.Sqrt(spacing * rMax / Math.PI);
        double thetaMax = (rMax / a) * (rMax / a);

        double chordTol = Math.Max(opts.Tolerance / 100.0, 0.05);
        var poly = new List<Vector>(1024);
        for (double theta = 0.0; theta <= thetaMax;)
        {
            double radius = a * Math.Sqrt(theta);
            poly.Add(new Vector(cx + radius * Math.Cos(theta), cy + radius * Math.Sin(theta)));

            // dθ from the chord sagitta, e ≈ (r·dθ)² / (8r). Guard the centre where r → 0.
            double dTheta = Math.Sqrt(8.0 * chordTol / Math.Max(radius, 1e-9));
            if (dTheta > 0.25) dTheta = 0.25;
            theta += dTheta;
        }
        if (poly.Count < 2) return;

        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        var shapes = HatchGeometry.InsetPolys(ctx.Polygons, startInset, Math.Max(spacing * 0.5, 0.25));
        if (shapes.Count == 0) return;
        var clip = HatchClipper.ToPaths64(shapes, ClipScale);
        if (clip.Count == 0) return;

        foreach (var sub in HatchClipper.ClipOpenToPolygon(clip, poly, ClipScale))
            HatchGeometry.AddPolylinePath(ctx.Paths, sub);
    }
}
