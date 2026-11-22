using PlotterHelper.Geometry;

namespace PlotterHelper.Trace;

/// <summary>
/// Boundary extraction for the parts of the drawing that are too thick to be a pen stroke: a
/// centerline says nothing about the shape of a blob, so those are handed to the hatch engine
/// as a closed outline instead.
///
/// Marching squares over the 2x2 cell, with the crossing placed at the cell edge midpoint.
/// Contours come out of the dual lattice at half-integer coordinates, so stitching is a walk
/// over edge midpoints: each midpoint belongs to exactly two cells and is therefore touched by
/// at most two segments.
///
/// Outer boundaries and holes are all returned; the caller puts them on one path and the hatch
/// engine's even-odd fill sorts the nesting out.
/// </summary>
public static class MarchingSquares
{
    public static List<List<Vector>> Trace(bool[] mask, int width, int height, int left, int top, int right, int bottom)
    {
        bool Ink(int x, int y) =>
            x >= left && y >= top && x < right && y < bottom && mask[y * width + x];

        var starts = new List<long>();
        var ends = new List<long>();
        var incident = new Dictionary<long, List<int>>();

        void Segment(long from, long to)
        {
            int id = starts.Count;
            starts.Add(from);
            ends.Add(to);
            Add(incident, from, id);
            Add(incident, to, id);
        }

        for (int y = top - 1; y < bottom; y++)
        {
            for (int x = left - 1; x < right; x++)
            {
                bool topLeft = Ink(x, y), topRight = Ink(x + 1, y), bottomRight = Ink(x + 1, y + 1), bottomLeft = Ink(x, y + 1);
                switch ((topLeft ? 1 : 0) | (topRight ? 2 : 0) | (bottomRight ? 4 : 0) | (bottomLeft ? 8 : 0))
                {
                    case 1: case 14: Segment(Left(x, y), Top(x, y)); break;
                    case 2: case 13: Segment(Top(x, y), Right(x, y)); break;
                    case 3: case 12: Segment(Left(x, y), Right(x, y)); break;
                    case 4: case 11: Segment(Right(x, y), Bottom(x, y)); break;
                    case 6: case 9: Segment(Top(x, y), Bottom(x, y)); break;
                    case 7: case 8: Segment(Left(x, y), Bottom(x, y)); break;
                    case 5:
                        // Saddle: pair each ink corner with itself; either pairing gives a valid
                        // even-odd fill.
                        Segment(Left(x, y), Top(x, y));
                        Segment(Right(x, y), Bottom(x, y));
                        break;
                    case 10:
                        Segment(Top(x, y), Right(x, y));
                        Segment(Bottom(x, y), Left(x, y));
                        break;
                }
            }
        }

        var used = new bool[starts.Count];
        var loops = new List<List<Vector>>();
        for (int seed = 0; seed < starts.Count; seed++)
        {
            if (used[seed]) continue;
            used[seed] = true;

            long start = starts[seed];
            long at = ends[seed];
            var loop = new List<Vector> { Decode(start), Decode(at) };

            // Walk from the point we actually arrived at, not from ends[segment]: a segment is
            // traversed in whichever direction the walk entered it, so the chain would break.
            int guard = starts.Count + 4;
            while (guard-- > 0)
            {
                int next = NextSegment(incident, at, used);
                if (next < 0) break;
                used[next] = true;
                at = starts[next] == at ? ends[next] : starts[next];
                loop.Add(Decode(at));
                if (at == start) break;
            }

            if (loop.Count > 2) loops.Add(loop);
        }

        return loops;
    }

    /// <summary>The unused segment leaving <paramref name="point"/>, if any.</summary>
    private static int NextSegment(Dictionary<long, List<int>> incident, long point, bool[] used)
    {
        if (!incident.TryGetValue(point, out var ids)) return -1;
        foreach (int id in ids) if (!used[id]) return id;
        return -1;
    }

    private static void Add(Dictionary<long, List<int>> incident, long point, int id)
    {
        if (!incident.TryGetValue(point, out var ids)) incident[point] = ids = new List<int>(2);
        ids.Add(id);
    }

    // Edge midpoints of cell (x, y), on the doubled lattice so they stay integral.
    private static long Top(int x, int y) => Key(2 * x + 1, 2 * y);
    private static long Right(int x, int y) => Key(2 * x + 2, 2 * y + 1);
    private static long Bottom(int x, int y) => Key(2 * x + 1, 2 * y + 2);
    private static long Left(int x, int y) => Key(2 * x, 2 * y + 1);

    private static long Key(int x, int y) => ((long)y << 32) | (uint)x;

    private static Vector Decode(long key) => new((int)(key & 0xFFFFFFFF) / 2.0, (int)(key >> 32) / 2.0);
}
