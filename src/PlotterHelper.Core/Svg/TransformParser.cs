using System.Globalization;
using PlotterHelper.Geometry;

namespace PlotterHelper.Svg;

/// <summary>
/// Parses SVG transform attribute strings (matrix/translate/scale/rotate/skewX/skewY)
/// into an <see cref="Affine"/>. Port of svg_to_gcode's Transformation, but built
/// directly on the value-type Affine matrix.
/// </summary>
public static class TransformParser
{
    public static Affine Parse(string text)
    {
        Affine result = Affine.Identity;
        int pos = 0;
        while (pos < text.Length)
        {
            // Read function name.
            while (pos < text.Length && text[pos] is ' ' or '\t' or '\n' or '\r' or ',') pos++;
            int nameStart = pos;
            while (pos < text.Length && (char.IsLetter(text[pos]))) pos++;
            string name = text[nameStart..pos];

            // Read argument list inside parentheses.
            while (pos < text.Length && text[pos] != '(') pos++;
            if (pos >= text.Length) break;
            pos++; // skip '('
            int argStart = pos;
            int depth = 1;
            while (pos < text.Length && depth > 0)
            {
                if (text[pos] == '(') depth++;
                else if (text[pos] == ')') { depth--; if (depth == 0) break; }
                pos++;
            }
            string argStr = text[argStart..pos];
            if (pos < text.Length) pos++; // skip ')'
            var args = ParseArgs(argStr);

            result = result.Compose(Build(name, args));
        }
        return result;
    }

    private static Affine Build(string name, double[] values) => name switch
    {
        "matrix" => new Affine(values[0], values[1], values[2], values[3], values[4], values[5]),
        "translate" => Affine.Translation(values[0], values.Length > 1 ? values[1] : 0),
        "scale" => Affine.Scale(values[0], values.Length > 1 ? values[1] : values[0]),
        "rotate" => values.Length == 3
            ? Affine.Translation(values[1], values[2])
                .Compose(Affine.Rotation(values[0] * Math.PI / 180.0))
                .Compose(Affine.Translation(-values[1], -values[2]))
            : Affine.Rotation(values[0] * Math.PI / 180.0),
        "skewX" => Affine.SkewX(values[0] * Math.PI / 180.0),
        "skewY" => Affine.SkewY(values[0] * Math.PI / 180.0),
        _ => Affine.Identity,
    };

    private static double[] ParseArgs(string text)
    {
        var list = new List<double>();
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && (text[pos] is ' ' or '\t' or '\n' or '\r' or ',')) pos++;
            if (pos >= text.Length) break;
            int start = pos;
            if (text[pos] is '+' or '-') pos++;
            bool sawDot = false, sawExp = false;
            while (pos < text.Length)
            {
                char numChar = text[pos];
                if (numChar is >= '0' and <= '9') { pos++; continue; }
                if (numChar == '.' && !sawDot && !sawExp) { sawDot = true; pos++; continue; }
                if ((numChar is 'e' or 'E') && !sawExp) { sawExp = true; pos++; if (pos < text.Length && text[pos] is '+' or '-') pos++; continue; }
                break;
            }
            if (pos > start &&
                double.TryParse(text.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                list.Add(value);
            else
                pos++;
        }
        return list.ToArray();
    }
}