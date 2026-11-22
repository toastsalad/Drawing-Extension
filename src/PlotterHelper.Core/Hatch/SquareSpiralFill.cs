using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// A square spiral: straight runs on a square lattice whose length grows as the spiral winds out.
/// </summary>
/// <remarks>
/// The rectilinear cousin of <see cref="SpiralFill"/>. On a plotter it reads as a hand-ruled coil rather
/// than a mathematical spiral, and it suits boxy artwork that the round spirals fight against. Generated
/// from the bbox centre outwards and then clipped like any other stroke.
/// </remarks>
internal sealed class SquareSpiralFill : IHatchFill
{
    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        var bbox = ctx.BBox;
        double width = bbox.Xmax - bbox.Xmin, height = bbox.Ymax - bbox.Ymin;
        if (width <= 0 || height <= 0) return;

        double cx = bbox.Xmin + width / 2.0, cy = bbox.Ymin + height / 2.0;
        double reach = Math.Sqrt(width * width + height * height) / 2.0 + spacing;

        // Outward from the centre: two runs per length, the length growing every second run.
        var points = new List<Vector>(4096) { new(cx, cy) };
        double x = cx, y = cy;
        int run = 0;
        while (Math.Abs(x - cx) <= reach && Math.Abs(y - cy) <= reach)
        {
            int length = (run / 2) + 1;
            double step = length * spacing;
            switch (run % 4)
            {
                case 0: x += step; break;   // right
                case 1: y -= step; break;   // up (the drawing frame is y-down)
                case 2: x -= step; break;   // left
                default: y += step; break;  // down
            }
            points.Add(new Vector(x, y));
            if (run++ > 4000) break;
        }
        if (points.Count < 2) return;

        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        var shapes = HatchGeometry.InsetPolys(ctx.Polygons, startInset, Math.Max(spacing * 0.5, 0.25));
        if (shapes.Count == 0) return;
        var clip = HatchClipper.ToPaths64(shapes, HatchGeometry.Scale);
        if (clip.Count == 0) return;

        foreach (var sub in HatchClipper.ClipOpenToPolygon(clip, points))
            HatchGeometry.AddPolylinePath(ctx.Paths, sub);
    }
}
