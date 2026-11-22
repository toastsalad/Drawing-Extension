using System.Globalization;

namespace PlotterHelper.Svg;

/// <summary>
/// SVG color string → #RRGGBB normalization. Handles #rgb / #rrggbb, rgb()/rgba(),
/// and the CSS/SVG named colors. Used to key per-color / per-pen settings so that
/// "#f00", "red", and "rgb(255,0,0)" all map to "FF0000".
/// </summary>
public static class SvgColor
{
    /// <summary>Normalize an SVG color string to an uppercase 6-digit hex string
    /// without a leading '#', or null for none/transparent/currentColor/url()/invalid.</summary>
    public static string? ToHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var low = trimmed.ToLowerInvariant();
        if (low == "none" || low == "transparent" || low == "currentcolor") return null;
        if (low.StartsWith("url(")) return null;

        if (trimmed[0] == '#')
        {
            var hex = trimmed[1..];
            if (hex.Length == 3 && IsHex(hex))
                return "" + Up(hex[0]) + Up(hex[0]) + Up(hex[1]) + Up(hex[1]) + Up(hex[2]) + Up(hex[2]);
            if (hex.Length == 6 && IsHex(hex)) return hex.ToUpperInvariant();
            if (hex.Length == 8 && IsHex(hex)) return hex[..6].ToUpperInvariant(); // #rrggbbaa
            if (hex.Length == 4 && IsHex(hex)) // #rgba
                return "" + Up(hex[0]) + Up(hex[0]) + Up(hex[1]) + Up(hex[1]) + Up(hex[2]) + Up(hex[2]);
            return null;
        }

        if (low.StartsWith("rgb"))
        {
            int leftParen = trimmed.IndexOf('(');
            int rightParen = trimmed.IndexOf(')');
            if (leftParen > 0 && rightParen > leftParen)
            {
                var inner = trimmed.Substring(leftParen + 1, rightParen - leftParen - 1);
                var parts = inner.Split('/', ',');
                if (parts.Length >= 3)
                {
                    int r = ParseChannel(parts[0]), g = ParseChannel(parts[1]), b = ParseChannel(parts[2]);
                    if (r >= 0 && g >= 0 && b >= 0)
                        return string.Format(CultureInfo.InvariantCulture, "{0:X2}{1:X2}{2:X2}", r, g, b);
                }
            }
            return null;
        }

        if (Named.TryGetValue(low, out var rgb)) return rgb;
        return null;
    }

    private static char Up(char hexChar) => char.ToUpperInvariant(hexChar);
    private static bool IsHex(string text) { foreach (var hexChar in text) if (!IsHex(hexChar)) return false; return true; }
    private static bool IsHex(char hexChar) => (hexChar >= '0' && hexChar <= '9') || (hexChar >= 'a' && hexChar <= 'f') || (hexChar >= 'A' && hexChar <= 'F');

    private static int ParseChannel(string text)
    {
        text = text.Trim();
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return Clamp((int)Math.Round(pct * 255 / 100));
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return Clamp(value);
        return -1;
    }
    private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>Lowercased SVG/CSS3 named colors → uppercase RRGGBB.</summary>
    private static readonly Dictionary<string, string> Named = new()
    {
        {"aliceblue","F0F8FF"},{"antiquewhite","FAEBD7"},{"aqua","00FFFF"},{"aquamarine","7FFFD4"},{"azure","F0FFFF"},
        {"beige","F5F5DC"},{"bisque","FFE4C4"},{"black","000000"},{"blanchedalmond","FFEBCD"},{"blue","0000FF"},
        {"blueviolet","8A2BE2"},{"brown","A52A2A"},{"burlywood","DEB887"},{"cadetblue","5F9EA0"},{"chartreuse","7FFF00"},
        {"chocolate","D2691E"},{"coral","FF7F50"},{"cornflowerblue","6495ED"},{"cornsilk","FFF8DC"},{"crimson","DC143C"},
        {"cyan","00FFFF"},{"darkblue","00008B"},{"darkcyan","008B8B"},{"darkgoldenrod","B8860B"},{"darkgray","A9A9A9"},
        {"darkgreen","006400"},{"darkgrey","A9A9A9"},{"darkkhaki","BDB76B"},{"darkmagenta","8B008B"},{"darkolivegreen","556B2F"},
        {"darkorange","FF8C00"},{"darkorchid","9932CC"},{"darkred","8B0000"},{"darksalmon","E9967A"},{"darkseagreen","8FBC8F"},
        {"darkslateblue","483D8B"},{"darkslategray","2F4F4F"},{"darkslategrey","2F4F4F"},{"darkturquoise","00CED1"},{"darkviolet","9400D3"},
        {"deeppink","FF1493"},{"deepskyblue","00BFFF"},{"dimgray","696969"},{"dimgrey","696969"},{"dodgerblue","1E90FF"},
        {"firebrick","B22222"},{"floralwhite","FFFAF0"},{"forestgreen","228B22"},{"fuchsia","FF00FF"},{"gainsboro","DCDCDC"},
        {"ghostwhite","F8F8FF"},{"gold","FFD700"},{"goldenrod","DAA520"},{"gray","808080"},{"green","008000"},
        {"greenyellow","ADFF2F"},{"grey","808080"},{"honeydew","F0FFF0"},{"hotpink","FF69B4"},{"indianred","CD5C5C"},
        {"indigo","4B0082"},{"ivory","FFFFF0"},{"khaki","F0E68C"},{"lavender","E6E6FA"},{"lavenderblush","FFF0F5"},
        {"lawngreen","7CFC00"},{"lemonchiffon","FFFACD"},{"lightblue","ADD8E6"},{"lightcoral","F08080"},{"lightcyan","E0FFFF"},
        {"lightgoldenrodyellow","FAFAD2"},{"lightgray","D3D3D3"},{"lightgreen","90EE90"},{"lightgrey","D3D3D3"},{"lightpink","FFB6C1"},
        {"lightsalmon","FFA07A"},{"lightseagreen","20B2AA"},{"lightskyblue","87CEFA"},{"lightslategray","778899"},{"lightslategrey","778899"},
        {"lightsteelblue","B0C4DE"},{"lightyellow","FFFFF0"},{"lime","00FF00"},{"limegreen","32CD32"},{"linen","FAF0E6"},
        {"magenta","FF00FF"},{"maroon","800000"},{"mediumaquamarine","66CDAA"},{"mediumblue","0000CD"},{"mediumorchid","BA55D3"},
        {"mediumpurple","9370DB"},{"mediumseagreen","3CB371"},{"mediumslateblue","7B68EE"},{"mediumspringgreen","00FA9A"},{"mediumturquoise","48D1CC"},
        {"mediumvioletred","C71585"},{"midnightblue","191970"},{"mintcream","F5FFFA"},{"mistyrose","FFE4E1"},{"moccasin","FFE4B5"},
        {"navajowhite","FFDEAD"},{"navy","000080"},{"oldlace","FDF5E6"},{"olive","808000"},{"olivedrab","6B8E23"},
        {"orange","FFA500"},{"orangered","FF4500"},{"orchid","DA70D6"},{"palegoldenrod","EEE8AA"},{"palegreen","98FB98"},
        {"paleturquoise","AFEEEE"},{"palevioletred","DB7093"},{"papayawhip","FFEFD5"},{"peachpuff","FFDAB9"},{"peru","CD853F"},
        {"pink","FFC0CB"},{"plum","DDA0DD"},{"powderblue","B0E0E6"},{"purple","800080"},{"rebeccapurple","663399"},
        {"red","FF0000"},{"rosybrown","BC8F8F"},{"royalblue","4169E1"},{"saddlebrown","8B4513"},{"salmon","FA8072"},
        {"sandybrown","F4A460"},{"seagreen","2E8B57"},{"seashell","FFF5EE"},{"sienna","A0522D"},{"silver","C0C0C0"},
        {"skyblue","87CEEB"},{"slateblue","6A5ACD"},{"slategray","708090"},{"slategrey","708090"},{"snow","FFFAFA"},
        {"springgreen","00FF7F"},{"steelblue","4682B4"},{"tan","D2B48C"},{"teal","008080"},{"thistle","D8BFD8"},
        {"tomato","FF6347"},{"turquoise","40E0D0"},{"violet","EE82EE"},{"wheat","F5DEB3"},{"white","FFFFFF"},
        {"whitesmoke","F5F5F5"},{"yellow","FFFF00"},{"yellowgreen","9ACD32"},
    };
}
