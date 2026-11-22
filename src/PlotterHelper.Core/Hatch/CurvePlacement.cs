using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Places a curve that was generated in its own units — a space-filling curve built from unit steps, a
/// tiling in cell coordinates — into the element's bounding box.
/// </summary>
/// <remarks>
/// Curves like the dragon and the Gosper flowsnake are easiest to build on an integer lattice and there
/// is no tidy closed form for their extent, so they are generated first and fitted afterwards. Fitting
/// keeps the aspect ratio: a curve that is taller than it is wide must not be stretched to fill a wide
/// box, or the ink thins out on one axis.
/// </remarks>
internal static class CurvePlacement
{
    internal static List<Vector> Fit(List<Vector> points, HatchBBox bbox, double margin = 0.0)
    {
        var result = new List<Vector>(points.Count);
        if (points.Count == 0) return result;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            if (point.X < minX) minX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.X > maxX) maxX = point.X;
            if (point.Y > maxY) maxY = point.Y;
        }

        double width = maxX - minX, height = maxY - minY;
        double boxWidth = Math.Max(0, bbox.Xmax - bbox.Xmin - 2 * margin);
        double boxHeight = Math.Max(0, bbox.Ymax - bbox.Ymin - 2 * margin);
        if (width <= 0 && height <= 0) return result;

        // One scale for both axes, so the curve keeps its shape.
        double scale = Math.Min(
            width > 0 ? boxWidth / width : double.MaxValue,
            height > 0 ? boxHeight / height : double.MaxValue);
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;

        double offsetX = bbox.Xmin + margin + (boxWidth - width * scale) / 2 - minX * scale;
        double offsetY = bbox.Ymin + margin + (boxHeight - height * scale) / 2 - minY * scale;

        foreach (var point in points)
            result.Add(new Vector(point.X * scale + offsetX, point.Y * scale + offsetY));
        return result;
    }
}
