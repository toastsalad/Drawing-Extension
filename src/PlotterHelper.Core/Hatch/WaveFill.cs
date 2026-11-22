using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Sine-wave fill: reuse the parallel-sweep intersection logic to find the
/// in-polygon line segments, then bow each segment into a sampled sine polyline.
/// Each polyline is emitted as its own <see cref="HatchPath"/> (continuous; no
/// connect-ends pass). Crosshatch adds a perpendicular pass.
/// </summary>
internal sealed class WaveFill : IHatchFill
{
    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double amp = Math.Abs(opts.WaveAmplitude);
        double wavelength = opts.WaveWavelength > 0 ? opts.WaveWavelength : Math.Max(opts.HatchSpacing * 2.0, 1.0);

        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double insetDist = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        double arcTol = Math.Max(spacing * 0.5, 0.25);
        var clip = HatchGeometry.BuildHatchClip(ctx.Polygons, insetDist, arcTol);
        if (clip == null) return;

        var segments = HatchGeometry.HatchPolygonSet(ctx.BBox, opts, opts.HatchAngle, clip);
        if (opts.CrossHatch)
            segments.AddRange(HatchGeometry.HatchPolygonSet(ctx.BBox, opts, opts.HatchAngle + 90.0, clip));

        // The sine bow is added after the lines were clipped, so its crests cross the outline by up to the
        // amplitude -- visible as ink outside the drawing. Clipping by crossings alone is not enough: a
        // crest that grazes the boundary tangentially can miss the crossing test, and an interval is then
        // judged by its midpoint and kept whole, outside points included. So each point is tested too and
        // a run is split where one fails. Points are sampled at about a wavelength in eight, so what is
        // left over is far below a pen width.
        var region = HatchClipper.RegionOf(clip);
        if (region is null) return;
        var query = region.NewQuery();
        const double scale = HatchGeometry.Scale;

        foreach (var segment in segments)
        {
            var polyline = SinePolyline(segment.A, segment.B, amp, wavelength);
            var scaled = new List<Vector>(polyline.Count);
            foreach (var point in polyline) scaled.Add(new Vector(point.X * scale, point.Y * scale));

            foreach (var run in region.ClipOpen(query, scaled))
            {
                var kept = new List<Vector>(run.Count);
                foreach (var point in run)
                {
                    if (query.Contains(point))
                    {
                        kept.Add(new Vector(point.X / scale, point.Y / scale));
                        continue;
                    }

                    if (kept.Count >= 2) HatchGeometry.AddPolylinePath(ctx.Paths, kept);
                    kept = new List<Vector>(run.Count);
                }
                if (kept.Count >= 2) HatchGeometry.AddPolylinePath(ctx.Paths, kept);
            }
        }
    }

    /// <summary>Sample a sine wave along segment a→b, bulging on the left normal.</summary>
    private static List<Vector> SinePolyline(Vector start, Vector end, double amp, double wavelength)
    {
        var pts = new List<Vector>();
        Vector delta = end - start;
        double len = delta.Length;
        if (len <= 1e-9) { pts.Add(start); pts.Add(end); return pts; }

        Vector unit = delta / len;
        var normal = new Vector(-unit.Y, unit.X);
        double waveNumber = 2.0 * Math.PI / wavelength;

        // Sample at ~8 points per wavelength, capped so short segments stay cheap.
        double step = Math.Min(wavelength / 8.0, len / 4.0);
        if (step < 0.25) step = 0.25;
        int count = Math.Max(2, (int)Math.Ceiling(len / step));

        for (int sample = 0; sample <= count; sample++)
        {
            double t = (double)sample / count;
            double arcLen = t * len;
            pts.Add(start + unit * arcLen + normal * (amp * Math.Sin(waveNumber * arcLen)));
        }
        return pts;
    }
}
