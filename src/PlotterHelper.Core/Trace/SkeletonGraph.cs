using PlotterHelper.Geometry;

namespace PlotterHelper.Trace;

/// <summary>A skeleton vertex: an endpoint, a junction cluster collapsed to a point, or a lone pixel.</summary>
public sealed class SkeletonNode
{
    public Vector Position;
    public readonly List<int> Edges = new();
    public bool Removed;

    /// <summary>Number of incident edges that are still live.</summary>
    public int Degree;
}

/// <summary>A skeleton polyline between two nodes, with the ink half-width sampled along it.</summary>
public sealed class SkeletonEdge
{
    public int NodeA;
    public int NodeB;
    public readonly List<Vector> Points = new();
    public readonly List<double> Radii = new();
    public bool Removed;

    /// <summary>Position in <see cref="SkeletonGraph.Edges"/>, kept stable so removal only flips
    /// <see cref="Removed"/>.</summary>
    public int Index;

    public double Length;
    public double Radius;
    public double MinRadius;
    public double MaxRadius;
    public double RadiusAtA;
    public double RadiusAtB;

    public bool IsLoop => NodeA == NodeB;

    /// <summary>Half-width near the given end; length thresholds scale with this.</summary>
    public double RadiusAt(int node) => node == NodeA ? RadiusAtA : RadiusAtB;

    public int OtherEnd(int node) => node == NodeA ? NodeB : NodeA;

    /// <summary>Reverse the point order and node ends in place.</summary>
    public void Reverse()
    {
        Points.Reverse();
        Radii.Reverse();
        (NodeA, NodeB) = (NodeB, NodeA);
        (RadiusAtA, RadiusAtB) = (RadiusAtB, RadiusAtA);
    }

    /// <summary>Recompute length and the radius statistics from <see cref="Radii"/>.</summary>
    public void Measure()
    {
        Length = 0;
        for (int i = 1; i < Points.Count; i++) Length += (Points[i] - Points[i - 1]).Length;

        if (Radii.Count == 0) { Radius = MinRadius = MaxRadius = RadiusAtA = RadiusAtB = 0; return; }
        var sorted = new double[Radii.Count];
        Radii.CopyTo(sorted);
        Array.Sort(sorted);
        Radius = sorted[sorted.Length / 2];
        MinRadius = sorted[0];
        MaxRadius = sorted[^1];

        int window = Math.Min(5, Radii.Count);
        RadiusAtA = DistanceTransform.Median(Radii, 0, window);
        RadiusAtB = DistanceTransform.Median(Radii, Radii.Count - window, window);
    }
}

/// <summary>
/// Skeleton pixels assembled into a graph. Nodes are the skeleton pixels whose 8-neighbour
/// count is not 2 (endpoints and junction clusters); edges are the chains of degree-2 pixels
/// between them.
/// </summary>
public sealed class SkeletonGraph
{
    public readonly List<SkeletonNode> Nodes = new();
    public readonly List<SkeletonEdge> Edges = new();

    private readonly int _width;
    private readonly int _height;

    /// <summary>Per-pixel bitmask of the directions already walked, so each chain is taken once.</summary>
    private byte[] _used = Array.Empty<byte>();

    private SkeletonGraph(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public static SkeletonGraph Build(bool[] skeleton, int width, int height, double[] distance, int maxParallelism = -1)
    {
        var graph = new SkeletonGraph(width, height);
        var degree = ComputeDegrees(skeleton, width, height, maxParallelism);

        var nodeOf = new int[skeleton.Length];
        Array.Fill(nodeOf, -1);
        graph.ClusterNodes(skeleton, degree, nodeOf);

        graph._used = new byte[skeleton.Length];
        graph.WalkChains(skeleton, degree, nodeOf, distance);
        graph.WalkIsolatedLoops(skeleton, degree, nodeOf, distance);
        return graph;
    }

    /// <summary>Add an edge and register it with both end nodes.</summary>
    public void Attach(SkeletonEdge edge)
    {
        edge.Index = Edges.Count;
        Edges.Add(edge);
        Nodes[edge.NodeA].Edges.Add(edge.Index);
        Nodes[edge.NodeA].Degree++;
        if (edge.NodeB != edge.NodeA)
        {
            Nodes[edge.NodeB].Edges.Add(edge.Index);
            Nodes[edge.NodeB].Degree++;
        }
    }

    /// <summary>Unhook an edge from both end nodes; it keeps its slot in <see cref="Edges"/>.</summary>
    public void Detach(SkeletonEdge edge)
    {
        Nodes[edge.NodeA].Edges.Remove(edge.Index);
        Nodes[edge.NodeA].Degree--;
        if (edge.NodeB != edge.NodeA)
        {
            Nodes[edge.NodeB].Edges.Remove(edge.Index);
            Nodes[edge.NodeB].Degree--;
        }
    }

    private static byte[] ComputeDegrees(bool[] skeleton, int width, int height, int maxParallelism)
    {
        var degree = new byte[skeleton.Length];
        int workers = Parallelism.Workers(maxParallelism, skeleton.Length);
        if (workers > 1)
        {
            Parallel.For(1, height - 1, Parallelism.Options(workers), y => DegreeRow(skeleton, degree, width, y));
            return degree;
        }
        for (int y = 1; y < height - 1; y++) DegreeRow(skeleton, degree, width, y);
        return degree;
    }

    private static void DegreeRow(bool[] skeleton, byte[] degree, int width, int y)
    {
        for (int x = 1; x < width - 1; x++)
        {
            int pixel = y * width + x;
            if (!skeleton[pixel]) continue;
            int count = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dy != 0) && skeleton[(y + dy) * width + x + dx]) count++;
            degree[pixel] = (byte)count;
        }
    }

    /// <summary>
    /// Junction pixels belonging to one crossing are usually a small blob, not a single pixel.
    /// Cluster them 8-connectedly into one node, or a single X explodes into a dozen micro-edges
    /// that no later cleanup can reassemble into two lines.
    /// </summary>
    private void ClusterNodes(bool[] skeleton, byte[] degree, int[] nodeOf)
    {
        var stack = new Stack<int>();
        var cluster = new List<int>();
        for (int start = 0; start < skeleton.Length; start++)
        {
            if (!skeleton[start] || degree[start] == 2 || nodeOf[start] >= 0) continue;
            int id = Nodes.Count;
            nodeOf[start] = id;
            stack.Push(start);
            cluster.Clear();
            while (stack.Count > 0)
            {
                int pixel = stack.Pop();
                cluster.Add(pixel);
                int px = pixel % _width, py = pixel / _width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= _height) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx;
                        if (nx < 0 || nx >= _width) continue;
                        int neighbour = ny * _width + nx;
                        if (skeleton[neighbour] && degree[neighbour] != 2 && nodeOf[neighbour] < 0)
                        {
                            nodeOf[neighbour] = id;
                            stack.Push(neighbour);
                        }
                    }
                }
            }
            double sumX = 0, sumY = 0;
            foreach (int pixel in cluster) { sumX += pixel % _width; sumY += pixel / _width; }
            Nodes.Add(new SkeletonNode { Position = new Vector(sumX / cluster.Count, sumY / cluster.Count) });
        }
    }

    private void WalkChains(bool[] skeleton, byte[] degree, int[] nodeOf, double[] distance)
    {
        for (int p = 0; p < skeleton.Length; p++)
        {
            if (!skeleton[p] || nodeOf[p] < 0) continue;
            int px = p % _width, py = p / _width;
            for (int dy = -1; dy <= 1; dy++)
            {
                int qy = py + dy;
                if (qy < 0 || qy >= _height) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int qx = px + dx;
                    if (qx < 0 || qx >= _width) continue;
                    int q = qy * _width + qx;
                    if (!skeleton[q]) continue;
                    if (IsUsed(p, dx, dy)) continue;
                    Consume(p, dx, dy, q);

                    var edge = new SkeletonEdge { NodeA = nodeOf[p] };
                    edge.Points.Add(Nodes[nodeOf[p]].Position);
                    edge.Radii.Add(distance[p]);

                    // x and y are carried along rather than recovered from the index with % and /;
                    // eight integer divisions per step cost more than everything else in this loop.
                    int previous = p, current = q, cx = qx, cy = qy;
                    int guard = skeleton.Length;
                    while (nodeOf[current] < 0 && guard-- > 0)
                    {
                        edge.Points.Add(new Vector(cx, cy));
                        edge.Radii.Add(distance[current]);
                        if (degree[current] != 2) break; // malformed: no way forward

                        int next = OtherNeighbour(skeleton, cx, cy, previous, out int stepX, out int stepY);
                        if (next < 0) break;
                        Consume(current, stepX, stepY, next);
                        previous = current;
                        current = next;
                        cx += stepX;
                        cy += stepY;
                    }

                    if (nodeOf[current] < 0) continue; // dead end, not a node: nothing to attach to
                    edge.NodeB = nodeOf[current];
                    edge.Points.Add(Nodes[nodeOf[current]].Position);
                    edge.Radii.Add(distance[current]);
                    edge.Measure();
                    Attach(edge);
                }
            }
        }
    }

    /// <summary>
    /// A component that is a closed curve has no endpoint and no junction, so it has no node
    /// pixels and <see cref="WalkChains"/> never sees it. Pick it up here.
    /// </summary>
    private void WalkIsolatedLoops(bool[] skeleton, byte[] degree, int[] nodeOf, double[] distance)
    {
        for (int start = 0; start < skeleton.Length; start++)
        {
            if (!skeleton[start] || nodeOf[start] >= 0 || degree[start] != 2 || _used[start] != 0) continue;

            int cx = start % _width, cy = start / _width;
            int id = Nodes.Count;
            Nodes.Add(new SkeletonNode { Position = new Vector(cx, cy) });

            var edge = new SkeletonEdge { NodeA = id, NodeB = id };
            edge.Points.Add(Nodes[id].Position);
            edge.Radii.Add(distance[start]);
            int previous = -1, current = start;
            int guard = skeleton.Length;
            while (guard-- > 0)
            {
                int next;
                int stepX, stepY;
                if (previous < 0) next = FirstNeighbour(skeleton, cx, cy, out stepX, out stepY);
                else next = OtherNeighbour(skeleton, cx, cy, previous, out stepX, out stepY);
                if (next < 0 || next == start) break;
                Consume(current, stepX, stepY, next);
                cx += stepX;
                cy += stepY;
                edge.Points.Add(new Vector(cx, cy));
                edge.Radii.Add(distance[next]);
                previous = current;
                current = next;
            }
            edge.Points.Add(Nodes[id].Position);
            edge.Radii.Add(edge.Radii.Count > 0 ? edge.Radii[^1] : 0);
            edge.Measure();
            Attach(edge);
        }
    }

    /// <summary>The ink neighbour of (x, y) that is not <paramref name="previous"/>, with the step taken to reach it.</summary>
    private int OtherNeighbour(bool[] skeleton, int x, int y, int previous, out int dx, out int dy)
    {
        for (int stepY = -1; stepY <= 1; stepY++)
        {
            int ny = y + stepY;
            if (ny < 0 || ny >= _height) continue;
            for (int stepX = -1; stepX <= 1; stepX++)
            {
                if (stepX == 0 && stepY == 0) continue;
                int nx = x + stepX;
                if (nx < 0 || nx >= _width) continue;
                int neighbour = ny * _width + nx;
                if (neighbour != previous && skeleton[neighbour]) { dx = stepX; dy = stepY; return neighbour; }
            }
        }
        dx = 0;
        dy = 0;
        return -1;
    }

    private int FirstNeighbour(bool[] skeleton, int x, int y, out int dx, out int dy)
    {
        for (int stepY = -1; stepY <= 1; stepY++)
        {
            int ny = y + stepY;
            if (ny < 0 || ny >= _height) continue;
            for (int stepX = -1; stepX <= 1; stepX++)
            {
                if (stepX == 0 && stepY == 0) continue;
                int nx = x + stepX;
                if (nx < 0 || nx >= _width) continue;
                int neighbour = ny * _width + nx;
                if (skeleton[neighbour]) { dx = stepX; dy = stepY; return neighbour; }
            }
        }
        dx = 0;
        dy = 0;
        return -1;
    }

    private static byte DirectionBit(int dx, int dy) => (byte)(1 << ((dy + 1) * 3 + (dx + 1)));

    private bool IsUsed(int pixel, int dx, int dy) => (_used[pixel] & DirectionBit(dx, dy)) != 0;

    /// <summary>Mark both directions of a step as walked.</summary>
    private void Consume(int from, int dx, int dy, int to)
    {
        _used[from] |= DirectionBit(dx, dy);
        _used[to] |= DirectionBit(-dx, -dy);
    }

    /// <summary>Recompute every node's live degree and incident-edge list.</summary>
    public void RebuildAdjacency()
    {
        foreach (var node in Nodes) { node.Edges.Clear(); node.Degree = 0; }
        for (int i = 0; i < Edges.Count; i++)
        {
            var edge = Edges[i];
            if (edge.Removed) continue;
            Nodes[edge.NodeA].Edges.Add(i);
            Nodes[edge.NodeA].Degree++;
            if (edge.NodeB != edge.NodeA)
            {
                Nodes[edge.NodeB].Edges.Add(i);
                Nodes[edge.NodeB].Degree++;
            }
        }
    }

    /// <summary>
    /// Incident edges that actually go somewhere, i.e. not removed and not a loop back to this
    /// node; thinning leaves plenty of degenerate loops around junctions, and counting them as
    /// branches makes every degree test in the cleanup wrong.
    /// </summary>
    public int BranchDegree(int node)
    {
        int degree = 0;
        foreach (int i in Nodes[node].Edges)
        {
            var edge = Edges[i];
            if (!edge.Removed && !edge.IsLoop) degree++;
        }
        return degree;
    }

    /// <summary>The live incident edges that are not loops back to this node.</summary>
    public List<int> Branches(int node)
    {
        var branches = new List<int>();
        foreach (int i in Nodes[node].Edges)
        {
            var edge = Edges[i];
            if (!edge.Removed && !edge.IsLoop) branches.Add(i);
        }
        return branches;
    }
}
