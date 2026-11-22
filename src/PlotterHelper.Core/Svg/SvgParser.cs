using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PlotterHelper.Geometry;

namespace PlotterHelper.Svg;

/// <summary>A parsed SVG graphical element: its curves, transform and stroke style.</summary>
public sealed class SvgPath
{
    public List<Curve> Curves { get; } = new();
    public Affine Transform { get; set; } = Affine.Identity;
    public string? Stroke { get; set; }
    public string? StrokeWidth { get; set; }
    /// <summary>The element's fill color (used to color/group its hatch fill). Null = unspecified.</summary>
    public string? Fill { get; set; }
    public string? Id { get; set; }
}

/// <summary>A parsed SVG document.</summary>
public sealed class SvgDocument
{
    public double CanvasWidth { get; init; }
    public double CanvasHeight { get; init; }
    public Affine DocTransform { get; init; } = Affine.Identity;
    public List<SvgPath> Paths { get; } = new();
}

/// <summary>
/// Recursively parses an SVG file into geometric curves, applying all inherited
/// transforms. Handles &lt;g&gt;, &lt;path&gt; and the basic shape elements
/// (&lt;rect&gt;, &lt;line&gt;, &lt;polyline&gt;, &lt;polygon&gt;, &lt;circle&gt;,
/// &lt;ellipse&gt;) for Axidraw-hatch parity. Port of svg_to_gcode's parse_root
/// plus eggbot_hatch's shape conversion.
/// </summary>
public static class SvgParser
{
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly char[] Separator = new[] { ' ', ',' };
    private static readonly char[] SPolyDelims = new[] { ' ', ',', '\t', '\n', '\r' };

    /// <summary>Parse an SVG file from disk.</summary>
    public static SvgDocument ParseFile(string path, bool handleViewBox = false, Affine? rootTransform = null, double? canvasHeight = null, bool transformOrigin = true)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        return ParseRoot(doc.Root!, handleViewBox, rootTransform, canvasHeight, transformOrigin);
    }

    /// <summary>Read just the SVG canvas size (numeric width/height attributes, user
    /// units) without walking the tree or building curves. Used by
    /// <c>Program.LoadDocument</c> for the bed fallback dims without a probe parse.</summary>
    public static (double Width, double Height) CanvasSize(string path)
    {
        using var reader = System.Xml.XmlReader.Create(path,
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore });
        while (reader.Read() && reader.NodeType != System.Xml.XmlNodeType.Element) { }
        double width = 3200, height = 800;
        if (reader.NodeType == System.Xml.XmlNodeType.Element)
        {
            width = ParseNumeric(reader.GetAttribute("width"), 3200);
            height = ParseNumeric(reader.GetAttribute("height"), 800);
        }
        return (width, height);
    }

    /// <summary>Parse an SVG document from a string.</summary>
    public static SvgDocument ParseString(string svg, bool handleViewBox = false, Affine? rootTransform = null, double? canvasHeight = null, bool transformOrigin = true)
    {
        var doc = XDocument.Parse(svg, LoadOptions.PreserveWhitespace);
        return ParseRoot(doc.Root!, handleViewBox, rootTransform, canvasHeight, transformOrigin);
    }

    public static SvgDocument ParseRoot(XElement root, bool handleViewBox = false, Affine? rootTransform = null, double? canvasHeight = null, bool transformOrigin = true)
    {
        // svg_to_gcode treats the SVG's numeric width/height as the canvas size in
        // user units -- it does *not* convert "210mm" to pixels, and it ignores the
        // viewBox. We mirror that for drop-in cut parity. Hatch mode can opt into
        // viewBox handling. The y-flip uses `canvasHeight` (the caller passes the bed
        // height here); if null, the SVG's own numeric height is used.
        double canvasW = ParseNumeric(root.Attribute("width")?.Value, 3200);
        double svgH = ParseNumeric(root.Attribute("height")?.Value, 800);
        double canvasH = canvasHeight ?? svgH;

        Affine docTransform = Affine.Identity;
        if (handleViewBox)
        {
            var viewBox = root.Attribute("viewBox")?.Value;
            if (!string.IsNullOrWhiteSpace(viewBox))
            {
                var parts = viewBox.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double vbW) &&
                    double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double vbH) &&
                    vbW != 0 && vbH != 0)
                {
                    docTransform = Affine.Scale(canvasW / vbW, svgH / vbH);
                }
            }
        }

        var ctx = new Ctx { Root = rootTransform ?? Affine.Identity, CanvasH = canvasH, TransformOrigin = transformOrigin };
        var result = new SvgDocument
        {
            CanvasWidth = canvasW,
            CanvasHeight = canvasH,
            DocTransform = docTransform,
        };

        foreach (var child in root.Elements())
            Traverse(child, docTransform, parentVisible: true, result, ctx);

        return result;
    }

    private struct Ctx { public Affine Root; public double CanvasH; public bool TransformOrigin; }

    private static void Traverse(XElement el, Affine parentTransform, bool parentVisible, SvgDocument result, Ctx ctx)
    {
        var name = el.Name.LocalName;

        // display:none cannot be overridden by inheritance.
        if (GetStyle(el, "display") == "none") return;
        if (name is "defs" or "metadata" or "namedview" or "pattern" or "clipPath" or "mask") return;

        bool visible = parentVisible;
        string vis = GetStyle(el, "visibility") ?? el.Attribute("visibility")?.Value ?? "inherit";
        if (vis == "hidden" || vis == "collapse") visible = false;
        else if (vis == "visible") visible = true;

        Affine transform = parentTransform;
        var transformAttr = el.Attribute("transform")?.Value;
        if (!string.IsNullOrWhiteSpace(transformAttr))
            transform = parentTransform.Compose(TransformParser.Parse(transformAttr));

        switch (name)
        {
            case "g":
                foreach (var child in el.Elements()) Traverse(child, transform, visible, result, ctx);
                return;
            case "use":
                ResolveUse(el, transform, visible, result, ctx);
                return;
            case "path":
                AddPath(el, transform, result, visible, ctx);
                return;
            case "rect":
            case "line":
            case "polyline":
            case "polygon":
            case "circle":
            case "ellipse":
                AddShape(el, name, transform, result, visible, ctx);
                return;
            default:
                // Unknown container-ish elements: recurse to be forgiving.
                if (el.HasElements)
                    foreach (var child in el.Elements()) Traverse(child, transform, visible, result, ctx);
                return;
        }
    }

    private static void AddPath(XElement el, Affine transform, SvgDocument result, bool visible, Ctx ctx)
    {
        if (!visible) return;
        var pathData = el.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData)) return;
        var path = BuildPath(pathData, transform, ctx);
        path.Stroke = GetStyle(el, "stroke");
        path.StrokeWidth = GetStyle(el, "stroke-width");
        path.Fill = GetStyle(el, "fill");
        path.Id = el.Attribute("id")?.Value;
        result.Paths.Add(path);
    }

    private static void AddShape(XElement el, string name, Affine transform, SvgDocument result, bool visible, Ctx ctx)
    {
        if (!visible) return;
        string? pathData = name switch
        {
            "rect" => RectToPath(el),
            "line" => LineToPath(el),
            "polyline" => PolyToPath(el, closed: false),
            "polygon" => PolyToPath(el, closed: true),
            "circle" => EllipseToPath(el, isCircle: true),
            "ellipse" => EllipseToPath(el, isCircle: false),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(pathData)) return;
        var path = BuildPath(pathData, transform, ctx);
        path.Stroke = GetStyle(el, "stroke");
        path.StrokeWidth = GetStyle(el, "stroke-width");
        path.Fill = GetStyle(el, "fill");
        path.Id = el.Attribute("id")?.Value;
        result.Paths.Add(path);
    }

    private static SvgPath BuildPath(string pathData, Affine transform, Ctx ctx)
    {
        // Per-path transform = root ∘ element, then PathParser composes the y-flip on top:
        //   origin_flip ∘ root ∘ element  — matching svg_to_gcode's Transformation.
        Affine perPath = ctx.Root.Compose(transform);
        var parser = new PathParser(pathData, ctx.CanvasH, transformOrigin: ctx.TransformOrigin, perPath);
        parser.Parse();
        var path = new SvgPath { Transform = perPath };
        path.Curves.AddRange(parser.Curves);
        return path;
    }

    private static void ResolveUse(XElement el, Affine transform, bool visible, SvgDocument result, Ctx ctx)
    {
        var href = el.Attribute("href")?.Value ?? el.Attribute(XLink + "href")?.Value;
        if (string.IsNullOrEmpty(href) || href[0] != '#') return;
        string id = href[1..];
        var root = el.Document!.Root!;
        var refEl = FindById(root, id);
        if (refEl == null) return;
        double x = ParseDouble(el.Attribute("x")?.Value, 0);
        double y = ParseDouble(el.Attribute("y")?.Value, 0);
        Affine useTransform = transform;
        if (x != 0 || y != 0)
            useTransform = useTransform.Compose(Affine.Translation(x, y));
        Traverse(refEl, useTransform, visible, result, ctx);
    }

    private static XElement? FindById(XElement root, string id)
    {
        foreach (var node in root.Descendants())
            if (node.Attribute("id")?.Value == id) return node;
        return null;
    }

    // ---- shape -> path-data conversions (mirror eggbot_hatch.py) ----

    private static string? RectToPath(XElement el)
    {
        double x = ParseDouble(el.Attribute("x")?.Value, 0);
        double y = ParseDouble(el.Attribute("y")?.Value, 0);
        double width = ParseDouble(el.Attribute("width")?.Value, 0);
        double height = ParseDouble(el.Attribute("height")?.Value, 0);
        if (width == 0 || height == 0) return null;

        // Honored corner radii (SVG rules: clamp each to half the rect side; if only
        // one is given the other mirrors it). rx/ry of 0 -> sharp corners (legacy path).
        double rx = ParseDouble(el.Attribute("rx")?.Value, 0);
        double ry = ParseDouble(el.Attribute("ry")?.Value, 0);
        if (rx == 0 && ry == 0)
            return $"M {F(x)},{F(y)} l {F(width)},0 l 0,{F(height)} l {F(-width)},0 Z";
        if (rx == 0) rx = ry;
        if (ry == 0) ry = rx;
        // Clamp per SVG: never larger than half the side, and if one exceeds its
        // half-side, the other is clamped to its own half-side (keeps corners valid).
        double maxRx = width / 2.0, maxRy = height / 2.0;
        if (rx > maxRx) rx = maxRx;
        if (ry > maxRy) ry = maxRy;
        if (rx <= 0 || ry <= 0)
            return $"M {F(x)},{F(y)} l {F(width)},0 l 0,{F(height)} l {F(-width)},0 Z";

        // Clockwise (SVG y-down) rounded rectangle: four lines + four 90-degree arcs.
        double x0 = x, y0 = y, x1 = x + width, y1 = y + height;
        return "M " + F(x0 + rx) + "," + F(y0) +
               " L " + F(x1 - rx) + "," + F(y0) +
               " A " + F(rx) + "," + F(ry) + " 0 0 1 " + F(x1) + "," + F(y0 + ry) +
               " L " + F(x1) + "," + F(y1 - ry) +
               " A " + F(rx) + "," + F(ry) + " 0 0 1 " + F(x1 - rx) + "," + F(y1) +
               " L " + F(x0 + rx) + "," + F(y1) +
               " A " + F(rx) + "," + F(ry) + " 0 0 1 " + F(x0) + "," + F(y1 - ry) +
               " L " + F(x0) + "," + F(y0 + ry) +
               " A " + F(rx) + "," + F(ry) + " 0 0 1 " + F(x0 + rx) + "," + F(y0) +
               " Z";
    }

    private static string LineToPath(XElement el)
    {
        double x1 = ParseDouble(el.Attribute("x1")?.Value, 0);
        double y1 = ParseDouble(el.Attribute("y1")?.Value, 0);
        double x2 = ParseDouble(el.Attribute("x2")?.Value, 0);
        double y2 = ParseDouble(el.Attribute("y2")?.Value, 0);
        return $"M {F(x1)},{F(y1)} L {F(x2)},{F(y2)}";
    }

    private static string? PolyToPath(XElement el, bool closed)
    {
        var pts = el.Attribute("points")?.Value;
        if (string.IsNullOrWhiteSpace(pts)) return null;
        var parts = pts.Split(SPolyDelims, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return null;
        var builder = new StringBuilder();
        for (int pair = 0; pair + 1 < parts.Length; pair += 2)
            builder.Append(pair == 0 ? "M " : " L ").Append(parts[pair]).Append(',').Append(parts[pair + 1]);
        if (closed) builder.Append(" Z");
        return builder.ToString();
    }

    private static string? EllipseToPath(XElement el, bool isCircle)
    {
        double rx = isCircle ? ParseDouble(el.Attribute("r")?.Value, 0) : ParseDouble(el.Attribute("rx")?.Value, 0);
        double ry = isCircle ? rx : ParseDouble(el.Attribute("ry")?.Value, 0);
        if (rx == 0 || ry == 0) return null;
        double cx = ParseDouble(el.Attribute("cx")?.Value, 0);
        double cy = ParseDouble(el.Attribute("cy")?.Value, 0);
        double x1 = cx - rx, x2 = cx + rx;
        return $"M {F(x1)},{F(cy)} A {F(rx)},{F(ry)} 0 1 0 {F(x2)},{F(cy)} A {F(rx)},{F(ry)} 0 1 0 {F(x1)},{F(cy)}";
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static double ParseDouble(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        // Strip any unit suffix.
        int cut = text.Length;
        for (int charIndex = 0; charIndex < text.Length; charIndex++)
            if (text[charIndex] is not ('-' or '+' or '.' or (>= '0' and <= '9') or 'e' or 'E')) { cut = charIndex; break; }
        if (double.TryParse(text.AsSpan(0, cut), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return value;
        return fallback;
    }

    /// <summary>Parse an SVG length by stripping any trailing unit suffix and returning the raw numeric value (svg_to_gcode behavior).</summary>
    public static double ParseNumeric(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        double value = ParseDouble(text, double.NaN);
        return double.IsNaN(value) ? fallback : value;
    }

    /// <summary>Read a CSS property from the style="..." attribute or as a direct attribute.</summary>
    public static string? GetStyle(XElement el, string key)
    {
        var style = el.Attribute("style")?.Value;
        if (!string.IsNullOrEmpty(style))
        {
            foreach (var decl in style.Split(';'))
            {
                int idx = decl.IndexOf(':');
                if (idx > 0 && decl.AsSpan(0, idx).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return decl[(idx + 1)..].Trim();
            }
        }
        return el.Attribute(key)?.Value;
    }
}