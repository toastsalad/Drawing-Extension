using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Concentric rings of a shape, as the iso-contours of the distance to its own boundary.
/// </summary>
/// <remarks>
/// This replaced an iterative erosion: offset the shape inward by one spacing, then offset the result, and so
/// on, drawing each level's rings. That construction kept producing two things it could not remove, because
/// removing them needs a boolean operation the offset engine does not have.
///
/// Folds. An offset that travels further than the local curvature radius crosses itself, and what survives is a
/// little loop flipped inside out -- the arrowheads that appeared at tight corners. The offset cannot tell such a
/// loop from a real boundary, because both lie at the same distance from the shape.
///
/// Holes. The machinery that resolves those self-crossings decides whether a loop bounds material by probing a
/// point inside it. For a ring around a hole that point is the hole, which is not material, so hole rings were
/// discarded whenever the inset was small.
///
/// A contour of the distance field has neither problem. It is by definition the set of points at a given distance
/// from the boundary, inside the shape, so it cannot fold and it wraps round a hole as readily as round an
/// outline. The field is computed once, exactly, and read at every level.
/// </remarks>
internal static class ContourField
{
    /// <summary>Cell size as a fraction of the hatch spacing: rings are smooth to about this.</summary>
    private const double CellsPerSpacing = 4.0;

    /// <summary>Ceiling on the grid, so a fine spacing over a large drawing cannot exhaust memory.</summary>
    private const int MaxCells = 4_000_000;

    internal static List<List<Vector[]>> Rings(List<List<Vector>> polygons, HatchBBox bbox,
        double startInset, double spacing, double minPerimeter)
    {
        var levels = new List<List<Vector[]>>();

        double width = bbox.Xmax - bbox.Xmin;
        double height = bbox.Ymax - bbox.Ymin;
        if (width <= 0 || height <= 0 || spacing <= 0) return levels;

        double cell = spacing / CellsPerSpacing;
        if (width * height / (cell * cell) > MaxCells) cell = Math.Sqrt(width * height / MaxCells);
        if (cell <= 0) return levels;

        // Padded by a few cells so a contour around the outermost edge has room to close; a grid cut off at the
        // shape's own bounding box leaves the outline running into the border, where it cannot be joined up.
        int pad = 2;
        double padX = pad * cell, padY = pad * cell;
        var grid = new HatchBBox
        {
            Xmin = bbox.Xmin - padX, Ymin = bbox.Ymin - padY,
            Xmax = bbox.Xmax + padX, Ymax = bbox.Ymax + padY,
        };

        int columns = Math.Max(4, (int)Math.Ceiling((grid.Xmax - grid.Xmin) / cell));
        int rows = Math.Max(4, (int)Math.Ceiling((grid.Ymax - grid.Ymin) / cell));
        cell = Math.Max((grid.Xmax - grid.Xmin) / columns, (grid.Ymax - grid.Ymin) / rows);

        var distance = DistanceToBoundary(polygons, grid, columns, rows, cell);
        var inside = InsideCells(polygons, grid, columns, rows, cell);

        double diagonal = Math.Sqrt(width * width + height * height);
        int maxLevels = (int)Math.Ceiling(diagonal / spacing) + 2;

        int firstLevel = 0;
        var outline = new List<Vector[]>();
        if (startInset <= 1e-9)
        {
            // No inset means the first level is the shape itself. Its rings are already exactly that, and
            // contouring at distance zero would only rediscover them a cell at a time.
            foreach (var ring in polygons)
                if (ring.Count >= 3) outline.Add(ring.ToArray());
            if (outline.Count == 0) return levels;
            levels.Add(outline);
            firstLevel = 1;
        }

        // One pass over the grid collects the contours of every level at once. Scanning the whole grid once per
        // level is the obvious way to write this and it is what it used to do; at a fine spacing a page holds a
        // few hundred levels, so that was a few hundred scans of a few hundred thousand cells to find a few
        // thousand crossings. A cell can only cross the levels whose depth falls between its lowest and highest
        // corner distance, which is usually none or one.
        var levelSegments = new List<(long A, long B, Vector PA, Vector PB)>?[maxLevels];
        Scan(distance, inside, columns, rows, grid, cell, startInset, spacing, firstLevel, maxLevels, levelSegments);

        for (int level = firstLevel; level < maxLevels; level++)
        {
            var segments = levelSegments[level];
            if (segments is null || segments.Count == 0) break;

            var rings = Chain(segments, cell);
            var kept = new List<Vector[]>(rings.Count);
            foreach (var ring in rings)
            {
                if (ring.Length < 3) continue;
                if (Perimeter(ring) < minPerimeter) continue;
                kept.Add(ring);
            }

            if (kept.Count == 0) break;
            levels.Add(kept);
        }

        return levels;
    }

    /// <summary>
    /// Exact squared distance from each cell centre to the shape's boundary, counted in cells.
    /// </summary>
    /// <remarks>
    /// Seeded on the cells the outline passes through, then propagated with the Felzenszwalb-Huttenlocher
    /// transform: one exact one-dimensional pass along the columns, one along the rows. It is exact Euclidean
    /// distance rather than a chamfer approximation, so the contours do not drift as the levels go deeper.
    ///
    /// Squared, and in cells, because that is the cheap unit all the way through: the contour needs a real
    /// distance only at the corners of the cells it crosses, which are a small minority, and taking the square
    /// root of every cell up front was six hundred thousand roots to reach a few thousand of them.
    /// </remarks>
    private static double[] DistanceToBoundary(List<List<Vector>> polygons, HatchBBox bbox,
        int columns, int rows, double cell)
    {
        int count = columns * rows;
        var squared = new double[count];
        // Large but finite: the transform below intersects parabolas, which infinities break. Cells that no
        // seeding reaches keep a distance far beyond any level, which is what they mean.
        Array.Fill(squared, Empty);

        // Seed every cell an edge passes through. Half a cell keeps a straight edge marking an unbroken line of
        // cells, which is what the transform needs to measure from.
        double step = cell * 0.5;
        foreach (var ring in polygons)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Vector a = ring[i], b = ring[(i + 1) % ring.Count];
                double length = (b - a).Length;
                int parts = Math.Max(1, (int)Math.Ceiling(length / step));
                for (int p = 0; p <= parts; p++)
                {
                    Vector point = a + (b - a) * ((double)p / parts);
                    int c = (int)Math.Floor((point.X - bbox.Xmin) / cell);
                    int r = (int)Math.Floor((point.Y - bbox.Ymin) / cell);
                    if (c < 0 || c >= columns || r < 0 || r >= rows) continue;
                    squared[r * columns + c] = 0;
                }
            }
        }

        var buffer = new double[Math.Max(columns, rows)];
        var targets = new int[Math.Max(columns, rows)];
        var bounds = new double[Math.Max(columns, rows) + 2];

        for (int c = 0; c < columns; c++)
        {
            for (int r = 0; r < rows; r++) buffer[r] = squared[r * columns + c];
            Transform1D(buffer, rows, targets, bounds);
            for (int r = 0; r < rows; r++) squared[r * columns + c] = buffer[r];
        }
        for (int r = 0; r < rows; r++)
        {
            int offset = r * columns;
            Array.Copy(squared, offset, buffer, 0, columns);
            Transform1D(buffer, columns, targets, bounds);
            Array.Copy(buffer, 0, squared, offset, columns);
        }

        for (int i = 0; i < count; i++)
            squared[i] = squared[i] >= Empty ? Empty : squared[i];

        return squared;
    }

    /// <summary>A distance no real cell has; stands in for "unreached".</summary>
    private const double Empty = 1e12;

    /// <summary>One pass of the exact squared-distance transform along a line.</summary>
    private static void Transform1D(double[] f, int n, int[] targets, double[] bounds)
    {
        int k = 0;
        targets[0] = 0;
        bounds[0] = double.NegativeInfinity;
        bounds[1] = double.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            double s;
            while (true)
            {
                int p = targets[k];
                s = ((f[q] + (double)q * q) - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
                if (s <= bounds[k] && k > 0) { k--; continue; }
                break;
            }
            k++;
            targets[k] = q;
            bounds[k] = s;
            bounds[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (bounds[k + 1] < q) k++;
            int p = targets[k];
            f[q] = (double)(q - p) * (q - p) + f[p];
        }
    }

    /// <summary>Which cell centres are inside the shape, by the even-odd rule.</summary>
    /// <remarks>
    /// A scanline: one row of crossings against every edge, sorted, then the columns walked along it. Asking the
    /// region per cell instead is one containment query per grid cell -- hundreds of thousands of them -- and it
    /// was most of the cost of a coarse level.
    /// </remarks>
    private static bool[] InsideCells(List<List<Vector>> polygons, HatchBBox bbox,
        int columns, int rows, double cell)
    {
        var inside = new bool[columns * rows];
        var crossings = new double[polygons.Sum(r => r.Count)];

        for (int r = 0; r < rows; r++)
        {
            double y = bbox.Ymin + (r + 0.5) * cell;
            int found = 0;
            foreach (var ring in polygons)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    Vector a = ring[i], b = ring[(i + 1) % ring.Count];
                    if (a.Y > y == b.Y > y) continue;
                    double t = (y - a.Y) / (b.Y - a.Y);
                    crossings[found++] = a.X + (b.X - a.X) * t;
                }
            }

            Array.Sort(crossings, 0, found);

            int offset = r * columns;
            int next = 0;
            bool odd = false;
            for (int c = 0; c < columns; c++)
            {
                double x = bbox.Xmin + (c + 0.5) * cell;
                while (next < found && crossings[next] <= x) { odd = !odd; next++; }
                inside[offset + c] = odd;
            }
        }

        return inside;
    }

    /// <summary>
    /// Collect the contour segments of every level in one pass over the grid.
    /// </summary>
    /// <remarks>
    /// For each cell the four corner distances are read once; the levels that can cross it are the ones whose
    /// depth lies between the lowest and highest of them, which is usually none or one, so the levels the hatch
    /// asks for cost the same single sweep however many there are.
    /// </remarks>
    private static void Scan(double[] squared, bool[] inside, int columns, int rows, HatchBBox grid,
        double cell, double startInset, double spacing, int firstLevel, int maxLevels,
        List<(long A, long B, Vector PA, Vector PB)>?[] levelSegments)
    {
        int stride = columns + 1;
        double cellSquared = cell * cell;

        // The levels' depths as squared distances in cells, so a cell can be matched against them without a
        // square root: the whole grid used to take two roots per cell to discover that most cells reach no level
        // at all.
        var levelSquared = new double[maxLevels];
        for (int level = 0; level < maxLevels; level++)
        {
            double depth = (startInset + level * spacing) / cell;
            levelSquared[level] = depth * depth;
        }

        for (int r = 0; r + 1 < rows; r++)
        {
            double y0 = grid.Ymin + r * cell;
            double y1 = y0 + cell;

            for (int c = 0; c + 1 < columns; c++)
            {                int i00 = r * columns + c;
                int i10 = i00 + 1;
                int i01 = i00 + columns;
                int i11 = i01 + 1;

                bool in00 = inside[i00], in10 = inside[i10], in01 = inside[i01], in11 = inside[i11];
                if (!in00 && !in10 && !in01 && !in11) continue;

                double q00 = in00 ? squared[i00] : -1;
                double q10 = in10 ? squared[i10] : -1;
                double q01 = in01 ? squared[i01] : -1;
                double q11 = in11 ? squared[i11] : -1;

                double lowest = Math.Min(Math.Min(q00, q10), Math.Min(q01, q11));
                double highest = Math.Max(Math.Max(q00, q10), Math.Max(q01, q11));
                if (highest < 0) continue;
                if (highest < levelSquared[firstLevel]) continue;
                if (lowest > levelSquared[^1]) continue;

                int from = Math.Max(firstLevel, FirstAtLeast(levelSquared, lowest));
                int to = Math.Min(maxLevels - 1, FirstAtLeast(levelSquared, highest) - 1);
                if (to < from) continue;

                double x0 = grid.Xmin + c * cell;
                double x1 = x0 + cell;

                long bottomEdge = ((long)r * stride + c) * 2;
                long rightEdge = ((long)r * stride + (c + 1)) * 2 + 1;
                long topEdge = ((long)(r + 1) * stride + c) * 2;
                long leftEdge = ((long)r * stride + c) * 2 + 1;

                double d00 = q00 < 0 ? -1 : Math.Sqrt(q00 * cellSquared);
                double d10 = q10 < 0 ? -1 : Math.Sqrt(q10 * cellSquared);
                double d01 = q01 < 0 ? -1 : Math.Sqrt(q01 * cellSquared);
                double d11 = q11 < 0 ? -1 : Math.Sqrt(q11 * cellSquared);

                for (int level = from; level <= to; level++)
                {
                    double depth = startInset + level * spacing;
                    double g00 = d00 < 0 ? -1 : d00 - depth;
                    double g10 = d10 < 0 ? -1 : d10 - depth;
                    double g01 = d01 < 0 ? -1 : d01 - depth;
                    double g11 = d11 < 0 ? -1 : d11 - depth;

                    // Corner order matters: bit0 bottom-left, bit1 bottom-right, bit2 top-right, bit3 top-left.
                    //
                    // Strictly greater, not greater-or-equal: a level can land exactly on a cell corner, and for an
                    // axis-aligned rectangle it lands on a whole row of them -- every corner of the cell then reads
                    // as "inside", the mask comes out full and the ring is never drawn. A tie belongs to the cell
                    // on the other side, which straddles the level properly.
                    int mask = (g00 > 0 ? 1 : 0) | (g10 > 0 ? 2 : 0) | (g11 > 0 ? 4 : 0) | (g01 > 0 ? 8 : 0);
                    if (mask == 0 || mask == 15) continue;

                    var segments = levelSegments[level] ??= new List<(long, long, Vector, Vector)>(256);
                    var bottom = Crossing(bottomEdge, x0, y0, x1, y0, g00, g10);
                    var right = Crossing(rightEdge, x1, y0, x1, y1, g10, g11);
                    var top = Crossing(topEdge, x1, y1, x0, y1, g11, g01);
                    var left = Crossing(leftEdge, x0, y1, x0, y0, g01, g00);

                    (long Edge, Vector Point)? first;
                    (long Edge, Vector Point)? second = null;
                    switch (mask)
                    {
                        case 1: case 14: first = left; second = bottom; break;
                        case 2: case 13: first = bottom; second = right; break;
                        case 3: case 12: first = left; second = right; break;
                        case 4: case 11: first = right; second = top; break;
                        case 6: case 9: first = bottom; second = top; break;
                        case 7: case 8: first = left; second = top; break;
                        default:
                            // Two opposite corners inked: the cell is ambiguous. The average of the four corners
                            // says which way the surface runs, which keeps the two curves apart instead of joining
                            // them into an hourglass.
                            if ((g00 + g10 + g01 + g11) * 0.25 >= 0) { first = left; second = top; }
                            else { first = left; second = bottom; }
                            break;
                    }

                    if (first is { } a1 && second is { } b1) segments.Add((a1.Edge, b1.Edge, a1.Point, b1.Point));

                    if (mask == 5 || mask == 10)
                    {
                        var tail = (g00 + g10 + g01 + g11) * 0.25 >= 0 ? (bottom, right) : (right, top);
                        if (tail.Item1 is { } a2 && tail.Item2 is { } b2)
                            segments.Add((a2.Edge, b2.Edge, a2.Point, b2.Point));
                    }
                }
            }
        }

    }

    /// <summary>First index whose value is at least <paramref name="value"/>, in a non-decreasing array.</summary>
    private static int FirstAtLeast(double[] values, double value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static (long Edge, Vector Point)? Crossing(long edge, double ax, double ay, double bx, double by,
        double ga, double gb)
    {
        // Same strict comparison as the mask: a corner exactly on the level belongs to the cell on the other side.
        if (ga > 0 == gb > 0) return null;
        double denominator = gb - ga;
        double t = Math.Abs(denominator) < 1e-12 ? 0.5 : -ga / denominator;
        t = Math.Clamp(t, 0, 1);
        return (edge, new Vector(ax + (bx - ax) * t, ay + (by - ay) * t));
    }

    /// <summary>Join segments end to end, by the grid edge they share, into closed loops.</summary>
    /// <remarks>
    /// The segments are indexed by edge in two parallel arrays sorted once, rather than a dictionary of small
    /// lists: a fine level has twenty thousand segments, and two little allocations per edge was most of the
    /// cost of walking them.
    /// </remarks>
    private static List<Vector[]> Chain(List<(long A, long B, Vector PA, Vector PB)> segments, double cell)
    {
        var loops = new List<Vector[]>();
        if (segments.Count == 0) return loops;

        int count = segments.Count;
        var edges = new long[count * 2];
        var owners = new int[count * 2];
        for (int i = 0; i < count; i++)
        {
            edges[i * 2] = segments[i].A;
            edges[i * 2 + 1] = segments[i].B;
            owners[i * 2] = i;
            owners[i * 2 + 1] = i;
        }
        Array.Sort(edges, owners);

        var used = new bool[count];
        for (int start = 0; start < count; start++)
        {
            if (used[start]) continue;

            used[start] = true;
            var loop = new List<Vector> { segments[start].PA, segments[start].PB };
            Vector tail = loop[1];
            long tailEdge = segments[start].B;
            bool closed = false;

            while (loop.Count <= count + 1)
            {
                int next = -1;
                int index = FirstAtLeast(edges, tailEdge);
                for (; index < edges.Length && edges[index] == tailEdge; index++)
                {
                    int candidate = owners[index];
                    if (candidate == start || used[candidate]) continue;
                    next = candidate;
                    break;
                }
                if (next < 0) break;

                used[next] = true;
                var segment = segments[next];
                bool forward = segment.A == tailEdge;
                tail = forward ? segment.PB : segment.PA;
                tailEdge = forward ? segment.B : segment.A;
                loop.Add(tail);

                // Back at the edge we started from: the loop is closed, and the closing edge is implied by
                // the repeat when the ring is drawn.
                if (tailEdge == segments[start].A) { closed = true; break; }
            }

            if (!closed || loop.Count < 3) continue;
            // The grid leaves a staircase on the contour; take it out at the cell scale, which is well below
            // the pen width and well above the staircase's own amplitude.
            loops.Add(Simplify(loop, cell * 0.35));
        }

        return loops;
    }

    /// <summary>First index whose value is at least <paramref name="value"/>, in a sorted array.</summary>
    private static int FirstAtLeast(long[] values, long value)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    /// <summary>Drop the staircase the grid leaves on the contour, to well below a pen width.</summary>
    private static Vector[] Simplify(List<Vector> loop, double tolerance)
    {
        var simplified = Polygons.Simplify(loop, tolerance);
        return simplified.ToArray();
    }

    private static double Perimeter(IReadOnlyList<Vector> ring)
    {
        double total = 0;
        for (int i = 0; i < ring.Count; i++) total += (ring[(i + 1) % ring.Count] - ring[i]).Length;
        return total;
    }
}
