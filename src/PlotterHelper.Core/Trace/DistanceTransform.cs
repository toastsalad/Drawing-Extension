namespace PlotterHelper.Trace;

/// <summary>
/// Exact Euclidean distance transform of the ink mask: for each pixel, the distance to the
/// nearest non-ink pixel (Felzenszwalb &amp; Huttenlocher's O(n) lower-envelope algorithm).
/// The tracer reads this as the local half-width of the ink, so every graph-cleanup threshold
/// stays proportional to how thick the drawing actually is.
/// </summary>
public static class DistanceTransform
{
    private const double Infinity = 1e20;

    public static double[] Compute(bool[] ink, int width, int height, int maxParallelism = -1)
    {
        var field = new double[width * height];
        for (int i = 0; i < field.Length; i++) field[i] = ink[i] ? Infinity : 0;

        int longest = Math.Max(width, height);
        int workers = Parallelism.Workers(maxParallelism, (long)width * height);
        var options = Parallelism.Options(workers);

        if (workers > 1)
        {
            Parallel.For(0, width, options,
                () => new Scratch(longest),
                (x, state, scratch) => { Column(field, width, height, x, scratch); return scratch; },
                scratch => { });
            Parallel.For(0, height, options,
                () => new Scratch(longest),
                (y, state, scratch) => { Row(field, width, y, scratch); return scratch; },
                scratch => { });
        }
        else
        {
            var scratch = new Scratch(longest);
            for (int x = 0; x < width; x++) Column(field, width, height, x, scratch);
            for (int y = 0; y < height; y++) Row(field, width, y, scratch);
        }
        return field;
    }

    /// <summary>Per-thread scratch, so the inner loops allocate nothing.</summary>
    private sealed class Scratch
    {
        public readonly double[] Line;
        public readonly double[] Result;
        public readonly int[] Sites;
        public readonly double[] Bounds;

        public Scratch(int longest)
        {
            Line = new double[longest];
            Result = new double[longest];
            Sites = new int[longest];
            Bounds = new double[longest + 1];
        }
    }

    private static void Column(double[] field, int width, int height, int x, Scratch scratch)
    {
        for (int y = 0; y < height; y++) scratch.Line[y] = field[y * width + x];
        Envelope(scratch.Line, height, scratch.Result, scratch.Sites, scratch.Bounds);
        for (int y = 0; y < height; y++) field[y * width + x] = scratch.Result[y];
    }

    private static void Row(double[] field, int width, int y, Scratch scratch)
    {
        int row = y * width;
        for (int x = 0; x < width; x++) scratch.Line[x] = field[row + x];
        Envelope(scratch.Line, width, scratch.Result, scratch.Sites, scratch.Bounds);
        for (int x = 0; x < width; x++) field[row + x] = Math.Sqrt(scratch.Result[x]);
    }

    /// <summary>Sample the medians of a small window of a distance field, for a robust half-width.</summary>
    public static double Median(IReadOnlyList<double> values, int from, int count)
    {
        if (count <= 0) return 0;
        var window = new double[count];
        for (int i = 0; i < count; i++) window[i] = values[from + i];
        Array.Sort(window);
        return window[count / 2];
    }

    /// <summary>
    /// 1D squared-distance transform: <c>out[q] = min_p (q-p)^2 + f[p]</c>, where sites holding
    /// +infinity simply never win. The <c>k &gt; 0</c> guard keeps the parabola stack from
    /// underflowing when f is not finite everywhere.
    /// </summary>
    private static void Envelope(double[] values, int count, double[] result, int[] sites, double[] bounds)
    {
        int top = 0;
        sites[0] = 0;
        bounds[0] = double.NegativeInfinity;
        bounds[1] = double.PositiveInfinity;

        for (int position = 1; position < count; position++)
        {
            double crossing = Intersection(values, position, sites[top]);
            while (top > 0 && crossing <= bounds[top])
            {
                top--;
                crossing = Intersection(values, position, sites[top]);
            }
            top++;
            sites[top] = position;
            bounds[top] = crossing;
            bounds[top + 1] = double.PositiveInfinity;
        }

        top = 0;
        for (int position = 0; position < count; position++)
        {
            while (top < count - 1 && bounds[top + 1] < position) top++;
            int site = sites[top];
            result[position] = (double)(position - site) * (position - site) + values[site];
        }
    }

    private static double Intersection(double[] values, int position, int site)
    {
        double numerator = values[position] + (double)position * position - values[site] - (double)site * site;
        return numerator / (2.0 * (position - site));
    }
}
