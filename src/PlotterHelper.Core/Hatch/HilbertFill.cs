using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>A Hilbert space-filling curve over the bbox at the hatch-spacing cell
/// resolution, clipped to the polygon. Order is capped at 8 (65536 cells) so very
/// small spacing on large drawings can't explode.
/// </summary>
internal sealed class HilbertFill : IHatchFill
{
    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        var bbox = ctx.BBox;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double width = bbox.Xmax - bbox.Xmin, height = bbox.Ymax - bbox.Ymin;
        double span = Math.Max(width, height);
        if (span <= 0) return;

        int order = (int)Math.Ceiling(Math.Log2(span / spacing));
        if (order < 1) order = 1;
        if (order > 8) order = 8;          // cap: 2^8 × 2^8 = 65536 cells
        int side = 1 << order;
        double cell = span / side;

        var poly = new List<Vector>(side * side);
        for (int hilbertIndex = 0; hilbertIndex < side * side; hilbertIndex++)
        {
            D2Xy(side, hilbertIndex, out int x, out int y);
            poly.Add(new Vector(bbox.Xmin + (x + 0.5) * cell, bbox.Ymin + (y + 0.5) * cell));
        }

        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        var shapes = HatchGeometry.InsetPolys(ctx.Polygons, startInset, Math.Max(spacing * 0.5, 0.25));
        if (shapes.Count == 0) return;
        var clip = HatchClipper.ToPaths64(shapes, HatchGeometry.Scale);
        if (clip.Count == 0) return;
        foreach (var sub in HatchClipper.ClipOpenToPolygon(clip, poly))
            HatchGeometry.AddPolylinePath(ctx.Paths, sub);
    }

    // ---- Hilbert d2xy (Wikipedia "Hilbert curve" index → point) ---------------

    private static void D2Xy(int side, int index, out int x, out int y)
    {
        int rx, ry, t = index, size;
        x = 0; y = 0;
        for (size = 1; size < side; size <<= 1)
        {
            rx = (t / 2) & 1;
            ry = (t ^ rx) & 1;
            Rot(size, ref x, ref y, rx, ry);
            x += size * rx;
            y += size * ry;
            t /= 4;
        }
    }

    private static void Rot(int side, ref int x, ref int y, int rx, int ry)
    {
        if (ry == 0)
        {
            if (rx == 1) { x = side - 1 - x; y = side - 1 - y; }
            (y, x) = (x, y);
        }
    }
}
