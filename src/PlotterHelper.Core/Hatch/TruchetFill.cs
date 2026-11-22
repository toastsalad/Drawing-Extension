using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Truchet tiling: a grid of tiles, each holding two quarter-arcs, with the corner pair chosen at
/// random. The arcs meet their neighbours at the tile edges, so the result is one flowing pattern of
/// curls with no straight lines anywhere.
/// </summary>
/// <remarks>
/// The cheapest way to make a fill that does not look like hatching. Every tile is one of two
/// orientations, and both connect the same four edge midpoints, so the pattern is continuous whatever the
/// random choice — that is Truchet's property, and it is why the tiling never breaks into dead ends.
///
/// The random choice is seeded from the options, so the same drawing always produces the same tiles; a
/// fill that changes when you re-render is useless for comparing two settings.
/// </remarks>
internal sealed class TruchetFill : IHatchFill
{
    /// <summary>Tile size as a multiple of the hatch spacing: three gives visible curls at default spacing.</summary>
    private const double TileFactor = 3.0;

    /// <summary>Points per quarter arc.</summary>
    private const int ArcSteps = 8;

    public void Fill(HatchFillContext ctx)
    {
        var opts = ctx.Options;
        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double tile = spacing * TileFactor;
        var bbox = ctx.BBox;
        double width = bbox.Xmax - bbox.Xmin, height = bbox.Ymax - bbox.Ymin;
        if (width <= 0 || height <= 0) return;

        double startInset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
        var shapes = HatchGeometry.InsetPolys(ctx.Polygons, startInset, Math.Max(spacing * 0.5, 0.25));
        if (shapes.Count == 0) return;
        var clip = HatchClipper.ToPaths64(shapes, HatchGeometry.Scale);
        if (clip.Count == 0) return;

        // A simple integer hash of the cell index: deterministic, no shared RNG state, so tiles do not
        // depend on the order they are visited.
        uint seed = (uint)opts.JitterSeed;

        for (double y = bbox.Ymin; y < bbox.Ymax; y += tile)
        {
            for (double x = bbox.Xmin; x < bbox.Xmax; x += tile)
            {
                uint hash = Hash(seed, x, y);
                bool flipped = (hash & 1) != 0;

                // Both orientations connect the four edge midpoints; the two arcs are centred on opposite
                // corners, which is what makes the neighbours meet.
                var first = Arc(x, y, tile, flipped ? ArcCorner.TopLeft : ArcCorner.BottomLeft);
                var second = Arc(x, y, tile, flipped ? ArcCorner.BottomRight : ArcCorner.TopRight);

                foreach (var arc in new[] { first, second })
                    foreach (var run in HatchClipper.ClipOpenToPolygon(clip, arc))
                        HatchGeometry.AddPolylinePath(ctx.Paths, run);
            }
        }
    }

    private enum ArcCorner { BottomLeft, BottomRight, TopLeft, TopRight }

    /// <summary>
    /// A quarter arc of radius half a tile, centred on one corner, from one edge midpoint to another.
    /// </summary>
    private static List<Vector> Arc(double x, double y, double tile, ArcCorner corner)
    {
        double half = tile / 2;
        double cx = corner is ArcCorner.BottomLeft or ArcCorner.TopLeft ? x : x + tile;
        double cy = corner is ArcCorner.BottomLeft or ArcCorner.BottomRight ? y + tile : y;

        // Start angle points at whichever edge midpoint the arc begins from, then sweep a quarter turn.
        double startAngle = corner switch
        {
            ArcCorner.BottomLeft => -Math.PI / 2,   // up
            ArcCorner.BottomRight => Math.PI,      // left
            ArcCorner.TopLeft => 0,                // right
            _ => Math.PI / 2,                      // down
        };

        var points = new List<Vector>(ArcSteps + 1);
        for (int step = 0; step <= ArcSteps; step++)
        {
            double angle = startAngle + (Math.PI / 2) * step / ArcSteps;
            points.Add(new Vector(cx + half * Math.Cos(angle), cy + half * Math.Sin(angle)));
        }
        return points;
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
