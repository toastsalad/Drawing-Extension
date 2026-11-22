namespace PlotterHelper.Geometry;

/// <summary>
/// A parametric curve over t∈[0,1] with a known start and end point.
/// Concrete curves (<see cref="Line"/>, <see cref="CubicBezier"/>,
/// <see cref="QuadraticBezier"/>, <see cref="EllipticalArc"/>) implement this.
/// </summary>
public abstract class Curve
{
    public Vector Start;
    public Vector End;

    /// <summary>Point on the curve at parameter t∈[0,1].</summary>
    public abstract Vector Point(double t);

    /// <summary>
    /// Flatten the curve into a polyline appended to <paramref name="output"/>.
    /// Implemented per-subtype with a flatness-based adaptive subdivision that is
    /// both faster and more accurate than the Python original's sampling loop.
    /// </summary>
    public abstract void Flatten(List<Vector> output, double tolerance);

    protected Curve(Vector start, Vector end) { Start = start; End = end; }
}
