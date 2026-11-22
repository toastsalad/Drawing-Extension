namespace PlotterHelper.Geometry;

/// <summary>
/// A 2D affine transform stored as [a c e; b d f; 0 0 1] (SVG convention).
/// Concrete value type — applying a transform to millions of points during
/// flattening/intersection is a hot path and must not allocate.
/// </summary>
public readonly struct Affine
{
    public readonly double A, B, C, D, E, F;

    public Affine(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; }

    public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Apply the full affine map (linear + translation) to a point.</summary>
    public Vector Apply(Vector point) => new(A * point.X + C * point.Y + E, B * point.X + D * point.Y + F);

    public double Determinant => A * D - B * C;

    /// <summary>Compose two transforms: result = this * other (other applied first).</summary>
    public Affine Compose(Affine other)
    {
        // this * other, where each is [a c e; b d f; 0 0 1]
        double newA = A * other.A + C * other.B;
        double newC = A * other.C + C * other.D;
        double newE = A * other.E + C * other.F + E;
        double newB = B * other.A + D * other.B;
        double newD = B * other.C + D * other.D;
        double newF = B * other.E + D * other.F + F;
        return new(newA, newB, newC, newD, newE, newF);
    }

    /// <summary>Invert the transform. Returns null if singular.</summary>
    public Affine? Inverse()
    {
        double det = Determinant;
        if (det == 0) return null;
        double inv = 1.0 / det;
        return new(
            D * inv,
            -B * inv,
            -C * inv,
            A * inv,
            (C * F - D * E) * inv,
            (B * E - A * F) * inv);
    }

    public static Affine Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
    public static Affine Scale(double s) => new(s, 0, 0, s, 0, 0);
    public static Affine Rotation(double radians) =>
        new(Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), 0, 0);
    public static Affine SkewX(double radians) => new(1, 0, Math.Tan(radians), 1, 0, 0);
    public static Affine SkewY(double radians) => new(1, Math.Tan(radians), 0, 1, 0, 0);
}
