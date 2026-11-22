namespace PlotterHelper.Geometry;

/// <summary>
/// An elliptical arc, parameterized by center, radii, rotation and angular sweep.
/// Constructed from SVG's endpoint parameterization (A command) via
/// <see cref="FromEndpoint"/>. Flattening uses recursive angular bisection with a
/// midpoint/chord flatness test, which is robust for arbitrary ellipses.
/// </summary>
public sealed class EllipticalArc : Curve
{
    public readonly Vector Center;
    public readonly Vector Radii;
    public readonly double Rotation;
    public readonly double StartAngle;
    public readonly double SweepAngle;
    public readonly Affine Transform;

    private EllipticalArc(Vector center, Vector radii, double rotation,
        double startAngle, double sweepAngle, Affine transform, Vector start, Vector end)
        : base(start, end)
    {
        Center = center; Radii = radii; Rotation = rotation;
        StartAngle = startAngle; SweepAngle = sweepAngle; Transform = transform;
    }

    public Vector AngleToPoint(double angle)
    {
        Vector local = new(Radii.X * Math.Cos(angle), Radii.Y * Math.Sin(angle));
        double cosA = Math.Cos(Rotation), sinA = Math.Sin(Rotation);
        Vector rotated = new(local.X * cosA - local.Y * sinA, local.X * sinA + local.Y * cosA);
        Vector point = rotated + Center;
        return Transform.Apply(point);
    }

    public override Vector Point(double t) =>
        AngleToPoint(StartAngle + t * SweepAngle);

    public override void Flatten(List<Vector> output, double tolerance)
    {
        // Recursive angular bisection. The start point was already emitted by the
        // chain, so we emit interior points and the end point.
        FlattenRange(output, StartAngle, StartAngle + SweepAngle, tolerance, 0, 24);
    }

    private void FlattenRange(List<Vector> output, double a0, double a1, double tol, int depth, int maxDepth)
    {
        Vector p0 = AngleToPoint(a0);
        Vector p1 = AngleToPoint(a1);
        if (depth >= maxDepth)
        {
            output.Add(p1);
            return;
        }
        Vector midPoint = AngleToPoint((a0 + a1) * 0.5);
        // Flatness: distance of the arc midpoint from the chord p0->p1.
        double dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double dist;
        if (len < 1e-12)
            dist = Vector.Distance(midPoint, p0);
        else
            dist = Math.Abs(dy * (midPoint.X - p0.X) - dx * (midPoint.Y - p0.Y)) / len;

        if (dist <= tol)
        {
            output.Add(p1);
        }
        else
        {
            FlattenRange(output, a0, (a0 + a1) * 0.5, tol, depth + 1, maxDepth);
            FlattenRange(output, (a0 + a1) * 0.5, a1, tol, depth + 1, maxDepth);
        }
    }

    /// <summary>
    /// Convert SVG endpoint arc parameterization to center parameterization and
    /// build an <see cref="EllipticalArc"/>. Implements the w3.org impl-note
    /// algorithm (https://www.w3.org/TR/SVG2/implnote.html).
    /// </summary>
    public static EllipticalArc? FromEndpoint(
        Vector start, Vector end, double rx, double ry, double rotationDeg,
        int largeArcFlag, int sweepFlag, Affine transform)
    {
        double rotationRad = rotationDeg * Math.PI / 180.0;

        if (start == end) return null; // degenerate; SVG spec: draw nothing

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            // A zero-radius arc is a straight line per the spec.
            return null;
        }

        // Step 1: compute (x1', y1') in the primed coordinate system.
        double dx = (start.X - end.X) / 2.0;
        double dy = (start.Y - end.Y) / 2.0;
        double cosR = Math.Cos(rotationRad), sinR = Math.Sin(rotationRad);
        double px = cosR * dx + sinR * dy;
        double py = -sinR * dx + cosR * dy;

        // Step 2: correct out-of-range radii.
        double lambda = (px * px) / (rx * rx) + (py * py) / (ry * ry);
        if (lambda > 1)
        {
            double scale = Math.Sqrt(lambda);
            rx *= scale; ry *= scale;
        }

        // Step 3: compute (cx', cy').
        double rx2 = rx * rx, ry2 = ry * ry;
        double px2 = px * px, py2 = py * py;
        double denom = rx2 * py2 + ry2 * px2;
        double radicand = denom == 0 ? 0 : (rx2 * ry2 - denom) / denom;
        radicand = Math.Max(0, radicand);
        double factor = Math.Sqrt(radicand);
        if (largeArcFlag == sweepFlag) factor = -factor;
        double cxp = factor * (rx * py) / ry;
        double cyp = factor * -(ry * px) / rx;

        // Step 4: compute (cx, cy).
        double cx = cosR * cxp - sinR * cyp + (start.X + end.X) / 2.0;
        double cy = sinR * cxp + cosR * cyp + (start.Y + end.Y) / 2.0;

        // Step 5: compute angles.
        double ax = (px - cxp) / rx;
        double ay = (py - cyp) / ry;
        double bx = (-px - cxp) / rx;
        double by = (-py - cyp) / ry;

        double startAngle = VectorAngle(new Vector(1, 0), new Vector(ax, ay));
        double sweepAngle = VectorAngle(new Vector(ax, ay), new Vector(bx, by));
        // Normalize to the directed sweep the flags request.
        if (sweepFlag == 0 && sweepAngle > 0) sweepAngle -= Formulas.TwoPi;
        if (sweepFlag == 1 && sweepAngle < 0) sweepAngle += Formulas.TwoPi;

        Vector center = new(cx, cy);
        Vector radii = new(rx, ry);
        Vector arcStart = transform.Apply(AngleToPointLocal(center, radii, rotationRad, startAngle));
        Vector arcEnd = transform.Apply(AngleToPointLocal(center, radii, rotationRad, startAngle + sweepAngle));
        return new EllipticalArc(center, radii, rotationRad, startAngle, sweepAngle, transform, arcStart, arcEnd);
    }

    private static Vector AngleToPointLocal(Vector center, Vector radii, double rotation, double angle)
    {
        Vector local = new(radii.X * Math.Cos(angle), radii.Y * Math.Sin(angle));
        double cosA = Math.Cos(rotation), sinA = Math.Sin(rotation);
        return new Vector(local.X * cosA - local.Y * sinA, local.X * sinA + local.Y * cosA) + center;
    }

    private static double VectorAngle(Vector u, Vector v)
    {
        double sign = (u.X * v.Y - u.Y * v.X) < 0 ? -1 : 1;
        double dot = u.X * v.X + u.Y * v.Y;
        double len = Math.Sqrt((u.X * u.X + u.Y * u.Y) * (v.X * v.X + v.Y * v.Y));
        if (len == 0) return 0;
        double cos = Math.Clamp(dot / len, -1, 1);
        return sign * Math.Acos(cos);
    }
}