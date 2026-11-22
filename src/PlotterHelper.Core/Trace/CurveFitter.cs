using PlotterHelper.Geometry;

namespace PlotterHelper.Trace;

/// <summary>
/// Turns a skeleton polyline into a handful of cubic Béziers: Douglas-Peucker to drop the
/// redundant pixel steps, then Schneider's least-squares fit (Graphics Gems) to round what is
/// left. The compiler flattens curves adaptively on the way out, so emitting a polyline per
/// skeleton pixel would multiply the G-code by an order of magnitude and leave the plotter
/// faceting along every pixel stair.
/// </summary>
public static class CurveFitter
{
    private const int MaxReparameterizations = 4;
    private const int MaxSplitDepth = 16;

    /// <summary>Douglas-Peucker simplification of a polyline.</summary>
    public static List<Vector> Simplify(IReadOnlyList<Vector> points, double epsilon)
    {
        int count = points.Count;
        var result = new List<Vector>();
        if (count == 0) return result;
        if (count < 3 || epsilon <= 0)
        {
            for (int i = 0; i < count; i++) result.Add(points[i]);
            return result;
        }

        var keep = new bool[count];
        keep[0] = keep[count - 1] = true;
        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, count - 1));
        while (pending.Count > 0)
        {
            var (first, last) = pending.Pop();
            double worst = 0;
            int split = -1;
            for (int i = first + 1; i < last; i++)
            {
                double distance = DistanceToSegment(points[i], points[first], points[last]);
                if (distance > worst) { worst = distance; split = i; }
            }
            if (split < 0 || worst <= epsilon) continue;
            keep[split] = true;
            pending.Push((first, split));
            pending.Push((split, last));
        }

        for (int i = 0; i < count; i++) if (keep[i]) result.Add(points[i]);
        return result;
    }

    /// <summary>Moving average that leaves the ends fixed, so the fit follows the drawn line
    /// instead of the pixel grid's sawtooth.</summary>
    public static List<Vector> Smooth(IReadOnlyList<Vector> points, int window)
    {
        if (points.Count < 3 || window < 1) return new List<Vector>(points);

        var result = new List<Vector>(points.Count) { points[0] };
        for (int i = 1; i < points.Count - 1; i++)
        {
            int from = Math.Max(0, i - window), to = Math.Min(points.Count - 1, i + window);
            double sumX = 0, sumY = 0;
            for (int j = from; j <= to; j++) { sumX += points[j].X; sumY += points[j].Y; }
            int count = to - from + 1;
            result.Add(new Vector(sumX / count, sumY / count));
        }
        result.Add(points[^1]);
        return result;
    }

    /// <summary>Fit a chain of cubics within <paramref name="error"/> of the polyline.</summary>
    public static List<Curve> Fit(IReadOnlyList<Vector> points, double error)
    {
        var curves = new List<Curve>();
        if (points.Count < 2) return curves;
        if (points.Count == 2)
        {
            curves.Add(new Line(points[0], points[1]));
            return curves;
        }

        var pts = new List<Vector>(points);
        Vector startTangent = Normalize(pts[1] - pts[0]);
        Vector endTangent = Normalize(pts[^2] - pts[^1]);
        FitCubic(pts, 0, pts.Count - 1, startTangent, endTangent, error, curves, 0);
        return curves;
    }

    private static void FitCubic(List<Vector> pts, int first, int last, Vector tangent1, Vector tangent2,
        double error, List<Curve> curves, int depth)
    {
        if (last - first + 1 == 2)
        {
            double reach = (pts[last] - pts[first]).Length / 3.0;
            curves.Add(new CubicBezier(pts[first], pts[first] + tangent1 * reach, pts[last] + tangent2 * reach, pts[last]));
            return;
        }

        var parameters = ChordLengthParameterize(pts, first, last);
        var bezier = GenerateBezier(pts, first, last, parameters, tangent1, tangent2);
        var (worst, split) = MaxError(pts, first, last, bezier, parameters);

        if (worst < error || depth >= MaxSplitDepth)
        {
            curves.Add(bezier);
            return;
        }

        // Close enough to converge by nudging the parameters instead of splitting; this is what
        // keeps a long smooth line as a few segments rather than dozens.
        if (worst < error * 4.0)
        {
            for (int i = 0; i < MaxReparameterizations; i++)
            {
                var improved = Reparameterize(pts, first, parameters, bezier);
                bezier = GenerateBezier(pts, first, last, improved, tangent1, tangent2);
                (worst, split) = MaxError(pts, first, last, bezier, improved);
                if (worst < error) { curves.Add(bezier); return; }
                parameters = improved;
            }
        }

        split = Math.Clamp(split, first + 1, last - 1);
        Vector centreTangent = Normalize(pts[split - 1] - pts[split + 1]);
        FitCubic(pts, first, split, tangent1, centreTangent, error, curves, depth + 1);
        FitCubic(pts, split, last, -centreTangent, tangent2, error, curves, depth + 1);
    }

    /// <summary>Least-squares placement of the two control points with the end tangents fixed.</summary>
    private static CubicBezier GenerateBezier(List<Vector> pts, int first, int last, double[] parameters, Vector tangent1, Vector tangent2)
    {
        int count = last - first + 1;
        var basis1 = new Vector[count];
        var basis2 = new Vector[count];
        double dot11 = 0, dot12 = 0, dot22 = 0, rhs1 = 0, rhs2 = 0;

        for (int i = 0; i < count; i++)
        {
            basis1[i] = tangent1 * B1(parameters[i]);
            basis2[i] = tangent2 * B2(parameters[i]);
            dot11 += Vector.Dot(basis1[i], basis1[i]);
            dot12 += Vector.Dot(basis1[i], basis2[i]);
            dot22 += Vector.Dot(basis2[i], basis2[i]);

            Vector onEndCaps = pts[first] * (B0(parameters[i]) + B1(parameters[i])) + pts[last] * (B2(parameters[i]) + B3(parameters[i]));
            Vector residual = pts[first + i] - onEndCaps;
            rhs1 += Vector.Dot(basis1[i], residual);
            rhs2 += Vector.Dot(basis2[i], residual);
        }

        double determinant = dot11 * dot22 - dot12 * dot12;
        double alphaLeft = 0, alphaRight = 0;
        // Scale-relative singularity test: dot11/dot22 grow with the square of the coordinate
        // magnitude, so an absolute epsilon would never fire on a real drawing.
        if (Math.Abs(determinant) > 1e-12 * (Math.Abs(dot11 * dot22) + 1e-12))
        {
            alphaLeft = (rhs1 * dot22 - dot12 * rhs2) / determinant;
            alphaRight = (dot11 * rhs2 - dot12 * rhs1) / determinant;
        }

        double segment = (pts[last] - pts[first]).Length;
        double minimum = 1e-6 * segment;
        // Cap the handles as well as flooring them: a near-singular solve can return an enormous
        // alpha, and a leaf at the recursion depth limit is emitted anyway, sweeping a control
        // point far outside the drawing. No useful cubic needs a handle longer than its chord.
        double maximum = 2.0 * segment;
        if (alphaLeft < minimum || alphaRight < minimum || alphaLeft > maximum || alphaRight > maximum)
        {
            // Degenerate system: fall back to the Wu/Barsky heuristic.
            double reach = segment / 3.0;
            return new CubicBezier(pts[first], pts[first] + tangent1 * reach, pts[last] + tangent2 * reach, pts[last]);
        }
        return new CubicBezier(pts[first], pts[first] + tangent1 * alphaLeft, pts[last] + tangent2 * alphaRight, pts[last]);
    }

    private static (double Error, int Split) MaxError(List<Vector> pts, int first, int last, CubicBezier bezier, double[] parameters)
    {
        double worst = 0;
        int split = (first + last) / 2;
        for (int i = 1; i < last - first; i++)
        {
            double squared = (bezier.Point(parameters[i]) - pts[first + i]).LengthSquared;
            if (squared >= worst) { worst = squared; split = first + i; }
        }
        return (Math.Sqrt(worst), split);
    }

    private static double[] Reparameterize(List<Vector> pts, int first, double[] parameters, CubicBezier bezier)
    {
        var improved = new double[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
            improved[i] = Math.Clamp(NewtonRaphson(bezier, pts[first + i], parameters[i]), 0, 1);
        improved[0] = 0;
        improved[^1] = 1;
        return improved;
    }

    /// <summary>One Newton-Raphson step: the parameter that locally minimises the distance to the curve.</summary>
    private static double NewtonRaphson(CubicBezier bezier, Vector point, double parameter)
    {
        double oneMinusT = 1 - parameter;
        Vector onCurve = bezier.Point(parameter);
        Vector firstDerivative = 3 * oneMinusT * oneMinusT * (bezier.C1 - bezier.Start)
                               + 6 * oneMinusT * parameter * (bezier.C2 - bezier.C1)
                               + 3 * parameter * parameter * (bezier.End - bezier.C2);
        Vector secondDerivative = 6 * oneMinusT * (bezier.C2 - 2 * bezier.C1 + bezier.Start)
                                + 6 * parameter * (bezier.End - 2 * bezier.C2 + bezier.C1);

        Vector offset = onCurve - point;
        double denominator = Vector.Dot(firstDerivative, firstDerivative) + Vector.Dot(offset, secondDerivative);
        if (Math.Abs(denominator) < 1e-12) return parameter;
        return parameter - Vector.Dot(offset, firstDerivative) / denominator;
    }

    private static double[] ChordLengthParameterize(List<Vector> pts, int first, int last)
    {
        int count = last - first + 1;
        var parameters = new double[count];
        for (int i = 1; i < count; i++) parameters[i] = parameters[i - 1] + (pts[first + i] - pts[first + i - 1]).Length;
        double total = parameters[count - 1];
        if (total <= 1e-12)
        {
            for (int i = 0; i < count; i++) parameters[i] = (double)i / (count - 1);
            return parameters;
        }
        for (int i = 0; i < count; i++) parameters[i] /= total;
        parameters[count - 1] = 1;
        return parameters;
    }

    private static double DistanceToSegment(Vector point, Vector start, Vector end)
    {
        Vector span = end - start;
        double lengthSquared = span.LengthSquared;
        if (lengthSquared < 1e-12) return (point - start).Length;
        double along = Math.Clamp(Vector.Dot(point - start, span) / lengthSquared, 0, 1);
        return (point - (start + span * along)).Length;
    }

    private static Vector Normalize(Vector direction) => direction.Length < 1e-12 ? new Vector(1, 0) : direction / direction.Length;

    private static double B0(double parameter) { double s = 1 - parameter; return s * s * s; }
    private static double B1(double parameter) { double s = 1 - parameter; return 3 * parameter * s * s; }
    private static double B2(double parameter) { double s = 1 - parameter; return 3 * parameter * parameter * s; }
    private static double B3(double parameter) => parameter * parameter * parameter;
}
