namespace PlotterHelper.Trace;

/// <summary>Turns a grey raster into a clean ink mask.</summary>
public static class Binarizer
{
    /// <summary>Windows flatter than this are treated as uniform (see the solid-ink branch below).</summary>
    private const double UniformSd = 8.0;

    /// <summary>Cutoff used inside a uniform window, and for <paramref name="globalThreshold"/>.</summary>
    private const double MidGrey = 128.0;

    /// <summary>
    /// Sauvola local threshold over integral images. A global cutoff cannot separate ink from
    /// paper on a shaded or JPEG-blocked scan, and on dense line art it welds neighbouring lines
    /// together. A window flatter than <see cref="UniformSd"/> falls back to a fixed mid-grey so
    /// solid black reads as ink instead of matching its own mean (the classic hollow-region
    /// artifact).
    /// </summary>
    public static bool[] Threshold(GrayImage image, int window, double k, double globalThreshold, int maxParallelism = -1)
    {
        int width = image.Width, height = image.Height;
        var ink = new bool[width * height];

        if (globalThreshold > 0)
        {
            for (int i = 0; i < ink.Length; i++) ink[i] = image.Data[i] < globalThreshold;
            return ink;
        }

        // long: 255 * 4000 * 4000 overflows int.
        int stride = width + 1;
        var sum = new long[stride * (height + 1)];
        var sumSq = new long[stride * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            long rowSum = 0, rowSq = 0;
            for (int x = 0; x < width; x++)
            {
                int value = image.Data[y * width + x];
                rowSum += value;
                rowSq += (long)value * value;
                sum[(y + 1) * stride + x + 1] = sum[y * stride + x + 1] + rowSum;
                sumSq[(y + 1) * stride + x + 1] = sumSq[y * stride + x + 1] + rowSq;
            }
        }

        int radius = Math.Max(1, window / 2);
        const double dynamicRange = 128.0;

        // Each row reads the shared integral images and writes only its own output, so this
        // parallelises exactly (the integral images themselves must be built sequentially).
        int workers = Parallelism.Workers(maxParallelism, (long)width * height);
        if (workers > 1)
        {
            Parallel.For(0, height, Parallelism.Options(workers), y => ThresholdRow(image, sum, sumSq, ink, width, height, y, radius, k, dynamicRange));
            return ink;
        }

        for (int y = 0; y < height; y++) ThresholdRow(image, sum, sumSq, ink, width, height, y, radius, k, dynamicRange);
        return ink;
    }

    private static void ThresholdRow(GrayImage image, long[] sum, long[] sumSq, bool[] ink,
        int width, int height, int y, int radius, double k, double dynamicRange)
    {
        int y0 = Math.Max(0, y - radius), y1 = Math.Min(height - 1, y + radius);
        for (int x = 0; x < width; x++)
        {
            int x0 = Math.Max(0, x - radius), x1 = Math.Min(width - 1, x + radius);
            double area = (double)(x1 - x0 + 1) * (y1 - y0 + 1);
            double mean = Rect(sum, width, x0, y0, x1, y1) / area;
            double variance = Math.Max(0.0, Rect(sumSq, width, x0, y0, x1, y1) / area - mean * mean);
            double sd = Math.Sqrt(variance);
            double threshold = sd < UniformSd
                ? MidGrey
                : mean * (1.0 + k * (sd / dynamicRange - 1.0));
            ink[y * width + x] = image.Data[y * width + x] < threshold;
        }
    }

    /// <summary>Sum over the inclusive pixel rectangle [x0,x1] x [y0,y1].</summary>
    private static double Rect(long[] integral, int width, int x0, int y0, int x1, int y1)
    {
        int stride = width + 1;
        return integral[(y1 + 1) * stride + x1 + 1]
             - integral[y0 * stride + x1 + 1]
             - integral[(y1 + 1) * stride + x0]
             + integral[y0 * stride + x0];
    }

    /// <summary>Negate the mask (for white-on-black artwork).</summary>
    public static void Invert(bool[] mask)
    {
        for (int i = 0; i < mask.Length; i++) mask[i] = !mask[i];
    }

    /// <summary>
    /// Drop ink blobs smaller than <paramref name="minArea"/> px and label what is left.
    /// Deliberately no morphological closing: on dense line art a closing pass fuses adjacent
    /// lines into one slab, the failure this tracer exists to avoid.
    /// </summary>
    public static int[] DespeckleAndLabel(bool[] mask, int width, int height, double minArea, out int labelCount)
    {
        var label = new int[mask.Length];
        var stack = new Stack<int>();
        int next = 0;

        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0) continue;
            int id = ++next;
            var members = new List<int>();
            stack.Push(start);
            label[start] = id;
            while (stack.Count > 0)
            {
                int pixel = stack.Pop();
                members.Add(pixel);
                int px = pixel % width, py = pixel / width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx;
                        if (nx < 0 || nx >= width) continue;
                        int neighbour = ny * width + nx;
                        if (mask[neighbour] && label[neighbour] == 0) { label[neighbour] = id; stack.Push(neighbour); }
                    }
                }
            }
            if (members.Count < minArea)
            {
                foreach (int pixel in members) { mask[pixel] = false; label[pixel] = 0; }
                next--;
            }
        }

        labelCount = next;
        return label;
    }

    /// <summary>
    /// Add a one-pixel non-ink border, which every later stage assumes: the distance transform
    /// needs a non-ink pixel in every row and column, and thinning should see the image edge.
    /// </summary>
    public static bool[] Pad(bool[] mask, int width, int height, out int paddedWidth, out int paddedHeight) =>
        Pad(mask, width, height, false, out paddedWidth, out paddedHeight);

    /// <summary>Widen any per-pixel array by a one-pixel border of <paramref name="fill"/>.</summary>
    public static T[] Pad<T>(T[] data, int width, int height, T fill, out int paddedWidth, out int paddedHeight)
    {
        paddedWidth = width + 2;
        paddedHeight = height + 2;
        var padded = new T[paddedWidth * paddedHeight];
        if (!EqualityComparer<T>.Default.Equals(fill, default!))
            Array.Fill(padded, fill);
        for (int y = 0; y < height; y++)
            Array.Copy(data, y * width, padded, (y + 1) * paddedWidth + 1, width);
        return padded;
    }
}
