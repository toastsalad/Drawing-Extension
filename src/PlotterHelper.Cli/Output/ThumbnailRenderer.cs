using System.Text;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Cli.Output;

/// <summary>
/// Renders a small PNG of the drawing (outlines + hatch fill, like the preview SVG)
/// and wraps it in a PrusaSlicer-style <c>; thumbnail begin ... ; thumbnail end</c>
/// block. Klipper/Moonraker parses that block from the uploaded G-code, writes the
/// PNG to <c>.thumbs/&lt;name&gt;-&lt;W&gt;x&lt;H&gt;.png</c>, and generates a 32x32
/// miniature from it -- so Mainsail/Fluidd/KlipperScreen show the preview with no
/// separate thumbnail upload. See Moonraker's <c>metadata.py</c> <c>parse_thumbnails</c>.
/// </summary>
internal static class ThumbnailRenderer
{
    private const int MaxSize = 400;     // long edge of the rendered thumbnail (px)
    private const int Supersample = 2;   // AA factor
    // If the flattened geometry exceeds this many points, decimate each polyline
    // (keep every Nth vertex) so we ALWAYS render a thumbnail -- a coarser preview
    // is far better than none. Without this, a large/finely-hatched drawing (e.g.
    // wave fill on a big canvas: millions of sampled points) would silently skip
    // the thumbnail and Mainsail/Fluidd would show nothing.
    private const int PointBudget = 300_000;

    /// <summary>
    /// Rasterize the outlines and hatch groups to an RGBA8 PNG, or return null if
    /// there is nothing to draw (empty document) or the input is too large.
    /// </summary>
    public static byte[]? Render(SvgDocument doc, List<HatchGroup> groups, bool showOutlines, double tolerance)
    {
        // Collect polylines with their stroke color.
        var lines = new List<(List<Vector> pts, byte r, byte g, byte b)>();

        if (showOutlines)
        {
            foreach (var path in doc.Paths)
            {
                var chain = new LineSegmentChain();
                foreach (var curve in path.Curves) chain.AppendFlattened(curve, tolerance);
                if (chain.Points.Count > 1)
                    lines.Add((new List<Vector>(chain.Points), 0x88, 0x88, 0x88));
            }
        }

        foreach (var group in groups)
            foreach (var path in group.Paths)
            {
                var pts = new List<Vector> { path.Start };
                foreach (var hatchOp in path.Ops)
                    pts.Add(hatchOp is LineTo line ? line.P : ((CubicTo)hatchOp).P);
                if (pts.Count > 1)
                    lines.Add((pts, 0xdd, 0x00, 0x00));
            }

        if (lines.Count == 0) return null;

        // Decimate dense input down to the point budget (keeps every polyline's
        // first/last vertex so shapes stay recognizable). Sparse input is untouched.
        long total = 0;
        foreach (var (pts, _, _, _) in lines) total += pts.Count;
        if (total > PointBudget)
        {
            int factor = (int)Math.Ceiling((double)total / PointBudget);
            if (factor < 2) factor = 2;
            var decimated = new List<(List<Vector> pts, byte r, byte g, byte b)>(lines.Count);
            foreach (var (pts, r, g, b) in lines)
            {
                if (pts.Count <= 2) { decimated.Add((pts, r, g, b)); continue; }
                var kept = new List<Vector>((pts.Count + factor - 1) / factor + 2);
                kept.Add(pts[0]);
                for (int sample = factor; sample < pts.Count - 1; sample += factor) kept.Add(pts[sample]);
                kept.Add(pts[^1]);
                decimated.Add((kept, r, g, b));
            }
            lines = decimated;
        }

        // Bounding box of all geometry (cartesian, y-up).
        double minx = double.PositiveInfinity, miny = double.PositiveInfinity;
        double maxx = double.NegativeInfinity, maxy = double.NegativeInfinity;
        foreach (var (pts, _, _, _) in lines)
            foreach (var point in pts)
            {
                if (point.X < minx) minx = point.X;
                if (point.Y < miny) miny = point.Y;
                if (point.X > maxx) maxx = point.X;
                if (point.Y > maxy) maxy = point.Y;
            }
        if (!double.IsFinite(minx)) return null;

        double width = Math.Max(1e-6, maxx - minx);
        double height = Math.Max(1e-6, maxy - miny);

        // 3% margin so edge strokes aren't clipped.
        double marginDoc = 0.03 * Math.Max(width, height);
        minx -= marginDoc;
        width += 2 * marginDoc; height += 2 * marginDoc;

        // Target size preserving aspect ratio (long edge = MaxSize).
        double scale = MaxSize / Math.Max(width, height);
        int imgW = Math.Max(1, (int)Math.Round(width * scale));
        int imgH = Math.Max(1, (int)Math.Round(height * scale));
        int superW = imgW * Supersample;
        int superH = imgH * Supersample;

        // Supersampled RGBA buffer (transparent black). Draw outlines first, then
        // hatches on top, as binary coverage; the downsample box-filter antialiases.
        var pixels = new byte[superW * superH * 4];
        double scaleX = (superW - 1) / width;
        double scaleY = (superH - 1) / height;
        int radius = Math.Max(1, Supersample); // ~1 output px line thickness

        double MapX(double x) => (x - minx) * scaleX;
        double MapY(double y) => (maxy + marginDoc - y) * scaleY; // flip y-down

        foreach (var (pts, r, g, b) in lines)
        {
            double px = MapX(pts[0].X), py = MapY(pts[0].Y);
            Stamp(pixels, superW, superH, px, py, radius, r, g, b);
            for (int point = 1; point < pts.Count; point++)
            {
                double nx = MapX(pts[point].X), ny = MapY(pts[point].Y);
                DrawSeg(pixels, superW, superH, px, py, nx, ny, radius, r, g, b);
                px = nx; py = ny;
            }
        }

        // Box-filter downsample: alpha = coverage, color = avg of covered pixels.
        var rgba = new byte[imgW * imgH * 4];
        int block = Supersample * Supersample;
        for (int oy = 0; oy < imgH; oy++)
            for (int ox = 0; ox < imgW; ox++)
            {
                int aSum = 0, rSum = 0, gSum = 0, bSum = 0, count = 0;
                for (int dy = 0; dy < Supersample; dy++)
                    for (int dx = 0; dx < Supersample; dx++)
                    {
                        int srcOff = (((oy * Supersample + dy) * superW) + (ox * Supersample + dx)) * 4;
                        int alpha = pixels[srcOff + 3];
                        if (alpha > 0) { rSum += pixels[srcOff]; gSum += pixels[srcOff + 1]; bSum += pixels[srcOff + 2]; aSum += alpha; count++; }
                    }
                int dstOff = (oy * imgW + ox) * 4;
                rgba[dstOff + 3] = (byte)(aSum / block); // average coverage -> alpha
                if (count > 0)
                {
                    rgba[dstOff] = (byte)(rSum / count);
                    rgba[dstOff + 1] = (byte)(gSum / count);
                    rgba[dstOff + 2] = (byte)(bSum / count);
                }
            }

        return PngImage.EncodeRgba(imgW, imgH, rgba);
    }

    private static void Stamp(byte[] buf, int superW, int superH, double fx, double fy,
        int radius, byte r, byte g, byte b)
    {
        int x = (int)Math.Round(fx), y = (int)Math.Round(fy);
        int x0 = Math.Max(0, x - radius), x1 = Math.Min(superW - 1, x + radius);
        int y0 = Math.Max(0, y - radius), y1 = Math.Min(superH - 1, y + radius);
        int radiusSq = radius * radius;
        for (int pixY = y0; pixY <= y1; pixY++)
            for (int pixX = x0; pixX <= x1; pixX++)
            {
                int dx = pixX - x, dy = pixY - y;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    int srcOff = (pixY * superW + pixX) * 4;
                    buf[srcOff] = r; buf[srcOff + 1] = g; buf[srcOff + 2] = b; buf[srcOff + 3] = 255;
                }
            }
    }

    private static void DrawSeg(byte[] buf, int superW, int superH,
        double x0, double y0, double x1, double y1,
        int radius, byte r, byte g, byte b)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) { Stamp(buf, superW, superH, x0, y0, radius, r, g, b); return; }
        double step = Math.Max(0.5, radius * 0.5);
        int steps = Math.Max(1, (int)Math.Ceiling(len / step));
        for (int sample = 0; sample <= steps; sample++)
        {
            double t = (double)sample / steps;
            Stamp(buf, superW, superH, x0 + dx * t, y0 + dy * t, radius, r, g, b);
        }
    }

    /// <summary>
    /// Build the PrusaSlicer-style thumbnail comment block that Moonraker parses:
    /// <code>
    /// ; thumbnail begin &lt;W&gt; &lt;H&gt; &lt;base64 length&gt;
    /// ; &lt;base64 line 1&gt;
    /// ; ...
    /// ; thumbnail end
    /// </code>
    /// The third number is the length of the base64 text (NOT the decoded PNG size),
    /// which is what Moonraker's metadata extractor validates against.
    /// </summary>
    public static string BuildBlock(byte[] png, int chunkSize = 78)
    {
        var b64 = Convert.ToBase64String(png);
        PngDims(png, out int width, out int height);

        var builder = new StringBuilder();
        builder.Append("; thumbnail begin ").Append(width).Append(' ').Append(height).Append(' ')
          .Append(b64.Length).Append('\n');
        for (int offset = 0; offset < b64.Length; offset += chunkSize)
        {
            int take = Math.Min(chunkSize, b64.Length - offset);
            builder.Append("; ").Append(b64, offset, take).Append('\n');
        }
        builder.Append("; thumbnail end\n");
        return builder.ToString();
    }

    /// <summary>Read width/height out of an IHDR chunk (first chunk after the signature).</summary>
    private static void PngDims(byte[] png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 24) return;
        // Bytes 16..19 = width (big-endian), 20..23 = height.
        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
    }
}