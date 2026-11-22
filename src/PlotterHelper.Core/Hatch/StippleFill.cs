using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Stippling: dots on a jittered lattice, kept where the element is dark enough. Marks rather than lines.
/// </summary>
/// <remarks>
/// A generator, not a pattern: each mark is decided independently from the tone at its own position, which
/// is exactly the contract <see cref="IGenerator"/> describes. Nothing is clipped, because nothing is drawn
/// where the tone says not to and the region says the point is on the paper.
///
/// A pen cannot make a dot, so a dot is a very short stroke. A stipple has no direction at all, which is
/// the texture line hatching cannot reach, and it is the natural fill for photographic input.
///
/// The lattice is jittered by a hash of the cell, so the dots do not line up into visible rows, and the
/// result is reproducible from <see cref="HatchOptions.JitterSeed"/>.
/// </remarks>
internal sealed class StippleFill : IGenerator
{
    /// <summary>Lattice pitch as a multiple of the requested spacing: one dot per cell that is dark.</summary>
    private const double PitchFactor = 1.0;

    /// <summary>Fraction of the pitch a dot may wander, so the lattice does not read as a grid.</summary>
    private const double JitterFactor = 0.7;

    /// <summary>Length of the stroke that stands in for a dot, in user units.</summary>
    private const double DotLength = 0.25;

    public void Generate(GeneratorContext ctx)
    {
        double pitch = ctx.Spacing * PitchFactor;
        double threshold = ctx.SeedThreshold;
        var bbox = ctx.BBox;
        uint seed = (uint)ctx.Options.JitterSeed;

        for (double y = bbox.Ymin; y <= bbox.Ymax; y += pitch)
        {
            for (double x = bbox.Xmin; x <= bbox.Xmax; x += pitch)
            {
                uint hash = Hash(seed, x, y);
                double jitterX = ((hash & 0xFF) / 255.0 - 0.5) * pitch * JitterFactor;
                double jitterY = (((hash >> 8) & 0xFF) / 255.0 - 0.5) * pitch * JitterFactor;

                var centre = new Vector(x + jitterX, y + jitterY);

                // The region decides what is on the paper, so no margin arithmetic is needed: a dot near
                // the outline is dropped if its jitter takes it outside.
                if (!ctx.Inside(centre)) continue;
                // Density, not a gate: a half-tone patch gets half the dots, spread evenly by the dither.
                if (!ctx.WantsMark(centre, pitch)) continue;

                // A short stroke through the lattice point, angled from the same hash so the dots do not all
                // lie in one direction.
                double angle = ((hash >> 16) & 0xFF) / 255.0 * Math.PI;
                var half = new Vector(Math.Cos(angle), Math.Sin(angle)) * (DotLength / 2);
                var from = centre - half;
                var to = centre + half;

                // Both ends, not just the centre: a dot centred just inside the outline still pokes out by
                // half its length.
                if (!ctx.Inside(from) || !ctx.Inside(to)) continue;
                HatchGeometry.AddPolylinePath(ctx.Paths, new List<Vector> { from, to });
            }
        }
    }

    private static uint Hash(uint seed, double x, double y)
    {
        unchecked
        {
            uint h = seed ^ (uint)((int)Math.Floor(x) * 73856093) ^ (uint)((int)Math.Floor(y) * 19349663);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return h;
        }
    }
}
