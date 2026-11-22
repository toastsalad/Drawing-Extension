namespace PlotterHelper.Geometry;

/// <summary>A point with double coordinates.</summary>
public struct PointD
{
    public double x;
    public double y;

    public PointD(double x, double y) { this.x = x; this.y = y; }
}

/// <summary>A point with integer (fixed-point) coordinates.</summary>
public struct Point64
{
    public long X;
    public long Y;

    public Point64(long x, long y) { X = x; Y = y; }
}

/// <summary>A closed or open ring of double-precision points.</summary>
public class PathD : List<PointD>
{
    public PathD() { }
    public PathD(int capacity) : base(capacity) { }
}

/// <summary>A closed or open ring of integer points.</summary>
public class Path64 : List<Point64>
{
    public Path64() { }
    public Path64(int capacity) : base(capacity) { }
}

/// <summary>A set of rings with double-precision points.</summary>
public class PathsD : List<PathD>
{
    public PathsD() { }
    public PathsD(int capacity) : base(capacity) { }
}

/// <summary>A set of rings with integer points.</summary>
/// <remarks>
/// These four types exist so the rest of the codebase reads the same as it did when the polygon work
/// was done by a third-party library. They are plain lists of points and carry no behaviour: every
/// operation lives in <see cref="EvenOddRegion"/>, <see cref="RegionOffset"/> and
/// <see cref="PlotterHelper.Hatch.HatchClipper"/>.
/// </remarks>
public class Paths64 : List<Path64>
{
    public Paths64() { }
    public Paths64(int capacity) : base(capacity) { }
}

/// <summary>Small geometry helpers shared by the region and offset code.</summary>
internal static class Polygons
{
    /// <summary>Twice the signed area; positive when the ring runs counter-clockwise.</summary>
    internal static double SignedArea2(IReadOnlyList<Vector> ring)
    {
        double sum = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
        return sum;
    }

    internal static bool IsCounterClockwise(IReadOnlyList<Vector> ring) => SignedArea2(ring) > 0;

    /// <summary>
    /// Douglas-Peucker simplification of a ring, keeping its shape to within <paramref name="tolerance"/>.
    /// </summary>
    /// <remarks>
    /// Used before eroding: a flattened drawing carries vertices a hundredth of a millimetre apart, and an
    /// offset at that density crosses itself on a microscopic scale, after which resolving the self-crossings
    /// cuts the ring into debris. Detail far below the offset distance cannot survive the erosion anyway.
    /// Never returns fewer than three points, so a ring cannot be simplified out of existence.
    /// </remarks>
    internal static List<Vector> Simplify(IReadOnlyList<Vector> ring, double tolerance)
    {
        int count = ring.Count;
        if (count < 4 || tolerance <= 0) return ring.ToList();

        var keep = new bool[count];
        keep[0] = true;
        keep[count - 1] = true;

        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, count - 1));
        double toleranceSquared = tolerance * tolerance;

        while (pending.Count > 0)
        {
            var (first, last) = pending.Pop();
            Vector a = ring[first], b = ring[last];
            Vector span = b - a;
            double lengthSquared = span.X * span.X + span.Y * span.Y;

            int worstIndex = -1;
            double worst = toleranceSquared;
            for (int i = first + 1; i < last; i++)
            {
                Vector p = ring[i];
                double dx, dy;
                if (lengthSquared <= 0)
                {
                    dx = p.X - a.X;
                    dy = p.Y - a.Y;
                }
                else
                {
                    double t = Math.Clamp(((p.X - a.X) * span.X + (p.Y - a.Y) * span.Y) / lengthSquared, 0, 1);
                    dx = p.X - (a.X + t * span.X);
                    dy = p.Y - (a.Y + t * span.Y);
                }
                double distance = dx * dx + dy * dy;
                if (distance > worst) { worst = distance; worstIndex = i; }
            }

            if (worstIndex < 0) continue;
            keep[worstIndex] = true;
            pending.Push((first, worstIndex));
            pending.Push((worstIndex, last));
        }

        var result = new List<Vector>(count);
        for (int i = 0; i < count; i++) if (keep[i]) result.Add(ring[i]);
        return result.Count >= 3 ? result : ring.ToList();
    }
}
