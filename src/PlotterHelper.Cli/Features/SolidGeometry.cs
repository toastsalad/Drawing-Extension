using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Cli.Features;

/// <summary>
/// Solid-area geometry for <c>--connect-outlines</c>: builds the group's solid region
/// (closed outline subpaths, holes via even-odd) as polygons or as a Clipper2 path set,
/// and the Clipper2 reference predicate that says whether a connector stays inside it.
/// <see cref="SolidRegion"/> is the fast path used in emission; <see cref="SegmentWithinSolid"/>
/// is kept as the reference implementation it is cross-checked against in the tests.
/// </summary>
internal static class SolidGeometry
{
    /// <summary>
    /// The group's solid area (closed subpaths of the outline / hatch-source paths;
    /// holes via even-odd), used to gate pen-down connectors. Splits each path into its
    /// closed subpaths (like <see cref="HatchEngine.BuildPolygons"/>) so a multi-subpath
    /// path -- e.g. an outer ring plus a hole (a "donut", common in cut/vinyl work) --
    /// contributes its true solid region instead of being dropped. (Previously used
    /// <see cref="InnerBeforeOuter.FlattenClosed"/>, which flattens the whole path as one
    /// chain and drops it when first != last, leaving outlines-only drawings with holes
    /// with no solid area -- so --connect-outlines only joined through the fill when
    /// hatching was on.)
    /// </summary>
    internal static List<List<Vector>> BuildSolidPolygons(IList<SvgPath> paths, double tolerance)
    {
        var polys = new List<List<Vector>>();
        foreach (var path in paths)
            polys.AddRange(HatchEngine.BuildPolygons(path, tolerance));
        return polys;
    }

    /// <summary>Clipper2 (scaled int64) form of <see cref="BuildSolidPolygons"/>, kept for
    /// the reference <see cref="SegmentWithinSolid"/> implementation and its tests.</summary>
    internal static Paths64 BuildSolidArea(IList<SvgPath> paths, double tolerance) =>
        HatchClipper.ToPaths64(BuildSolidPolygons(paths, tolerance));

    /// <summary>
    /// True when the straight segment a->b lies entirely inside (or on the boundary
    /// of) the solid area. Clips the open segment to the polygon and accepts it iff
    /// none of its length is lost (i.e. no part crosses outside the filled region).
    /// </summary>
    internal static bool SegmentWithinSolid(Vector start, Vector end, Paths64 solid)
    {
        double fullLen = Vector.Distance(start, end);
        if (fullLen <= 1e-9) return true;
        var clipped = HatchClipper.ClipOpenToPolygon(solid, new List<Vector>(2) { start, end });
        if (clipped.Count == 0) return false;
        double total = 0;
        foreach (var path in clipped)
            for (int point = 1; point < path.Count; point++)
                total += Vector.Distance(path[point - 1], path[point]);
        return total >= fullLen - 1e-6;
    }
}
