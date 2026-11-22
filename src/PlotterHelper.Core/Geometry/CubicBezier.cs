namespace PlotterHelper.Geometry;

/// <summary>
/// Cubic Bézier curve. Flattening uses the control-point distance flatness test
/// with recursive de Casteljau subdivision — O(segments) with no per-segment
/// sampling, replacing the Python original's repeated 9-sample max-distance loop.
/// </summary>
public sealed class CubicBezier : Curve
{
    public readonly Vector C1, C2;

    public CubicBezier(Vector start, Vector c1, Vector c2, Vector end) : base(start, end) { C1 = c1; C2 = c2; }

    public override Vector Point(double t)
    {
        double inv = 1 - t;
        double inv2 = inv * inv, inv3 = inv2 * inv;
        double t2 = t * t, t3 = t2 * t;
        return inv3 * Start + 3 * inv2 * t * C1 + 3 * inv * t2 * C2 + t3 * End;
    }

    public override void Flatten(List<Vector> output, double tolerance) =>
        BezierFlatten.Cubic(output, Start, C1, C2, End, tolerance, maxDepth: 24);
}

/// <summary>Quadratic Bézier curve.</summary>
public sealed class QuadraticBezier : Curve
{
    public readonly Vector C1;

    public QuadraticBezier(Vector start, Vector c1, Vector end) : base(start, end) { C1 = c1; }

    public override Vector Point(double t)
    {
        double inv = 1 - t;
        return inv * inv * Start + 2 * inv * t * C1 + t * t * End;
    }

    public override void Flatten(List<Vector> output, double tolerance) =>
        BezierFlatten.Quadratic(output, Start, C1, End, tolerance, maxDepth: 24);
}

/// <summary>
/// Adaptive Bézier flattening shared by cubic and quadratic curves.
/// Uses the control-point-distance flatness measure (distance of control points
/// from the chord) which is the standard fast test. An explicit stack avoids
/// deep recursion on pathological curves while keeping allocations low.
/// </summary>
internal static class BezierFlatten
{
    public static void Cubic(List<Vector> output, Vector p0, Vector p1, Vector p2, Vector p3, double tol, int maxDepth)
    {
        Span<Vector> stack = new Vector[4 * (maxDepth + 1)];
        int stackPtr = 0;
        // Push the full curve.
        stack[stackPtr++] = p0; stack[stackPtr++] = p1; stack[stackPtr++] = p2; stack[stackPtr++] = p3;
        int depth = 0;

        while (stackPtr > 0)
        {
            // Peek top curve.
            Vector a = stack[stackPtr - 4], b = stack[stackPtr - 3], c = stack[stackPtr - 2], d = stack[stackPtr - 1];

            if (depth >= maxDepth || IsFlatCubic(a, b, c, d, tol))
            {
                // Emit d (a was emitted by the parent or the caller as Start).
                output.Add(d);
                stackPtr -= 4;
                depth--;
                continue;
            }

            // Subdivide at t=0.5 via de Casteljau. Put the RIGHT half at the stack top
            // and push the LEFT half above it, so the left half is processed first
            // (LIFO) and points are emitted front-to-back.
            Vector midAb = Vector.Lerp(a, b, 0.5);
            Vector midBc = Vector.Lerp(b, c, 0.5);
            Vector midCd = Vector.Lerp(c, d, 0.5);
            Vector midAbc = Vector.Lerp(midAb, midBc, 0.5);
            Vector midBcd = Vector.Lerp(midBc, midCd, 0.5);
            Vector mid = Vector.Lerp(midAbc, midBcd, 0.5);

            stack[stackPtr - 4] = mid; stack[stackPtr - 3] = midBcd; stack[stackPtr - 2] = midCd; stack[stackPtr - 1] = d;   // right half
            stack[stackPtr++] = a; stack[stackPtr++] = midAb; stack[stackPtr++] = midAbc; stack[stackPtr++] = mid;        // left half on top
            depth++;
        }
    }

    public static void Quadratic(List<Vector> output, Vector p0, Vector p1, Vector p2, double tol, int maxDepth)
    {
        Span<Vector> stack = new Vector[3 * (maxDepth + 1)];
        int stackPtr = 0;
        stack[stackPtr++] = p0; stack[stackPtr++] = p1; stack[stackPtr++] = p2;
        int depth = 0;

        while (stackPtr > 0)
        {
            Vector a = stack[stackPtr - 3], b = stack[stackPtr - 2], c = stack[stackPtr - 1];

            if (depth >= maxDepth || IsFlatQuadratic(a, b, c, tol))
            {
                output.Add(c);
                stackPtr -= 3;
                depth--;
                continue;
            }

            Vector midAb = Vector.Lerp(a, b, 0.5);
            Vector midBc = Vector.Lerp(b, c, 0.5);
            Vector mid = Vector.Lerp(midAb, midBc, 0.5);

            stack[stackPtr - 3] = mid; stack[stackPtr - 2] = midBc; stack[stackPtr - 1] = c;   // right half
            stack[stackPtr++] = a; stack[stackPtr++] = midAb; stack[stackPtr++] = mid;      // left half on top
            depth++;
        }
    }

    private static bool IsFlatCubic(Vector p0, Vector p1, Vector p2, Vector p3, double tol)
    {
        // Distance of p1, p2 from the chord p0->p3.
        double dx = p3.X - p0.X, dy = p3.Y - p0.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-20)
        {
            // Degenerate chord: flat only if control points coincide with p0.
            return Vector.DistanceSquared(p0, p1) <= tol * tol &&
                   Vector.DistanceSquared(p0, p2) <= tol * tol;
        }
        double dist1 = Math.Abs(dy * (p1.X - p0.X) - dx * (p1.Y - p0.Y));
        double dist2 = Math.Abs(dy * (p2.X - p0.X) - dx * (p2.Y - p0.Y));
        // Compare against chord length so the test is in absolute units.
        double len = Math.Sqrt(lenSq);
        return dist1 <= tol * len && dist2 <= tol * len;
    }

    private static bool IsFlatQuadratic(Vector p0, Vector p1, Vector p2, double tol)
    {
        double dx = p2.X - p0.X, dy = p2.Y - p0.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-20)
            return Vector.DistanceSquared(p0, p1) <= tol * tol;
        double dist1 = Math.Abs(dy * (p1.X - p0.X) - dx * (p1.Y - p0.Y));
        double len = Math.Sqrt(lenSq);
        return dist1 <= tol * len;
    }
}