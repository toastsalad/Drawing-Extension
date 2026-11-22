using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Two families of parallel hatch lines at almost the same angle and almost the same spacing, so
/// they drift in and out of phase across the shape and beat into broad bands: a moiré fill.
/// </summary>
/// <remarks>
/// Traditional moiré hatching overlays two screens with slightly different pitch; the interference
/// is the picture, so the pitch difference has to be small enough that the beat period covers the
/// shape rather than repeating as visible stripes. The angle skew does the same job in the other
/// axis and stops the two families from fusing into one thick set of lines where they happen to
/// coincide.
///
/// Both families run through the same clip and connector machinery as <see cref="FillStyle.Parallel"/>,
/// so the pen still never leaves the outline.
/// </remarks>
internal sealed class MoireFill : IHatchFill
{
    /// <summary>Relative pitch difference between the two screens. Small enough to beat, not to stripe.</summary>
    private const double PitchOffset = 0.06;

    /// <summary>Angle skew in degrees between the two screens.</summary>
    private const double AngleSkew = 2.5;

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double insetDist = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        double arcTol = Math.Max(spacing * 0.5, 0.25);
        var clip = HatchGeometry.BuildHatchClip(ctx.Polygons, insetDist, arcTol);
        if (clip == null) return;

        // First screen.
        var coarse = HatchGeometry.HatchSegmentList(ctx.BBox, opts, opts.HatchAngle, clip, spacing);
        Append(ctx, coarse, spacing);

        // Second screen: same construction, slightly wider pitch and slightly turned, so the two
        // families drift through each other instead of sitting on top.
        var fine = HatchGeometry.HatchSegmentList(ctx.BBox, opts, opts.HatchAngle + AngleSkew, clip,
            spacing * (1 + PitchOffset));
        Append(ctx, fine, spacing);
    }

    private static void Append(HatchFillContext ctx, List<(Vector A, Vector B)> segments, double spacing)
    {
        double minLength = spacing * HatchGeometry.MinHatchFraction;
        foreach (var (a, b) in segments)
        {
            if ((b - a).Length < minLength) continue;
            var path = new HatchPath { Start = a };
            path.Ops.Add(new LineTo(b));
            ctx.Paths.Add(path);
        }
    }
}
