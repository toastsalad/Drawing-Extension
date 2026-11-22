namespace PlotterHelper.Geometry;

/// <summary>A straight line segment.</summary>
public sealed class Line : Curve
{
    public Line(Vector start, Vector end) : base(start, end) { }

    public override Vector Point(double t) => Vector.Lerp(Start, End, t);

    public override void Flatten(List<Vector> output, double tolerance)
    {
        // A line is already flat. Caller has already emitted Start; emit End.
        output.Add(End);
    }
}
