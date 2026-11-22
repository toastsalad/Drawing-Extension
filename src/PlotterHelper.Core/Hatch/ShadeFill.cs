using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// A single hatch family at half the requested spacing, inked only where the element is dark enough: the
/// tonal shading you get by letting the image decide where the pen lands rather than where the lines are.
/// </summary>
/// <remarks>
/// The difference from <see cref="FillStyle.Engrave"/> is which way round the tone acts. Engrave always
/// lays the same grid and adds families where it is dark, so the line *direction* carries the tone.
/// This one lays a grid twice as fine as asked and keeps only the dark parts of it, so the *density*
/// carries the tone — the classical thresholded scanline, and the fill that photographic input wants.
///
/// With no image, the shape's boundary supplies the tone and it becomes a contour hatch that thins toward
/// the middle.
/// </remarks>
internal sealed class ShadeFill : IHatchFill
{
    /// <summary>The grid is this much finer than the requested spacing, so the tone can drop lines.</summary>
    private const double Oversample = 0.5;

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double requested = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double spacing = requested * Oversample;
        double insetDist = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        double arcTol = Math.Max(requested * 0.5, 0.25);
        var clip = HatchGeometry.BuildHatchClip(ctx.Polygons, insetDist, arcTol);
        if (clip == null) return;

        var tone = ctx.Tone;
        double threshold = Math.Clamp(opts.ToneThreshold, 0, 1);
        uint seed = (uint)opts.JitterSeed;

        // Each step of the grid is inked when the tone there beats that cell's own dither threshold, so a
        // gradient fades in and out instead of switching on at a cutoff.
        var segments = HatchGeometry.HatchSegmentList(ctx.BBox, opts, opts.HatchAngle, clip, spacing);
        foreach (var segment in segments)
            HatchGeometry.EmitDensity(ctx.Paths, segment, spacing, tone, threshold, seed);
    }
}
