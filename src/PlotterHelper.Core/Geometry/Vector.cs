namespace PlotterHelper.Geometry;

/// <summary>
/// A 2D vector. Implemented as a readonly struct so that curve flattening and
/// hatch intersection stay allocation-free and cache-friendly — this is one of
/// the biggest wins over the original Python tuple-based vectors.
/// </summary>
public readonly struct Vector : IEquatable<Vector>
{
    public readonly double X;
    public readonly double Y;

    public Vector(double x, double y) { X = x; Y = y; }

    public static Vector Zero => default;

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector operator -(Vector a) => new(-a.X, -a.Y);
    public static Vector operator *(Vector a, double s) => new(a.X * s, a.Y * s);
    public static Vector operator *(double s, Vector a) => new(a.X * s, a.Y * s);
    public static Vector operator /(Vector a, double s) => new(a.X / s, a.Y / s);

    public static double Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(Vector a, Vector b) => a.X * b.Y - a.Y * b.X;

    public static double Distance(Vector a, Vector b) => (a - b).Length;
    public static double DistanceSquared(Vector a, Vector b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public static Vector Lerp(Vector a, Vector b, double t) =>
        new(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));

    public bool Equals(Vector other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is Vector other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(Vector a, Vector b) => a.Equals(b);
    public static bool operator !=(Vector a, Vector b) => !a.Equals(b);

    public override string ToString() => $"({X}, {Y})";
}
