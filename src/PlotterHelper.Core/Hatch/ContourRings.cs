using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// The nested levels of a shape: level 0 is the shape inset by the requested amount, and each later level is
/// one hatch spacing further in.
/// </summary>
/// <remarks>
/// Shared by <see cref="ConcentricFill"/>, which draws each level as closed rings, and
/// <see cref="ContourSpiralFill"/>, which joins the levels into one continuous stroke.
///
/// The levels come from <see cref="ContourField"/>: iso-contours of the distance to the shape's own boundary,
/// read at every level. This used to erode iteratively -- offset inward by a spacing, then offset the result --
/// and carried three faults that could not be fixed from here. Rings were filtered by enclosed area, which
/// discards a ring in a corridor because it is long and thin. They were filtered by "is a point of this ring
/// inside the shape", which is true of an outer edge and false of the edge of a hole, so holes were never
/// ringed. And a folded ring could survive every erosion unchanged, so the loop ran to its guard emitting the
/// same small loop over and over.
/// </remarks>
internal static class ContourRings
{
    internal static List<List<Vector[]>> Levels(List<List<Vector>> polygons, HatchBBox bbox,
        double startInset, double spacing, double arcTol)
    {
        _ = arcTol;

        // A ring worth drawing is a question about ink, not about enclosed ground: a ring in a corridor is
        // sixty millimetres of perimeter around a few square millimetres.
        double minPerimeter = Math.Max(spacing * 2, 1.0);

        // Detail far below the spacing cannot survive being read at that spacing, and a flattened drawing
        // arrives with vertices a hundredth of a millimetre apart.
        double simplify = Math.Max(spacing * 0.02, 0.1);
        var simplified = new List<List<Vector>>(polygons.Count);
        foreach (var ring in polygons)
        {
            var reduced = Polygons.Simplify(ring, simplify);
            if (reduced.Count >= 3) simplified.Add(reduced);
        }
        if (simplified.Count == 0) return new List<List<Vector[]>>();

        return ContourField.Rings(simplified, bbox, startInset, spacing, minPerimeter);
    }
}
