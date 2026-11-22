using System.Runtime.CompilerServices;

namespace PlotterHelper.Geometry;

/// <summary>
/// A polygon region held as a set of closed rings, filled by the even-odd rule.
/// </summary>
/// <remarks>
/// This is the whole region representation. A ring set <em>is</em> the region, so there is no
/// normalisation or union step: the previous implementation ran a general boolean sweep over the
/// rings purely to turn them into a canonical form that a library would accept, which cost about
/// 60% of the hatch time for no gain. Even-odd is well defined for rings that cross themselves or
/// each other, so that work is simply not done.
///
/// Operations:
/// <list type="bullet">
/// <item><see cref="Contains"/> -- even-odd ray cast, accelerated by a uniform grid when indexed.</item>
/// <item><see cref="ClipOpen"/> -- the parts of an open polyline that lie inside.</item>
/// </list>
///
/// Coordinates are whatever the caller uses. The hatch code works in the same integer units as its
/// clip polygons, so no scaling or conversion happens between the region and its callers.
/// </remarks>
public sealed class EvenOddRegion
{
    private readonly Vector[][] _rings;
    private readonly double[] _ax, _ay, _bx, _by;   // edge endpoints, flat for locality
    private readonly int _edgeCount;

    // Uniform grid over the edges, in compressed-row form: `_cellStart` gives the slice of
    // `_cellEdges` belonging to a cell. A List<int>[] per cell was costing more in indirection than
    // the edge tests themselves, and a point test is the hottest thing in the hatch: once per
    // connector interior point, once per inset probe, once per clipped interval.
    private int[]? _cellStart;
    private int[]? _cellEdges;
    private int _cellsX, _cellsY;
    private double _cellW, _cellH, _originX, _originY;

    /// <summary>
    /// Scratch for one caller's point tests: a stamp per edge so an edge is not tested twice for the
    /// same query, and a generation counter that invalidates the stamps without clearing them.
    /// </summary>
    /// <remarks>
    /// This used to be a field on the region, which made the point test single-threaded. Anything that
    /// wants to test points from several threads takes its own <see cref="Query"/>;
    /// <see cref="Contains(Vector)"/> keeps using one owned by the region for the callers that do not.
    /// </remarks>
    public sealed class Query
    {
        private readonly EvenOddRegion _region;
        internal readonly int[] Stamp;
        internal int Generation;

        internal Query(EvenOddRegion region)
        {
            _region = region;
            Stamp = new int[region._edgeCount];
        }

        /// <summary>True when a ray cast from the point crosses an odd number of edges.</summary>
        public bool Contains(Vector p) => _region.Contains(this, p);
    }

    private readonly Query _shared;

    public EvenOddRegion(IEnumerable<IReadOnlyList<Vector>> rings, bool indexed = false)
    {
        var list = new List<Vector[]>();
        foreach (var ring in rings)
        {
            if (ring.Count < 3) continue;
            var copy = new Vector[ring.Count];
            for (int i = 0; i < ring.Count; i++) copy[i] = ring[i];
            list.Add(copy);
        }
        _rings = list.ToArray();

        int edges = 0;
        foreach (var ring in _rings) edges += ring.Length;
        _edgeCount = edges;
        _ax = new double[edges]; _ay = new double[edges];
        _bx = new double[edges]; _by = new double[edges];

        int e = 0;
        var bounds = new Bounds();
        foreach (var ring in _rings)
        {
            for (int i = 0; i < ring.Length; i++)
            {
                Vector a = ring[i], b = ring[(i + 1) % ring.Length];
                _ax[e] = a.X; _ay[e] = a.Y;
                _bx[e] = b.X; _by[e] = b.Y;
                bounds.Add(a);
                e++;
            }
        }

        Bounds = bounds;
        _shared = new Query(this);
        if (indexed && edges > 0) BuildIndex();
    }

    /// <summary>Scratch for point tests, safe to hold for the duration of one thread's work.</summary>
    public Query NewQuery() => new(this);

    public IReadOnlyList<Vector[]> Rings => _rings;

    public Bounds Bounds { get; }

    public bool IsEmpty => _rings.Length == 0;

    /// <summary>True when a ray cast from the point crosses an odd number of edges.</summary>
    public bool Contains(Vector p) => Contains(_shared, p);

    /// <summary>As <see cref="Contains(Vector)"/>, using the caller's own scratch.</summary>
    public bool Contains(Query query, Vector p)
    {
        if (_edgeCount == 0) return false;
        if (!Bounds.Contains(p)) return false;

        bool inside = false;
        if (_cellEdges == null)
        {
            for (int edge = 0; edge < _edgeCount; edge++)
                if (Crosses(edge, p)) inside = !inside;
            return inside;
        }

        int row = RowOf(p.Y);
        int rowBase = row * _cellsX;
        int last = rowBase + ColumnOf(p.X);
        int generation = ++query.Generation;
        int[] start = _cellStart!, edges = _cellEdges, stamp = query.Stamp;
        for (int cell = rowBase; cell <= last; cell++)
        {
            int end = start[cell + 1];
            for (int k = start[cell]; k < end; k++)
            {
                int edge = edges[k];
                if (stamp[edge] == generation) continue;
                stamp[edge] = generation;
                if (Crosses(edge, p)) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// True when the point is inside, or within <paramref name="tolerance"/> of the boundary.
    /// </summary>
    /// <remarks>
    /// A point on the outline itself is not reliably "inside": the ray-cast test has to go one way or the other,
    /// and rounding decides it. An eroded ring legally touches the outline wherever the shape is barely wider than
    /// the offset, so a caller that wants "is this entire loop inside the region" needs to allow that touching
    /// rather than lose the loop. Four axis probes and four diagonal ones are enough for that judgement.
    /// </remarks>
    public bool ContainsWithin(Vector p, double tolerance)
    {
        if (Contains(p)) return true;
        if (!(tolerance > 0)) return false;

        const double diagonal = 0.70710678118654752;
        return Contains(new Vector(p.X + tolerance, p.Y))
            || Contains(new Vector(p.X - tolerance, p.Y))
            || Contains(new Vector(p.X, p.Y + tolerance))
            || Contains(new Vector(p.X, p.Y - tolerance))
            || Contains(new Vector(p.X + tolerance * diagonal, p.Y + tolerance * diagonal))
            || Contains(new Vector(p.X - tolerance * diagonal, p.Y + tolerance * diagonal))
            || Contains(new Vector(p.X + tolerance * diagonal, p.Y - tolerance * diagonal))
            || Contains(new Vector(p.X - tolerance * diagonal, p.Y - tolerance * diagonal));
    }

    /// <summary>
    /// The parts of an open polyline that lie inside the region, in order, with the cut points
    /// computed exactly at the boundary crossings.
    /// </summary>
    /// <remarks>
    /// A segment is split at every boundary crossing, and each piece is classified by testing its
    /// midpoint. Consecutive inside pieces are joined, so a polyline that merely touches the
    /// boundary stays one run. Tried carrying the state along the polyline and flipping it at each
    /// crossing instead; it is much faster but it changed the runs, so it is not trusted yet.
    /// </remarks>
    public List<List<Vector>> ClipOpen(IReadOnlyList<Vector> polyline) => ClipOpen(_shared, polyline);

    /// <summary>As <see cref="ClipOpen(IReadOnlyList{Vector})"/>, using the caller's own scratch.</summary>
    public List<List<Vector>> ClipOpen(Query query, IReadOnlyList<Vector> polyline)
    {
        var runs = new List<List<Vector>>();
        if (_edgeCount == 0 || polyline.Count < 2) return runs;
        if (_cellEdges == null) BuildIndex();

        List<Vector>? run = null;
        var cuts = new List<double>(8);

        for (int i = 0; i + 1 < polyline.Count; i++)
        {
            Vector p = polyline[i], q = polyline[i + 1];
            double dx = q.X - p.X, dy = q.Y - p.Y;
            if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) continue;

            cuts.Clear();
            cuts.Add(0);
            cuts.Add(1);
            CollectCuts(query, p, q, dx, dy, cuts);
            cuts.Sort();

            for (int c = 0; c + 1 < cuts.Count; c++)
            {
                double t0 = cuts[c], t1 = cuts[c + 1];
                if (t1 - t0 < 1e-9) continue;

                double mid = (t0 + t1) * 0.5;
                if (Contains(query, new Vector(p.X + dx * mid, p.Y + dy * mid)))
                {
                    var start = new Vector(p.X + dx * t0, p.Y + dy * t0);
                    var end = new Vector(p.X + dx * t1, p.Y + dy * t1);
                    if (run == null)
                    {
                        run = new List<Vector>(4) { start };
                    }
                    else if (!Same(run[^1], start))
                    {
                        // Not continuous with the previous run: flush and start again.
                        runs.Add(run);
                        run = new List<Vector>(4) { start };
                    }
                    run.Add(end);
                }
                else if (run != null)
                {
                    runs.Add(run);
                    run = null;
                }
            }
        }

        if (run != null) runs.Add(run);
        return runs;
    }

    /// <summary>Whether the segment crosses the horizontal line through <paramref name="p"/> to its left.</summary>
    /// <remarks>
    /// The half-open test on y counts a vertex exactly once, which is what keeps a line that grazes
    /// a corner from flipping the answer twice.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Crosses(int edge, Vector p)
    {
        double ay = _ay[edge], by = _by[edge];
        if ((ay > p.Y) == (by > p.Y)) return false;
        double x = _ax[edge] + (p.Y - ay) * (_bx[edge] - _ax[edge]) / (by - ay);
        return x <= p.X;
    }

    private void CollectCuts(Query query, Vector p, Vector q, double dx, double dy, List<double> cuts)
    {
        int generation = ++query.Generation;

        // Walk the cells the segment actually passes through, not the rectangle its bounding box
        // covers. A hatch run can cross an element diagonally, and its box is then the whole grid:
        // visiting that rectangle costs one pass over every edge in the region, per segment.
        double length = Math.Sqrt(dx * dx + dy * dy);
        double step = Math.Min(_cellW, _cellH) * 0.5;
        int samples = Math.Max(1, (int)Math.Ceiling(length / step));
        int previousCell = -1;

        for (int sample = 0; sample <= samples; sample++)
        {
            double t = (double)sample / samples;
            int cell = RowOf(p.Y + dy * t) * _cellsX + ColumnOf(p.X + dx * t);
            if (cell == previousCell) continue;
            previousCell = cell;

            int cellEnd = _cellStart![cell + 1];
            for (int k = _cellStart[cell]; k < cellEnd; k++)
            {
                int edge = _cellEdges![k];
                if (query.Stamp[edge] == generation) continue;
                query.Stamp[edge] = generation;

                double ex = _bx[edge] - _ax[edge], ey = _by[edge] - _ay[edge];
                double denominator = dx * ey - dy * ex;
                if (Math.Abs(denominator) < 1e-12) continue;   // parallel, or collinear overlap

                double t1 = ((_ax[edge] - p.X) * ey - (_ay[edge] - p.Y) * ex) / denominator;
                if (t1 <= 1e-9 || t1 >= 1 - 1e-9) continue;
                double u = ((_ax[edge] - p.X) * dy - (_ay[edge] - p.Y) * dx) / denominator;
                if (u < -1e-9 || u > 1 + 1e-9) continue;
                cuts.Add(t1);
            }
        }
    }

    private static bool Same(Vector a, Vector b) =>
        Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    // ---- index --------------------------------------------------------------------

    private void BuildIndex()
    {
        double width = Bounds.MaxX - Bounds.MinX;
        double height = Bounds.MaxY - Bounds.MinY;

        int side = 1;
        if (width > 0 && height > 0 && _edgeCount > 0)
            side = Math.Clamp((int)Math.Sqrt(_edgeCount / 2.0), 1, 128);
        _cellsX = _cellsY = side;
        _cellW = width > 0 ? width / side * (1 + 1e-9) : 1;
        _cellH = height > 0 ? height / side * (1 + 1e-9) : 1;
        _originX = Bounds.MinX;
        _originY = Bounds.MinY;

        int cells = side * side;
        _cellStart = new int[cells + 1];

        // Count the entries per cell, prefix-sum them, then fill. Two passes over the edges, no
        // per-cell list, and the fill order is already the order a query walks it.
        for (int edge = 0; edge < _edgeCount; edge++) CountCells(edge, _cellStart, 1);
        for (int cell = 1; cell <= cells; cell++) _cellStart[cell] += _cellStart[cell - 1];

        _cellEdges = new int[_cellStart[cells]];
        var cursor = new int[cells];
        Array.Copy(_cellStart, cursor, cells);
        for (int edge = 0; edge < _edgeCount; edge++) CountCells(edge, cursor, -1);

        void CountCells(int edge, int[] targets, int delta)
        {
            int fromX = ColumnOf(Math.Min(_ax[edge], _bx[edge]));
            int toX = ColumnOf(Math.Max(_ax[edge], _bx[edge]));
            int fromY = RowOf(Math.Min(_ay[edge], _by[edge]));
            int toY = RowOf(Math.Max(_ay[edge], _by[edge]));
            for (int row = fromY; row <= toY; row++)
            {
                for (int column = fromX; column <= toX; column++)
                {
                    int cell = row * _cellsX + column;
                    if (delta > 0) targets[cell + 1]++;
                    else _cellEdges![targets[cell]++] = edge;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ColumnOf(double x) =>
        Math.Clamp((int)((x - _originX) / _cellW), 0, _cellsX - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RowOf(double y) =>
        Math.Clamp((int)((y - _originY) / _cellH), 0, _cellsY - 1);
}

/// <summary>An axis-aligned bounding box that starts empty and grows.</summary>
public struct Bounds
{
    public double MinX, MinY, MaxX, MaxY;

    public Bounds()
    {
        MinX = MinY = double.MaxValue;
        MaxX = MaxY = double.MinValue;
    }

    public bool IsValid => MinX <= MaxX && MinY <= MaxY;

    public void Add(Vector p)
    {
        if (p.X < MinX) MinX = p.X;
        if (p.Y < MinY) MinY = p.Y;
        if (p.X > MaxX) MaxX = p.X;
        if (p.Y > MaxY) MaxY = p.Y;
    }

    public readonly bool Contains(Vector p) =>
        p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
}
