using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Parallel hatch lines joined end to end into back-and-forth strokes: the "lawnmower" or
/// boustrophedon fill.
/// </summary>
/// <remarks>
/// This is the fill everyone reaches for on a machine that hates pen lifts. Where
/// <see cref="FillStyle.Parallel"/> emits one stroke per hatch line and relies on connect-ends to
/// weld nearby ends together by heuristic, this emits the lines in the order a mower cuts a lawn, so
/// the join is the *plan* rather than a guess: the pen is already at the end of a line and the next
/// line begins one spacing away, on the other side.
///
/// The chaining is done per connected run rather than per line, because one hatch line through a
/// shape with a hole arrives as two runs, and welding the wrong pair would draw across the hole.
/// Runs are consumed in line order and each is entered at whichever end is nearer the pen; a gap
/// wider than <see cref="JoinGapFactor"/> spacings ends the stroke instead of cutting the corner.
/// </remarks>
internal sealed class SerpentineFill : IHatchFill
{
    /// <summary>A jump wider than this many spacings starts a new stroke rather than drawing across.</summary>
    private const double JoinGapFactor = 2.5;

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double insetDist = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        var clip = HatchGeometry.BuildHatchClip(
            ctx.Polygons, insetDist, Math.Max(spacing * 0.5, 0.25));
        if (clip == null) return;

        // The same grid the parallel fill uses, taken as segments in line order. One line may arrive as
        // several segments when a hole interrupts it; they stay adjacent in this list, which is what
        // makes the chaining below safe.
        var runs = HatchGeometry.HatchSegmentList(ctx.BBox, opts, opts.HatchAngle, clip, spacing);
        if (runs.Count == 0) return;

        // Joining two runs draws a straight move across the gap, and a straight move between two points
        // inside a concave shape can still leave it -- across a notch, or across a hole. Every connector
        // is therefore sampled against the region first; one that leaves starts a new stroke instead.
        var region = HatchClipper.RegionOf(clip);
        var query = region?.NewQuery();
        bool inside(Vector from, Vector to)
        {
            var scratch = query;
            if (scratch is null) return true;
            const int samples = 4;
            for (int i = 1; i < samples; i++)
            {
                var probe = new Vector(
                    (from.X + (to.X - from.X) * i / samples) * HatchGeometry.Scale,
                    (from.Y + (to.Y - from.Y) * i / samples) * HatchGeometry.Scale);
                if (!scratch.Contains(probe)) return false;
            }
            return true;
        }

        double joinGap = spacing * JoinGapFactor;
        double joinGapSq = joinGap * joinGap;

        HatchPath? current = null;
        Vector pen = default;

        foreach (var run in runs)
        {
            Vector head = run.A, tail = run.B;
            bool forward = current is null
                || (head - pen).LengthSquared <= (tail - pen).LengthSquared;
            Vector entry = forward ? head : tail;
            Vector exit = forward ? tail : head;

            bool reachable = current is not null
                && (entry - pen).LengthSquared <= joinGapSq
                && inside(pen, entry);

            if (!reachable)
            {
                // Nothing reachable: close what we have and start a new stroke at this run.
                if (current is not null) ctx.Paths.Add(current);
                current = new HatchPath { Start = entry };
                current.Ops.Add(new LineTo(exit));
            }
            else
            {
                // Reachable: draw the connector into this run, then along it. Emitting only the exit point
                // would draw one straight move from the previous end to this run's far end -- a diagonal
                // across the shape, which is what put ink outside the outline.
                if (!SamePoint(current!.Ops, entry)) current.Ops.Add(new LineTo(entry));
                current.Ops.Add(new LineTo(exit));
            }

            pen = exit;

            static bool SamePoint(List<HatchOp> ops, Vector point) =>
                ops.Count > 0 && ops[^1] is LineTo last && (last.P - point).Length < 1e-9;
        }

        if (current is not null) ctx.Paths.Add(current);
    }
}
