using PlotterHelper.Geometry;
using PlotterHelper.Hatch;

namespace PlotterHelper.Cli;

/// <summary>
/// Converts generated hatch paths into geometric curves for the compiler, so a
/// hatch fill can be plotted as G-code (optionally together with the original
/// outlines). Cubic Bézier joins are preserved (and flattened by the compiler).
/// </summary>
internal static class HatchFlattener
{
    public static List<Curve> ToCurves(IEnumerable<HatchGroup> groups)
    {
        var curves = new List<Curve>();
        foreach (var group in groups)
            foreach (var path in group.Paths)
            {
                Vector prev = path.Start;
                foreach (var hatchOp in path.Ops)
                {
                    switch (hatchOp)
                    {
                        case LineTo line:
                            if (prev != line.P) curves.Add(new Line(prev, line.P));
                            prev = line.P;
                            break;
                        case CubicTo cubic:
                            curves.Add(new CubicBezier(prev, cubic.C1, cubic.C2, cubic.P));
                            prev = cubic.P;
                            break;
                    }
                }
            }
        return curves;
    }
}
