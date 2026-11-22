namespace PlotterHelper.Trace;

/// <summary>An 8-bit single-channel raster, 0 = white paper and 255 = black ink.</summary>
public sealed class GrayImage
{
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Data;

    public GrayImage(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new byte[width * height];
    }

    public GrayImage(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Data = data;
    }

    public byte this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    /// <summary>
    /// Catmull-Rom bicubic resample. Tracing at 2-4x the source resolution is the biggest
    /// quality win on fine line art: a thin line survives the threshold instead of being cut
    /// into dashes, and the skeleton has room to run down its middle.
    /// </summary>
    public GrayImage Resample(int newWidth, int newHeight, int maxParallelism = -1)
    {
        if (newWidth <= 0 || newHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(newWidth), "resample target must be positive");

        var dst = new GrayImage(newWidth, newHeight);
        if (newWidth == Width && newHeight == Height)
        {
            Array.Copy(Data, dst.Data, Data.Length);
            return dst;
        }

        double sx = (double)Width / newWidth;
        double sy = (double)Height / newHeight;

        var tmp = new double[(long)newWidth * Height > int.MaxValue
            ? throw new ArgumentOutOfRangeException(nameof(newWidth), "resample target too large")
            : newWidth * Height];

        int workers = Parallelism.Workers(maxParallelism, (long)newWidth * newHeight);

        if (workers > 1)
        {
            Parallel.For(0, Height, Parallelism.Options(workers), y => HorizontalPass(y, newWidth, sx, tmp));
            Parallel.For(0, newHeight, Parallelism.Options(workers), y => VerticalPass(y, newWidth, newHeight, sy, tmp, dst));
            return dst;
        }

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < newWidth; x++)
            {
                double srcX = (x + 0.5) * sx - 0.5;
                int ix = (int)Math.Floor(srcX);
                double fx = srcX - ix;
                double acc = 0;
                for (int tap = -1; tap <= 2; tap++)
                    acc += CubicWeight(tap - fx) * Data[row + Math.Clamp(ix + tap, 0, Width - 1)];
                tmp[y * newWidth + x] = acc;
            }
        }

        for (int y = 0; y < newHeight; y++)
        {
            double srcY = (y + 0.5) * sy - 0.5;
            int iy = (int)Math.Floor(srcY);
            double fy = srcY - iy;
            for (int x = 0; x < newWidth; x++)
            {
                double acc = 0;
                for (int tap = -1; tap <= 2; tap++)
                    acc += CubicWeight(tap - fy) * tmp[Math.Clamp(iy + tap, 0, Height - 1) * newWidth + x];
                dst.Data[y * newWidth + x] = (byte)Math.Clamp((int)Math.Round(acc), 0, 255);
            }
        }
        return dst;
    }

    private void HorizontalPass(int y, int newWidth, double sx, double[] tmp)
    {
        int row = y * Width;
        for (int x = 0; x < newWidth; x++)
        {
            double srcX = (x + 0.5) * sx - 0.5;
            int ix = (int)Math.Floor(srcX);
            double fx = srcX - ix;
            double acc = 0;
            for (int tap = -1; tap <= 2; tap++)
                acc += CubicWeight(tap - fx) * Data[row + Math.Clamp(ix + tap, 0, Width - 1)];
            tmp[y * newWidth + x] = acc;
        }
    }

    private void VerticalPass(int y, int newWidth, int newHeight, double sy, double[] tmp, GrayImage dst)
    {
        double srcY = (y + 0.5) * sy - 0.5;
        int iy = (int)Math.Floor(srcY);
        double fy = srcY - iy;
        for (int x = 0; x < newWidth; x++)
        {
            double acc = 0;
            for (int tap = -1; tap <= 2; tap++)
                acc += CubicWeight(tap - fy) * tmp[Math.Clamp(iy + tap, 0, Height - 1) * newWidth + x];
            dst.Data[y * newWidth + x] = (byte)Math.Clamp((int)Math.Round(acc), 0, 255);
        }
    }

    /// <summary>Catmull-Rom kernel (the cubic with a = -0.5).</summary>
    private static double CubicWeight(double t)
    {
        t = Math.Abs(t);
        if (t < 1) return ((1.5 * t - 2.5) * t) * t + 1;
        if (t < 2) return (((-0.5 * t + 2.5) * t) - 4) * t + 2;
        return 0;
    }
}
