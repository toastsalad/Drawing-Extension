namespace PlotterHelper.Geometry;

/// <summary>
/// A continuous chain of line segments, stored as an ordered list of vertices.
/// Built by flattening <see cref="Curve"/>s. The compiler walks the vertices to
/// emit G1 moves; the hatch engine walks them as polygon edges.
/// </summary>
public sealed class LineSegmentChain
{
    public List<Vector> Points { get; } = new();

    public int Count => Points.Count;

    public Vector this[int i] => Points[i];

    public void Append(Vector point) => Points.Add(point);

    /// <summary>
    /// Flatten <paramref name="curve"/> and append its vertices (sharing the
    /// existing tail when the chain is non-empty and continuous) to this chain.
    /// </summary>
    public void AppendFlattened(Curve curve, double tolerance)
    {
        if (Points.Count == 0)
        {
            Points.Add(curve.Start);
        }
        else
        {
            // Ensure continuity: if the chain's tail differs from the curve start,
            // snap it (floating-point drift only).
            Points[^1] = curve.Start;
        }
        curve.Flatten(Points, tolerance);
    }
}
