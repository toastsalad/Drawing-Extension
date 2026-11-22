using PlotterHelper.Geometry;
using PlotterHelper.Svg;

namespace PlotterHelper.Cli;

/// <summary>Debug stats dump (<c>--debug-stats</c>): per-path curve/flat-point/length
/// totals plus a grand total. Intended for quick diagnosis of flattening cost.</summary>
internal static class DebugStats
{
    internal static void Print(Options opts)
    {
        var doc = SvgParser.ParseFile(opts.Input, handleViewBox: opts.HandleViewBox);
        double tolerance = opts.FlatteningTolerance;
        double totalLen = 0;
        int totalCurves = 0;
        int pathIndex = 0;
        foreach (var path in doc.Paths)
        {
            double len = 0;
            Vector prev = default;
            bool first = true;
            int npts = 0;
            foreach (var curve in path.Curves)
            {
                var chain = new LineSegmentChain();
                chain.AppendFlattened(curve, tolerance);
                foreach (var point in chain.Points)
                {
                    if (!first) len += Vector.Distance(prev, point);
                    prev = point; first = false; npts++;
                }
            }
            totalLen += len; totalCurves += path.Curves.Count;
            Console.Error.WriteLine($"path {pathIndex++}: curves={path.Curves.Count} flatPts={npts} len={len:F2}");
        }
        Console.Error.WriteLine($"TOTAL paths={doc.Paths.Count} curves={totalCurves} flatLen={totalLen:F2}");
    }
}