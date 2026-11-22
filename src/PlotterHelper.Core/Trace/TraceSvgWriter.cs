using System.Globalization;
using System.Text;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;

namespace PlotterHelper.Trace;

/// <summary>
/// Serialize traced geometry as an SVG the existing pipeline can consume: sized in real output
/// units with the viewBox left in trace pixels, so <c>--handle-viewbox</c> scales and y-flips it
/// exactly like any hand-drawn SVG.
/// </summary>
public static class TraceSvgWriter
{
    public static string Write(IReadOnlyList<SvgPath> paths, int traceWidth, int traceHeight,
        double outputWidth, double outputHeight, string outputUnits)
    {
        var builder = new StringBuilder();
        string unitSuffix = outputUnits is "mm" or "in" ? outputUnits : "";
        builder.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" ");
        builder.Append(CultureInfo.InvariantCulture, $"width=\"{Round(outputWidth)}{unitSuffix}\" height=\"{Round(outputHeight)}{unitSuffix}\" ");
        builder.Append(CultureInfo.InvariantCulture, $"viewBox=\"0 0 {traceWidth} {traceHeight}\">\n");

        foreach (var path in paths)
        {
            string data = PathData(path.Curves);
            if (data.Length == 0) continue;

            builder.Append("  <path d=\"").Append(data).Append('"');
            if (path.Fill != null)
            {
                builder.Append(" fill=\"").Append(path.Fill).Append("\" stroke=\"none\"");
            }
            else
            {
                builder.Append(" fill=\"none\" stroke=\"").Append(path.Stroke ?? "#000000").Append('"');
                if (path.StrokeWidth != null)
                    builder.Append(" stroke-width=\"").Append(path.StrokeWidth).Append('"');
            }
            builder.Append("/>\n");
        }

        builder.Append("</svg>\n");
        return builder.ToString();
    }

    private static string PathData(IReadOnlyList<Curve> curves)
    {
        var builder = new StringBuilder();
        Vector? cursor = null;
        foreach (var curve in curves)
        {
            switch (curve)
            {
                case Line line:
                    if (cursor == null || cursor.Value != line.Start) builder.Append(CultureInfo.InvariantCulture, $"M {Round(line.Start.X)} {Round(line.Start.Y)} ");
                    builder.Append(CultureInfo.InvariantCulture, $"L {Round(line.End.X)} {Round(line.End.Y)} ");
                    break;
                case CubicBezier cubic:
                    if (cursor == null || cursor.Value != cubic.Start) builder.Append(CultureInfo.InvariantCulture, $"M {Round(cubic.Start.X)} {Round(cubic.Start.Y)} ");
                    builder.Append(CultureInfo.InvariantCulture,
                        $"C {Round(cubic.C1.X)} {Round(cubic.C1.Y)} {Round(cubic.C2.X)} {Round(cubic.C2.Y)} {Round(cubic.End.X)} {Round(cubic.End.Y)} ");
                    break;
                case QuadraticBezier quadratic:
                    if (cursor == null || cursor.Value != quadratic.Start) builder.Append(CultureInfo.InvariantCulture, $"M {Round(quadratic.Start.X)} {Round(quadratic.Start.Y)} ");
                    builder.Append(CultureInfo.InvariantCulture,
                        $"Q {Round(quadratic.C1.X)} {Round(quadratic.C1.Y)} {Round(quadratic.End.X)} {Round(quadratic.End.Y)} ");
                    break;
                default:
                    continue;
            }
            cursor = curve.End;
        }
        return builder.ToString().TrimEnd();
    }

    private static string Round(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
