using PlotterHelper.Geometry;
using PlotterHelper.Trace;

namespace PlotterHelper.Hatch;

/// <summary>
/// A "how dark is it here" scalar over the drawing, from 0 (leave the paper bare) to 1 (fill it).
/// </summary>
/// <remarks>
/// The tone-driven fills need one of these, and the two useful sources are quite different: a shape has
/// no image behind it, so its only tone is how close a point is to its own outline, whereas an image
/// brings its own luminance. Abstracting the question rather than the answer is what lets one shading
/// implementation serve both — <see cref="FillStyle.Engrave"/> reads the shape's boundary tone, and
/// <see cref="FillStyle.Shade"/> reads the image's when one is supplied.
///
/// The image is mapped onto the element's bounding box, because that is the only frame the engine knows:
/// the fill is told a box and a set of polygons, not where the element sat on the page.
/// </remarks>
internal interface IToneField
{
    /// <summary>Darkness at a point, 0 to 1.</summary>
    double Sample(Vector p);
}

/// <summary>Everywhere the same tone; used when a fill does not care.</summary>
internal sealed class FlatToneField : IToneField
{
    private readonly double _tone;
    internal FlatToneField(double tone) => _tone = tone;
    public double Sample(Vector p) => _tone;
}

/// <summary>
/// Tone from the shape itself: 1 on the boundary, falling to 0 at <c>toneDepth</c> inside.
/// </summary>
/// <remarks>
/// Distance to the nearest boundary sample over a coarse grid, normalised by a fixed depth so the rim has
/// the same visual width whether the shape is small or a whole page; a shape thinner than the rim uses its
/// own depth, so the ramp still spans the available tone instead of all being dark.
///
/// Sampling is a bucket lookup, not a pass over every edge per sample: a detailed drawing has tens of
/// thousands of them.
/// </remarks>
internal sealed class BoundaryToneField : IToneField
{
    private const int FieldCells = 96;

    private readonly double[] _tone;
    private readonly int _columns, _rows;
    private readonly double _originX, _originY, _cellW, _cellH;

    private BoundaryToneField(double[] tone, int columns, int rows,
        double originX, double originY, double cellW, double cellH)
    {
        _tone = tone;
        _columns = columns;
        _rows = rows;
        _originX = originX;
        _originY = originY;
        _cellW = cellW;
        _cellH = cellH;
    }

    public double Sample(Vector p) => Bilinear(p.X, p.Y);

    private double Bilinear(double x, double y)
    {
        double fx = (x - _originX) / _cellW;
        double fy = (y - _originY) / _cellH;
        int cx = Math.Clamp((int)fx, 0, _columns - 1);
        int cy = Math.Clamp((int)fy, 0, _rows - 1);
        int cx1 = Math.Min(cx + 1, _columns - 1);
        int cy1 = Math.Min(cy + 1, _rows - 1);
        double tx = Math.Clamp(fx - cx, 0, 1);
        double ty = Math.Clamp(fy - cy, 0, 1);

        // Bilinear: a straight hatch line crosses several cells and a nearest-cell read makes the layer
        // edges visibly stair-step.
        double c00 = _tone[cy * _columns + cx];
        double c10 = _tone[cy * _columns + cx1];
        double c01 = _tone[cy1 * _columns + cx];
        double c11 = _tone[cy1 * _columns + cx1];
        return (c00 * (1 - tx) + c10 * tx) * (1 - ty) + (c01 * (1 - tx) + c11 * tx) * ty;
    }

    /// <summary>Build the field, or null when the shape has nothing to measure (no depth to reach).</summary>
    internal static BoundaryToneField? Build(List<List<Vector>> polygons, HatchBBox bbox, double toneDepth)
    {
        double width = bbox.Xmax - bbox.Xmin, height = bbox.Ymax - bbox.Ymin;
        if (width <= 0 || height <= 0) return null;
        double step = Math.Max(width, height) / FieldCells;

        // Boundary samples spread along the edges, not just at the vertices: a rectangle has four
        // vertices, so a vertex-only sample set leaves every interior cell with no boundary point in reach
        // and the whole shape reads as deep interior. Half a cell is close enough that the distance is
        // accurate to well under the tonal resolution.
        var samples = new List<Vector>(2048);
        double sampleStep = Math.Max(step * 0.5, 1e-6);
        foreach (var ring in polygons)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Vector a = ring[i], b = ring[(i + 1) % ring.Count];
                double length = (b - a).Length;
                int parts = Math.Max(1, (int)Math.Ceiling(length / sampleStep));
                for (int p = 0; p < parts; p++) samples.Add(a + (b - a) * ((double)p / parts));
            }
        }
        if (samples.Count == 0) return null;

        int columns = Math.Max(2, (int)Math.Ceiling(width / step));
        int rows = Math.Max(2, (int)Math.Ceiling(height / step));
        double cellW = width / columns, cellH = height / rows;

        // Seed the cells the outline passes through with zero. Samples are laid along the edges at half a
        // cell, so every cell the boundary crosses collects one -- including on a long straight edge, where
        // seeding from the vertices alone would find nothing.
        var distance = new double[columns * rows];
        Array.Fill(distance, double.PositiveInfinity);
        foreach (var sample in samples)
        {
            int c = Math.Clamp((int)((sample.X - bbox.Xmin) / cellW), 0, columns - 1);
            int r = Math.Clamp((int)((sample.Y - bbox.Ymin) / cellH), 0, rows - 1);
            distance[r * columns + c] = 0;
        }

        // Two sweeps of a chamfer distance transform.
        //
        // This used to search the boundary samples in the 3x3 block of buckets around each cell, which could
        // only see one cell away: every distance was capped at about a cell, so the gradient spanned a
        // couple of units no matter what --tone-depth asked the tone to reach. Beyond about one spacing the
        // option did nothing at all. A sweep sees the whole field for one pass over the grid, and faster.
        double straight = Math.Min(cellW, cellH);
        double diagonal = Math.Sqrt(cellW * cellW + cellH * cellH);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                int i = r * columns + c;
                double d = distance[i];
                if (c > 0) d = Math.Min(d, distance[i - 1] + straight);
                if (r > 0)
                {
                    d = Math.Min(d, distance[i - columns] + straight);
                    if (c > 0) d = Math.Min(d, distance[i - columns - 1] + diagonal);
                    if (c < columns - 1) d = Math.Min(d, distance[i - columns + 1] + diagonal);
                }
                distance[i] = d;
            }
        }
        for (int r = rows - 1; r >= 0; r--)
        {
            for (int c = columns - 1; c >= 0; c--)
            {
                int i = r * columns + c;
                double d = distance[i];
                if (c < columns - 1) d = Math.Min(d, distance[i + 1] + straight);
                if (r < rows - 1)
                {
                    d = Math.Min(d, distance[i + columns] + straight);
                    if (c < columns - 1) d = Math.Min(d, distance[i + columns + 1] + diagonal);
                    if (c > 0) d = Math.Min(d, distance[i + columns - 1] + diagonal);
                }
                distance[i] = d;
            }
        }

        // The gradient spans the tone depth, or the shape's own half-width when that is smaller, so a narrow
        // shape still shades from edge to middle instead of coming out flat. Only cells inside the shape
        // count towards that half-width: the bounding box of a diagonal shape is mostly paper, and measuring
        // the distance out there would always report a depth the shape does not have.
        var region = new EvenOddRegion(polygons, indexed: true);
        var query = region.NewQuery();
        var inside = new bool[distance.Length];
        double furthest = 0;
        for (int r = 0; r < rows; r++)
        {
            double y = bbox.Ymin + (r + 0.5) * cellH;
            for (int c = 0; c < columns; c++)
            {
                int i = r * columns + c;
                if (double.IsPositiveInfinity(distance[i])) continue;
                if (!query.Contains(new Vector(bbox.Xmin + (c + 0.5) * cellW, y))) continue;
                inside[i] = true;
                if (distance[i] > furthest) furthest = distance[i];
            }
        }
        if (furthest <= 0) return null;

        double scale = Math.Min(furthest, toneDepth);
        if (scale <= 0) return null;

        var tone = new double[distance.Length];
        for (int i = 0; i < distance.Length; i++)
            tone[i] = inside[i] ? Math.Clamp(1 - distance[i] / scale, 0, 1) : 0;

        return new BoundaryToneField(tone, columns, rows, bbox.Xmin, bbox.Ymin, cellW, cellH);
    }
}

/// <summary>Tone from an image, mapped onto the element's bounding box.</summary>
internal sealed class ImageToneField : IToneField
{
    private readonly byte[] _data;
    private readonly int _width, _height;
    private readonly double _originX, _originY, _scaleX, _scaleY;
    private readonly bool _invert;

    internal ImageToneField(GrayImage image, HatchBBox bbox, bool invert)
    {
        _data = image.Data;
        _width = image.Width;
        _height = image.Height;
        _invert = invert;
        _originX = bbox.Xmin;
        _originY = bbox.Ymin;
        double width = Math.Max(1e-9, bbox.Xmax - bbox.Xmin);
        double height = Math.Max(1e-9, bbox.Ymax - bbox.Ymin);
        _scaleX = _width / width;
        _scaleY = _height / height;
    }

    public double Sample(Vector p)
    {
        // Nearest sample is enough here: the fill gates in half-spacing steps, which is coarser than the
        // pixel grid for any reasonable image, so interpolating would add cost for no visible gain.
        int x = (int)((p.X - _originX) * _scaleX);
        int y = (int)((p.Y - _originY) * _scaleY);
        if (x < 0 || y < 0 || x >= _width || y >= _height) return 0;

        // The image's rows run top-down and the drawing's run bottom-up once the engine has flipped the
        // page, so the row is mirrored. A dark pixel means ink, hence 1 - value.
        int row = _height - 1 - y;
        double value = _data[row * _width + x] / 255.0;
        return _invert ? value : 1 - value;
    }
}
