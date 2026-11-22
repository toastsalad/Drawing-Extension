namespace PlotterHelper.Trace;

/// <summary>
/// Zhang-Suen thinning: reduce the ink mask to a one-pixel-wide skeleton.
///
/// Two things make this fast enough for a multi-megapixel trace: the eight-neighbour deletion
/// test collapses to a 256-entry lookup table, and the test is per-pixel independent within a
/// sub-step, so the frontier scan runs on every core. Only boundary pixels can be deleted, so
/// instead of rescanning the image each round the pass walks a frontier set and rebuilds it
/// from the neighbourhood of whatever it deleted.
/// </summary>
public static class Thinning
{
    /// <summary>Deletable[step * 256 + pattern]; see <see cref="BuildDeletableTable"/>.</summary>
    private static readonly bool[] Deletable = BuildDeletableTable();

    public static bool[] Skeletonize(bool[] ink, int width, int height, int maxParallelism = -1)
    {
        var image = (bool[])ink.Clone();
        var current = new List<int>();
        var next = new List<int>();
        var queued = new bool[image.Length];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int pixel = y * width + x;
                if (image[pixel] && HasBackgroundNeighbour(image, width, pixel))
                {
                    current.Add(pixel);
                    queued[pixel] = true;
                }
            }
        }

        while (current.Count > 0)
        {
            int deletedThisRound = 0;
            for (int step = 0; step < 2; step++)
            {
                deletedThisRound += Delete(image, width, height, current, queued, next, step * 256, maxParallelism);
                (current, next) = (next, current);
            }
            // Zhang-Suen has converged once a whole round (both sub-steps) deletes nothing.
            if (deletedThisRound == 0) break;
        }
        return image;
    }

    /// <summary>
    /// One Zhang-Suen sub-step: mark every deletable frontier pixel, then remove them all together.
    /// Returns how many were removed.
    /// </summary>
    private static int Delete(bool[] image, int width, int height, List<int> current, bool[] queued,
        List<int> next, int tableOffset, int maxParallelism)
    {
        var deletions = new List<int>();
        int workers = Parallelism.Workers(maxParallelism, current.Count);

        if (workers > 1)
        {
            var parts = new List<int>[workers];
            Parallel.For(0, workers, Parallelism.Options(workers), worker =>
            {
                var mine = new List<int>();
                int from = (int)((long)current.Count * worker / workers);
                int to = (int)((long)current.Count * (worker + 1) / workers);
                for (int slot = from; slot < to; slot++)
                {
                    int pixel = current[slot];
                    if (image[pixel] && Deletable[tableOffset + Pattern(image, width, pixel)]) mine.Add(pixel);
                }
                parts[worker] = mine;
            });
            foreach (var part in parts) deletions.AddRange(part);
        }
        else
        {
            for (int slot = 0; slot < current.Count; slot++)
            {
                int pixel = current[slot];
                if (image[pixel] && Deletable[tableOffset + Pattern(image, width, pixel)]) deletions.Add(pixel);
            }
        }

        foreach (int pixel in deletions) image[pixel] = false;

        // The new frontier is whatever survived plus the 8-neighbourhood of every deletion, whose
        // pixels may have just become boundary pixels.
        foreach (int pixel in current) queued[pixel] = false;
        next.Clear();
        foreach (int pixel in current)
            if (image[pixel] && !queued[pixel]) { queued[pixel] = true; next.Add(pixel); }
        foreach (int pixel in deletions)
        {
            int px = pixel % width, py = pixel / width;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = py + dy;
                if (ny < 1 || ny >= height - 1) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx;
                    if (nx < 1 || nx >= width - 1) continue;
                    int neighbour = ny * width + nx;
                    if (image[neighbour] && !queued[neighbour]) { queued[neighbour] = true; next.Add(neighbour); }
                }
            }
        }
        return deletions.Count;
    }

    /// <summary>
    /// The eight neighbours packed into a byte, p2..p9 bit 0..7 clockwise from north. Building this
    /// is all that is left of the deletion test in the hot loop.
    /// </summary>
    private static int Pattern(bool[] image, int width, int pixel) =>
          (image[pixel - width] ? 1 : 0)
        | (image[pixel - width + 1] ? 2 : 0)
        | (image[pixel + 1] ? 4 : 0)
        | (image[pixel + width + 1] ? 8 : 0)
        | (image[pixel + width] ? 16 : 0)
        | (image[pixel + width - 1] ? 32 : 0)
        | (image[pixel - 1] ? 64 : 0)
        | (image[pixel - width - 1] ? 128 : 0);

    private static bool HasBackgroundNeighbour(bool[] image, int width, int pixel)
    {
        for (int dy = -1; dy <= 1; dy++)
            if (!image[pixel + dy * width - 1] || !image[pixel + dy * width] || !image[pixel + dy * width + 1]) return true;
        return false;
    }

    /// <summary>
    /// Precompute the Zhang-Suen decision for every eight-neighbour pattern, for both sub-steps.
    /// Pixel p1 is deletable when it has 2..6 ink neighbours, exactly one 0-&gt;1 transition around
    /// the ring, and no full 2x2 block on the side the sub-step targets.
    /// </summary>
    private static bool[] BuildDeletableTable()
    {
        var table = new bool[2 * 256];
        for (int step = 0; step < 2; step++)
        {
            for (int pattern = 0; pattern < 256; pattern++)
            {
                bool north = (pattern & 1) != 0, northEast = (pattern & 2) != 0, east = (pattern & 4) != 0, southEast = (pattern & 8) != 0;
                bool south = (pattern & 16) != 0, southWest = (pattern & 32) != 0, west = (pattern & 64) != 0, northWest = (pattern & 128) != 0;

                int neighbours = (north ? 1 : 0) + (northEast ? 1 : 0) + (east ? 1 : 0) + (southEast ? 1 : 0)
                               + (south ? 1 : 0) + (southWest ? 1 : 0) + (west ? 1 : 0) + (northWest ? 1 : 0);
                if (neighbours < 2 || neighbours > 6) continue;

                int transitions = (!north && northEast ? 1 : 0) + (!northEast && east ? 1 : 0) + (!east && southEast ? 1 : 0) + (!southEast && south ? 1 : 0)
                                + (!south && southWest ? 1 : 0) + (!southWest && west ? 1 : 0) + (!west && northWest ? 1 : 0) + (!northWest && north ? 1 : 0);
                if (transitions != 1) continue;

                table[step * 256 + pattern] = step == 0
                    ? !(north && east && south) && !(east && south && west)
                    : !(north && east && west) && !(north && south && west);
            }
        }
        return table;
    }
}
