using PlotterHelper.Svg;

namespace PlotterHelper.Hatch;

/// <summary>
/// Groups SVG paths by stroke color, preserving first-appearance order. Used to
/// drive multi-color pen plotting: draw every path of one color, lift the pen,
/// (optionally pause so the operator can swap pens), then draw the next color.
/// </summary>
public static class ColorSort
{
    /// <summary>The key used for paths with no usable stroke color.</summary>
    public const string DefaultKey = "(default)";

    /// <summary>Normalize a raw SVG stroke value into a group key.</summary>
    public static string NormalizeColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DefaultKey;
        var trimmed = text.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return DefaultKey;
        if (trimmed.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return DefaultKey;
        return trimmed;
    }

    /// <summary>
    /// Group paths by stroke color, in first-appearance order. Each returned
    /// list is the paths of one color, in original document order within the group.
    /// </summary>
    public static List<List<SvgPath>> GroupByColor(IEnumerable<SvgPath> paths)
    {
        var order = new List<string>();
        var map = new Dictionary<string, List<SvgPath>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var key = NormalizeColor(path.Stroke);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<SvgPath>();
                map[key] = list;
                order.Add(key);
            }
            list.Add(path);
        }
        return order.Select(colorKey => map[colorKey]).ToList();
    }
}