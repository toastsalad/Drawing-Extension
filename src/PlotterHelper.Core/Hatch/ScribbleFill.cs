using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Scribble: a short wandering stroke in each cell of a lattice, as long as the picture there is dark.
/// </summary>
/// <remarks>
/// The point is a fill that reads as hand scribble rather than as a pattern, so a cell is scribbled over about
/// once when it is black and left alone when it is white, and the picture comes through as the density of the
/// strokes.
///
/// This replaces a version whose strokes were free to go wherever they liked: seeds four spacings apart, and
/// each stroke allowed a hundred spacings of ink, so a stroke started in one part of a photograph wandered into
/// another and the result was an even grey scrawl that resembled nothing. A stroke now stays inside its own
/// cell, and how much ink that cell gets is decided by the tone at its centre. Gating seeds alone cannot do
/// that: a stroke that starts in shadow and wanders ends up anywhere, and strokes long enough to look like
/// scribble are long enough to wander.
///
/// Total ink is then bounded by the picture and the spacing, not by a budget: a cell gets at most a couple of
/// cell widths of stroke, so the fill puts down about one spacing's worth of ink per unit of area, the same as
/// a line fill. That matters because the previous version stopped seeding when a global budget ran out, which
/// truncated the drawing from wherever the lattice had got to -- half a picture, cut on a horizontal line.
/// </remarks>
internal sealed class ScribbleFill : IGenerator
{
    /// <summary>
    /// Seed lattice pitch as a multiple of the hatch spacing. Two, so that a cell is about four spacings of
    /// area: fine enough to follow the picture, coarse enough that neighbouring scribbles are distinguishable.
    /// </summary>
    private const double CellFactor = 2.0;

    /// <summary>Smallest cell, so a fine spacing does not turn the scribble into noise.</summary>
    private const double MinCell = 1.2;

    /// <summary>Step length as a fraction of the spacing: small enough to curve, large enough to be ink.</summary>
    private const double StepFactor = 0.35;

    /// <summary>Shortest step, so move count does not explode at a fine spacing.</summary>
    private const double MinStep = 0.35;

    /// <summary>How far the heading may drift per step, in radians. Small, or it turns into noise.</summary>
    private const double Drift = 0.55;

    /// <summary>
    /// Ink a fully dark cell receives, in units of the cell width. A little over two, which scribbles the cell
    /// over about twice and leaves the paper showing between the strokes.
    /// </summary>
    private const double InkPerCell = 2.2;

    public void Generate(GeneratorContext ctx)
    {
        double pitch = Math.Max(ctx.Spacing * CellFactor, MinCell);
        double step = Math.Max(ctx.Spacing * StepFactor, MinStep);
        double half = pitch * 0.5;
        var bbox = ctx.BBox;

        for (double y = bbox.Ymin; y <= bbox.Ymax; y += pitch)
        {
            for (double x = bbox.Xmin; x <= bbox.Xmax; x += pitch)
            {
                // Jitter the seed so the lattice does not show through the scribble.
                var seed = new Vector(
                    x + (ctx.Random.NextDouble() - 0.5) * pitch * 0.8,
                    y + (ctx.Random.NextDouble() - 0.5) * pitch * 0.8);

                if (!ctx.Inside(seed)) continue;

                // How dark it is here decides both whether this cell is scribbled at all and how much ink it
                // gets, which is what makes the picture appear: a cell at half tone is marked half the time,
                // and when it is marked its stroke is half as long.
                double level = ctx.InkLevel(seed);
                if (level <= HatchGeometry.Dither(seed, pitch, (uint)ctx.Options.JitterSeed)) continue;

                // The floor keeps the lightest marked cells a short stroke rather than a single speck: a
                // scribble made of isolated dots reads as dirt on the paper, not as a tone.
                double target = Math.Max(level * pitch * InkPerCell, step * 2.5);
                var walk = Walk(ctx, seed, new Vector(x, y), step, target, half);
                if (walk.Count >= 2) HatchGeometry.AddPolylinePath(ctx.Paths, walk);
            }
        }
    }

    /// <summary>
    /// One stroke: step, drift the heading, turn away from the edge of its cell and from the edge of the shape,
    /// and stop when it has laid down the ink its cell asked for.
    /// </summary>
    /// <remarks>
    /// The cell is what keeps the stroke honest. Without it the walk is free to leave, and a stroke that leaves
    /// carries ink into a lighter part of the picture -- which is what made the previous version look like a
    /// scrawl rather than like a picture. Its box is the lattice cell, not a box around the jittered seed, so
    /// neighbouring strokes partition the paper instead of overlapping.
    /// </remarks>
    private static List<Vector> Walk(GeneratorContext ctx, Vector start, Vector centre, double step,
        double target, double half)
    {
        var points = new List<Vector>(64) { start };
        Vector pen = start;
        double heading = ctx.Random.NextDouble() * Math.PI * 2;
        double ink = 0;

        while (ink < target)
        {
            heading += (ctx.Random.NextDouble() - 0.5) * Drift;

            if (!Advance(ctx, pen, ref heading, step, centre, half, out Vector next)) break;

            ink += (next - pen).Length;
            points.Add(next);
            pen = next;
        }

        return points;
    }

    /// <summary>
    /// Find a direction and a length that stay on the paper and inside the cell, preferring the current heading
    /// and the full step.
    /// </summary>
    /// <remarks>
    /// Shrinking the step matters more than it looks. Near the edge of a cell, or in a narrow shape -- a star's
    /// arm, a thin stroke -- no step of the full length fits, so a walk that can only turn stops after a point or
    /// two and the cell comes out dotted rather than scribbled. Halving the step twice before giving up lets the
    /// stroke fill the space that is there.
    /// </remarks>
    private static bool Advance(GeneratorContext ctx, Vector pen, ref double heading, double step,
        Vector centre, double half, out Vector next)
    {
        for (int shrink = 0; shrink < 3; shrink++)
        {
            double length = step * Math.Pow(0.5, shrink);
            for (int turn = 0; turn <= 12; turn++)
            {
                double delta = turn * (Math.PI / 12);
                if (TryStep(ctx, pen, heading + delta, length, centre, half, out next))
                {
                    heading += delta;
                    return true;
                }
                if (turn > 0 && TryStep(ctx, pen, heading - delta, length, centre, half, out next))
                {
                    heading -= delta;
                    return true;
                }
            }
        }

        next = pen;
        return false;
    }

    private static bool TryStep(GeneratorContext ctx, Vector from, double heading, double step,
        Vector centre, double half, out Vector next)
    {
        next = from + new Vector(Math.Cos(heading), Math.Sin(heading)) * step;

        // The cell is a square around the lattice point, so a stroke stays where its tone was read.
        if (Math.Abs(next.X - centre.X) > half) return false;
        if (Math.Abs(next.Y - centre.Y) > half) return false;
        return ctx.Inside(next);
    }
}
