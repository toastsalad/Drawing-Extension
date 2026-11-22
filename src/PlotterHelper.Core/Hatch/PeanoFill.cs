using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// A Peano space-filling curve over the bbox at the hatch-spacing cell resolution, clipped to the
/// polygon. Order is capped at 5 (243×243 = 59049 cells) so a fine spacing on a large drawing cannot
/// explode.
/// </summary>
/// <remarks>
/// The recursion is the meander form from Breinholt and Schierz, "A recursive algorithm for the
/// generation of space-filling curves" (EUSIPCO 1998), listing 1. It carries two flags rather than a
/// rotation matrix: <c>i1</c> says whether the sub-curve starts at the lower-left or upper-right
/// corner of its cell block and <c>i2</c> whether it ends at the lower-right or upper-left, so the
/// nine recursive calls are the whole algorithm. Every cell of the 3^k grid is visited once and
/// consecutive points are one cell apart, which is what <see cref="HatchStyleTests"/> checks.
///
/// It is the base-3 cousin of <see cref="HilbertFill"/>: same long uninterrupted stroke, coarser
/// texture because each level divides into nine rather than four.
/// </remarks>
internal sealed class PeanoFill : IHatchFill
{
    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        var bbox = ctx.BBox;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double width = bbox.Xmax - bbox.Xmin, height = bbox.Ymax - bbox.Ymin;
        double span = Math.Max(width, height);
        if (span <= 0) return;

        // Smallest power of three whose cells fit inside the requested spacing.
        int order = 1;
        int side = 3;
        while (side < span / spacing && order < 5)
        {
            side *= 3;
            order++;
        }
        double cell = span / side;

        var poly = new List<Vector>(side * side);
        Emit(poly, bbox, cell, 0, 0, side, 0, 0);
        if (poly.Count < 2) return;

        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        var shapes = HatchGeometry.InsetPolys(ctx.Polygons, startInset, Math.Max(spacing * 0.5, 0.25));
        if (shapes.Count == 0) return;
        var clip = HatchClipper.ToPaths64(shapes, HatchGeometry.Scale);
        if (clip.Count == 0) return;
        foreach (var sub in HatchClipper.ClipOpenToPolygon(clip, poly))
            HatchGeometry.AddPolylinePath(ctx.Paths, sub);
    }

    /// <summary>
    /// Breinholt–Schierz meander Peano. <paramref name="lg"/> is the current block width in cells,
    /// <paramref name="i1"/> the start corner (0 = lower left, 1 = upper right) and
    /// <paramref name="i2"/> the end corner (0 = lower right, 1 = upper left), both in cell units.
    /// </summary>
    private static void Emit(List<Vector> poly, HatchBBox bbox, double cell,
        int x, int y, int lg, int i1, int i2)
    {
        if (lg == 1)
        {
            poly.Add(new Vector(bbox.Xmin + (x + 0.5) * cell, bbox.Ymin + (y + 0.5) * cell));
            return;
        }

        lg /= 3;
        Emit(poly, bbox, cell, x + 2 * i1 * lg, y + 2 * i1 * lg, lg, i1, i2);
        Emit(poly, bbox, cell, x + (i1 - i2 + 1) * lg, y + (i1 + i2) * lg, lg, i1, 1 - i2);
        Emit(poly, bbox, cell, x + lg, y + lg, lg, i1, 1 - i2);
        Emit(poly, bbox, cell, x + (i1 + i2) * lg, y + (i1 - i2 + 1) * lg, lg, 1 - i1, 1 - i2);
        Emit(poly, bbox, cell, x + 2 * i2 * lg, y + 2 * (1 - i2) * lg, lg, i1, i2);
        Emit(poly, bbox, cell, x + (1 + i2 - i1) * lg, y + (2 - i1 - i2) * lg, lg, i1, i2);
        Emit(poly, bbox, cell, x + 2 * (1 - i1) * lg, y + 2 * (1 - i1) * lg, lg, i1, i2);
        Emit(poly, bbox, cell, x + (2 - i1 - i2) * lg, y + (1 + i2 - i1) * lg, lg, 1 - i1, i2);
        Emit(poly, bbox, cell, x + 2 * (1 - i2) * lg, y + 2 * i2 * lg, lg, 1 - i1, i2);
    }
}
