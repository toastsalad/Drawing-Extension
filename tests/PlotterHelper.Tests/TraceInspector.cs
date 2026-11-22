using System.Globalization;
using System.Text.RegularExpressions;
using PlotterHelper.Geometry;

namespace PlotterHelper.Tests;

/// <summary>One &lt;path&gt; from a traced SVG.</summary>
internal sealed class TracePath
{
    public bool IsRegion;
    public readonly List<List<Vector>> Subpaths = new();

    public List<Vector> Points => Subpaths[0];

    public bool IsClosed => Subpaths.Count == 1
        && Subpaths[0].Count > 1
        && Subpaths[0][0] == Subpaths[0][^1];

    public double MinX, MinY = double.MaxValue, MaxX = double.MinValue, MaxY;

    public void Measure()
    {
        MinX = double.MaxValue;
        MinY = double.MaxValue;
        MaxX = double.MinValue;
        MaxY = double.MinValue;
        foreach (var subpath in Subpaths)
        {
            foreach (var point in subpath)
            {
                MinX = Math.Min(MinX, point.X);
                MaxX = Math.Max(MaxX, point.X);
                MinY = Math.Min(MinY, point.Y);
                MaxY = Math.Max(MaxY, point.Y);
            }
        }
    }
}

/// <summary>
/// Reads a traced SVG back into geometry, so the tests assert on what was actually written rather
/// than on tracer internals.
/// </summary>
internal static class TraceInspector
{
    private static readonly Regex PathElement = new("<path d=\"([^\"]*)\"([^>]*)/>", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"-?\d*\.?\d+(?:e-?\d+)?", RegexOptions.Compiled);

    public static List<TracePath> Paths(string svg)
    {
        var paths = new List<TracePath>();
        foreach (Match element in PathElement.Matches(svg))
        {
            var path = new TracePath { IsRegion = element.Groups[2].Value.Contains("fill=\"#000000\"") };
            foreach (var subpath in ParseSubpaths(element.Groups[1].Value)) path.Subpaths.Add(subpath);
            if (path.Subpaths.Count == 0) continue;
            path.Measure();
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Traced strokes only: the open centerline paths, excluding hatched regions.</summary>
    public static List<TracePath> Strokes(string svg) => Paths(svg).Where(path => !path.IsRegion).ToList();

    private static List<List<Vector>> ParseSubpaths(string data)
    {
        var subpaths = new List<List<Vector>>();
        var tokens = Regex.Matches(data, @"[MLCQmlcq]|-?\d*\.?\d+(?:e-?\d+)?");
        List<Vector>? current = null;
        var numbers = new List<double>();
        char command = '\0';

        foreach (Match token in tokens)
        {
            string text = token.Value;
            if (char.IsLetter(text[0]))
            {
                command = char.ToUpperInvariant(text[0]);
                numbers.Clear();
                continue;
            }

            numbers.Add(double.Parse(text, CultureInfo.InvariantCulture));
            int needed = command switch { 'M' or 'L' => 2, 'C' => 6, 'Q' => 4, _ => 0 };
            if (needed == 0 || numbers.Count < needed) continue;

            switch (command)
            {
                case 'M':
                    current = new List<Vector>();
                    subpaths.Add(current);
                    current.Add(new Vector(numbers[0], numbers[1]));
                    break;
                case 'L':
                    current?.Add(new Vector(numbers[0], numbers[1]));
                    break;
                case 'C':
                    current?.Add(new Vector(numbers[4], numbers[5]));
                    break;
                case 'Q':
                    current?.Add(new Vector(numbers[2], numbers[3]));
                    break;
            }
            numbers.Clear();
        }

        return subpaths;
    }
}
