using PlotterHelper.Cli;
using PlotterHelper.Geometry;

namespace PlotterHelper.Cli.Features;

/// <summary>
/// Uniform-grid spatial index over a group's solid (closed-outline, even-odd) region,
/// used by <see cref="StrokeEmitter.EmitOrderedChains"/> to gate pen-down connectors for
/// <c>--connect-outlines</c>. <see cref="SegmentInside"/> is O(1)-ish per connector;
/// the older <see cref="SolidGeometry.SegmentWithinSolid"/> ran a full Clipper intersection
/// per connector against the whole outline (~10 ms each, seconds + ~1 GB on dense
/// drawings). <see cref="SolidGeometry.SegmentWithinSolid"/> is kept as the reference and
/// cross-checked in the tests.
/// <para>
/// <see cref="SegmentInside"/> accepts iff the segment never leaves the solid: both
/// endpoints inside-or-on-boundary, no proper edge crossing, not collinear with an
/// edge, no polygon vertex in the segment interior. Conservative (a miss only costs a
/// pen lift; a false accept would draw outside the fill).
/// </para>
/// </summary>
internal sealed class SolidRegion
{
    private readonly double _ox, _oy, _cw, _ch;
    private readonly int _cols, _rows;
    private readonly List<(Vector A, Vector B)> _edges;
    private readonly List<int>[]? _grid;          // [row*cols+col] -> edge indices

    public bool IsEmpty { get; }

    /// <summary>Build from closed polygons (each a ring with first==last). Holes via even-odd.</summary>
    public SolidRegion(IList<List<Vector>> polygons)
    {
        _edges = new List<(Vector, Vector)>();
        if (polygons.Count == 0) { IsEmpty = true; _cols = _rows = 0; _grid = null; return; }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int edgeCount = 0;
        foreach (var poly in polygons)
            if (poly.Count >= 3)
            {
                edgeCount += poly.Count - 1;     // includes the closing edge (first==last)
                for (int pointIndex = 0; pointIndex < poly.Count; pointIndex++)
                {
                    var point = poly[pointIndex];
                    if (point.X < minX) minX = point.X; if (point.X > maxX) maxX = point.X;
                    if (point.Y < minY) minY = point.Y; if (point.Y > maxY) maxY = point.Y;
                }
            }
        if (edgeCount == 0 || !double.IsFinite(minX)) { IsEmpty = true; _cols = _rows = 0; _grid = null; return; }

        // ~max_extent/64 cells, clamped to a sane range.
        double target = Math.Max(1.0, Math.Max(maxX - minX, maxY - minY) / 64.0);
        _cols = Clamp((int)Math.Ceiling((maxX - minX) / target), 1, 256);
        _rows = Clamp((int)Math.Ceiling((maxY - minY) / target), 1, 256);
        _ox = minX; _oy = minY;
        _cw = (maxX - minX) / _cols; _ch = (maxY - minY) / _rows;
        if (_cw <= 0) _cw = 1; if (_ch <= 0) _ch = 1;

        _grid = new List<int>[_cols * _rows];

        // Bucket each edge into the cells its bbox overlaps (a superset of the cells it
        // crosses; long edges are subdivided to cap the per-edge cell count).
        for (int polyIndex = 0; polyIndex < polygons.Count; polyIndex++)
        {
            var poly = polygons[polyIndex];
            if (poly.Count < 3) continue;
            for (int vertex = 0; vertex < poly.Count - 1; vertex++)
                AddEdge(poly[vertex], poly[vertex + 1]);
        }

        IsEmpty = _edges.Count == 0;
        if (IsEmpty) _grid = null;
    }

    private void AddEdge(Vector a, Vector b)
    {
        int idx = _edges.Count;
        _edges.Add((a, b));
        InsertEdgeCells(idx, a, b);
    }

    private void InsertEdgeCells(int idx, Vector a, Vector b)
    {
        int col0 = Col(a.X), row0 = Row(a.Y), col1 = Col(b.X), row1 = Row(b.Y);
        int colMin = Math.Min(col0, col1), colMax = Math.Max(col0, col1);
        int rowMin = Math.Min(row0, row1), rowMax = Math.Max(row0, row1);
        int span = (colMax - colMin + 1) * (rowMax - rowMin + 1);
        if (span <= 64) { FillCells(idx, colMin, colMax, rowMin, rowMax); return; }
        // Too many cells: subdivide (the edge index is shared across both halves).
        var mid = new Vector((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        InsertEdgeCells(idx, a, mid);
        InsertEdgeCells(idx, mid, b);
    }

    private void FillCells(int idx, int colMin, int colMax, int rowMin, int rowMax)
    {
        for (int row = rowMin; row <= rowMax; row++)
        {
            if (row < 0 || row >= _rows) continue;
            int rowBase = row * _cols;
            for (int col = colMin; col <= colMax; col++)
            {
                if (col < 0 || col >= _cols) continue;
                ref var cell = ref _grid![rowBase + col];
                cell ??= new List<int>(4);
                cell.Add(idx);
            }
        }
    }

    private int Col(double x) { int col = (int)Math.Floor((x - _ox) / _cw); return col < 0 ? 0 : col >= _cols ? _cols - 1 : col; }
    private int Row(double y) { int row = (int)Math.Floor((y - _oy) / _ch); return row < 0 ? 0 : row >= _rows ? _rows - 1 : row; }

    /// <summary>True iff segment a->b lies entirely inside (or on the boundary of) the solid.</summary>
    public bool SegmentInside(Vector a, Vector b)
    {
        if (IsEmpty) return false;
        if (Vector.Distance(a, b) <= 1e-9) return true;     // touching -> fine
        if (!InsideOrOn(a) || !InsideOrOn(b)) return false;

        // Test edges in the cells the segment passes through (a superset of crossers).
        var seen = new HashSet<int>();
        foreach (var (col, row) in CellsAlongSegment(a, b))
        {
            if (row < 0 || row >= _rows || col < 0 || col >= _cols) continue;
            var cell = _grid![row * _cols + col];
            if (cell == null) continue;
            foreach (int edgeIdx in cell)
            {
                if (!seen.Add(edgeIdx)) continue;
                var (p, q) = _edges[edgeIdx];
                if (ProperlyCrosses(a, b, p, q)) return false;
                if (CollinearOverlap(a, b, p, q)) return false;     // runs along an edge
                if (VertexOnSegmentInterior(p, a, b) || VertexOnSegmentInterior(q, a, b)) return false;
            }
        }
        return true;
    }

    /// <summary>Even-odd point-in-polygon; on-boundary counts as inside.</summary>
    private bool InsideOrOn(Vector p)
    {
        int col = Col(p.X), row = Row(p.Y);

        // On-boundary: edges in p's own cell.
        if (col >= 0 && col < _cols && row >= 0 && row < _rows && _grid != null)
        {
            var cell = _grid[row * _cols + col];
            if (cell != null)
                foreach (int edgeIdx in cell)
                    if (OnSeg(p, _edges[edgeIdx].A, _edges[edgeIdx].B)) return true;
        }

        // Ray cast +X; count edges straddling p.Y with crossing x > p.X.
        int crossings = 0;
        var seen = new HashSet<int>();
        for (int colCursor = col; colCursor < _cols; colCursor++)
        {
            var cell = _grid![row * _cols + colCursor];
            if (cell == null) continue;
            foreach (int edgeIdx in cell)
            {
                if (!seen.Add(edgeIdx)) continue;
                var (a, b) = _edges[edgeIdx];
                bool aAbove = a.Y > p.Y, bAbove = b.Y > p.Y;
                if (aAbove == bAbove) continue;        // horizontal, or wholly above/below
                double crossX = a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                if (crossX > p.X) crossings++;
            }
        }
        return (crossings & 1) == 1;
    }

    private static double Orient(Vector a, Vector b, Vector c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>ab and pq cross at a single interior point of both.</summary>
    private static bool ProperlyCrosses(Vector a, Vector b, Vector p, Vector q)
    {
        double orient1 = Orient(a, b, p), orient2 = Orient(a, b, q);
        double orient3 = Orient(p, q, a), orient4 = Orient(p, q, b);
        return orient1 * orient2 < 0 && orient3 * orient4 < 0;            // both pairs strictly opposite
    }

    /// <summary>ab and pq are collinear and overlap over a non-trivial interval.</summary>
    private static bool CollinearOverlap(Vector a, Vector b, Vector p, Vector q)
    {
        if (Math.Abs(Orient(a, b, p)) > 1e-9 || Math.Abs(Orient(a, b, q)) > 1e-9) return false;
        // Project onto the dominant axis and test 1-D interval overlap.
        bool useX = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
        double segLo = useX ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y);
        double segHi = useX ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
        double edgeLo = useX ? Math.Min(p.X, q.X) : Math.Min(p.Y, q.Y);
        double edgeHi = useX ? Math.Max(p.X, q.X) : Math.Max(p.Y, q.Y);
        return segHi - segLo > 1e-9 && edgeHi - edgeLo > 1e-9 && segHi > edgeLo + 1e-9 && edgeHi > segLo + 1e-9;
    }

    /// <summary>v lies strictly between a and b on segment ab (collinear, interior).</summary>
    private static bool VertexOnSegmentInterior(Vector v, Vector a, Vector b)
    {
        if (Math.Abs(Orient(a, b, v)) > 1e-7) return false;
        bool useX = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
        double t = useX ? (v.X - a.X) / (b.X - a.X) : (v.Y - a.Y) / (b.Y - a.Y);
        const double eps = 1e-7;
        return t > eps && t < 1 - eps;
    }

    private static bool OnSeg(Vector p, Vector a, Vector b)
    {
        if (Math.Abs(Orient(a, b, p)) > 1e-7) return false;
        bool useX = Math.Abs(b.X - a.X) >= Math.Abs(b.Y - a.Y);
        double low, high, value;
        if (useX) { low = Math.Min(a.X, b.X); high = Math.Max(a.X, b.X); value = p.X; } else { low = Math.Min(a.Y, b.Y); high = Math.Max(a.Y, b.Y); value = p.Y; }
        return value >= low - 1e-7 && value <= high + 1e-7;
    }

    /// <summary>Cells the segment passes through (Amanatides-Woo supercover), each once.</summary>
    private IEnumerable<(int col, int row)> CellsAlongSegment(Vector a, Vector b)
    {
        int col0 = Col(a.X), row0 = Row(a.Y), col1 = Col(b.X), row1 = Row(b.Y);
        yield return (col0, row0);
        int colStep = Math.Sign(col1 - col0), rowStep = Math.Sign(row1 - row0);
        if (colStep == 0 && rowStep == 0) yield break;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double tDeltaX = colStep != 0 ? Math.Abs(_cw / dx) : double.PositiveInfinity;
        double tDeltaY = rowStep != 0 ? Math.Abs(_ch / dy) : double.PositiveInfinity;
        double tMaxX = colStep > 0 ? ((col0 + 1) * _cw + _ox - a.X) / dx
                    : colStep < 0 ? (col0 * _cw + _ox - a.X) / dx : double.PositiveInfinity;
        double tMaxY = rowStep > 0 ? ((row0 + 1) * _ch + _oy - a.Y) / dy
                    : rowStep < 0 ? (row0 * _ch + _oy - a.Y) / dy : double.PositiveInfinity;
        int col = col0, row = row0;
        while (col != col1 || row != row1)
        {
            if (tMaxX < tMaxY) { col += colStep; tMaxX += tDeltaX; } else if (tMaxY < tMaxX) { row += rowStep; tMaxY += tDeltaY; } else { col += colStep; row += rowStep; tMaxX += tDeltaX; tMaxY += tDeltaY; }
            yield return (col, row);
        }
    }

    private static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;
}