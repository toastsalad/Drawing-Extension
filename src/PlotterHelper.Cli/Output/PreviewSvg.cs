using System.Globalization;
using System.Text;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;

namespace PlotterHelper.Cli.Output;

/// <summary>
/// Emits a preview SVG showing the original outlines and the generated hatch fills,
/// so users can eyeball the result before sending G-code to the machine. The hatch
/// geometry lives in cartesian (y-up) space, so a flip transform is applied.
/// </summary>
internal static class PreviewSvg
{
    public static string Write(Svg.SvgDocument doc, IEnumerable<HatchGroup> hatchGroups, bool showOutlines, double tolerance)
    {
        double width = doc.CanvasWidth;
        double height = doc.CanvasHeight;
        var builder = new StringBuilder();
        builder.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{FormatCoord(width)}\" height=\"{FormatCoord(height)}\" viewBox=\"0 0 {FormatCoord(width)} {FormatCoord(height)}\">\n");
        // Flip y so cartesian (y-up) geometry renders correctly in SVG (y-down).
        builder.Append($"<g transform=\"matrix(1 0 0 -1 0 {FormatCoord(height)})\">\n");

        if (showOutlines)
        {
            builder.Append("<g stroke=\"#888\" stroke-width=\"0.3\" fill=\"none\">\n");
            foreach (var path in doc.Paths)
            {
                var chain = new LineSegmentChain();
                foreach (var curve in path.Curves) chain.AppendFlattened(curve, tolerance);
                if (chain.Count > 1) builder.Append(PolylineToPath(chain.Points));
            }
            builder.Append("</g>\n");
        }

        builder.Append("<g stroke=\"#d00\" stroke-width=\"0.3\" fill=\"none\">\n");
        foreach (var group in hatchGroups)
            foreach (var path in group.Paths)
                builder.Append(HatchPathToPath(path));
        builder.Append("</g>\n");

        builder.Append("</g>\n</svg>\n");
        return builder.ToString();
    }

    // Each helper emits a complete <path d="..."/> element. Earlier versions
    // appended the bare path data ("M ... L ...") straight into the parent <g>,
    // which is invalid SVG -- renderers ignore text nodes, so the preview was blank.

    private static string PolylineToPath(List<Vector> pts)
    {
        var builder = new StringBuilder();
        builder.Append("<path d=\"M ").Append(FormatCoord(pts[0].X)).Append(' ').Append(FormatCoord(pts[0].Y));
        for (int point = 1; point < pts.Count; point++)
            builder.Append(" L ").Append(FormatCoord(pts[point].X)).Append(' ').Append(FormatCoord(pts[point].Y));
        builder.Append("\"/>\n");
        return builder.ToString();
    }

    private static string HatchPathToPath(HatchPath path)
    {
        var builder = new StringBuilder();
        builder.Append("<path d=\"M ").Append(FormatCoord(path.Start.X)).Append(' ').Append(FormatCoord(path.Start.Y));
        foreach (var hatchOp in path.Ops)
        {
            switch (hatchOp)
            {
                case LineTo line:
                    builder.Append(" L ").Append(FormatCoord(line.P.X)).Append(' ').Append(FormatCoord(line.P.Y));
                    break;
                case CubicTo cubic:
                    builder.Append(" C ").Append(FormatCoord(cubic.C1.X)).Append(' ').Append(FormatCoord(cubic.C1.Y))
                      .Append(' ').Append(FormatCoord(cubic.C2.X)).Append(' ').Append(FormatCoord(cubic.C2.Y))
                      .Append(' ').Append(FormatCoord(cubic.P.X)).Append(' ').Append(FormatCoord(cubic.P.Y));
                    break;
            }
        }
        builder.Append("\"/>\n");
        return builder.ToString();
    }

    private static string FormatCoord(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}