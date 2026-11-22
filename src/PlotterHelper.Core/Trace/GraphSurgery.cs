using PlotterHelper.Geometry;

namespace PlotterHelper.Trace;

/// <summary>
/// The stage that turns a fragmented pixel skeleton into whole strokes.
///
/// Thinning alone will not give you "the line, drawn from beginning to end": it leaves whiskers
/// at every corner, forks at every stroke tip, breaks a faint line into dashes wherever the
/// threshold dipped, and explodes each crossing into a knot of micro-edges. This pass prunes
/// spurs, drops tiny loops, extends free ends to the real ink edge, bridges small gaps and
/// splices the surviving two-edge nodes back into one stroke.
///
/// All lengths in <see cref="TraceOptions"/> are in source-image pixels; <c>scale</c> is the
/// trace upscale factor, so every threshold here is in trace pixels and stays proportional to
/// the drawing whatever resolution it was traced at.
/// </summary>
public static class GraphSurgery
{
    /// <summary>How many ink half-widths a branch must reach away from a junction to be a candidate
    /// for splicing through it (see <c>ExtendsAway</c>).</summary>
    private const double BranchReach = 4.0;

    public static void Clean(SkeletonGraph graph, bool[] mask, int width, int height, TraceOptions options, double scale)
    {
        DropTinyLoops(graph, options, scale);
        ContractCloseJunctions(graph, options);
        PruneSpurs(graph, options, scale);
        DropTinyLoops(graph, options, scale);
        MergeThroughJunctions(graph, options);
        CollapseDegreeTwo(graph);
        ExtendEndpoints(graph, mask, width, height, scale);
        BridgeGaps(graph, options, scale);
        CollapseDegreeTwo(graph);
        SnapToNodes(graph);
    }

    /// <summary>
    /// Fuse junction nodes that sit within a couple of ink-widths of each other.
    ///
    /// Where two thin strokes cross at a shallow angle their skeletons do not meet at a point:
    /// they run alongside each other and get joined by a ladder of short rungs, so one crossing
    /// arrives as a chain of degree-4 nodes a few pixels apart. Junction merging pairs branches
    /// per node, and every node in the ladder sees the rung as a legitimate branch, so the
    /// pairing comes out arbitrary until the ladder is fused into a single node.
    /// </summary>
    private static void ContractCloseJunctions(SkeletonGraph graph, TraceOptions options)
    {
        if (options.JunctionMerge <= 0) return;

        // Collect the candidates once: merging never changes an edge's length and never turns a
        // non-junction into a junction, so rescanning after each fusion only made this quadratic.
        var candidates = new List<int>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Removed || edge.IsLoop) continue;
            if (edge.Length > 1e-9)
            {
                if (graph.BranchDegree(edge.NodeA) < 3 || graph.BranchDegree(edge.NodeB) < 3) continue;
                double limit = options.JunctionMerge * Math.Max(edge.RadiusAt(edge.NodeA), edge.RadiusAt(edge.NodeB));
                if (edge.Length > limit) continue;
            }
            candidates.Add(edge.Index);
        }

        foreach (int index in candidates)
        {
            var edge = graph.Edges[index];
            if (edge.Removed || edge.IsLoop) continue;
            // Both ends may already have been fused into one node by an earlier candidate.
            if (edge.NodeA == edge.NodeB) continue;
            Contract(graph, edge);
        }
    }

    /// <summary>Fuse the two ends of <paramref name="bridge"/> into one node and drop the bridge.</summary>
    private static void Contract(SkeletonGraph graph, SkeletonEdge bridge)
    {
        int keep = bridge.NodeA, drop = bridge.NodeB;
        graph.Detach(bridge);
        bridge.Removed = true;

        // Re-point the dropped node's edges and fix up both adjacency lists directly; calling
        // RebuildAdjacency() per fusion would make this pass O(edges) per merge.
        var moved = graph.Nodes[drop].Edges.ToArray();
        foreach (int index in moved)
        {
            var edge = graph.Edges[index];
            if (edge.Removed) continue;
            graph.Nodes[drop].Edges.Remove(index);
            graph.Nodes[drop].Degree--;
            if (edge.NodeA == drop) edge.NodeA = keep;
            if (edge.NodeB == drop) edge.NodeB = keep;
            graph.Nodes[keep].Edges.Add(index);
            graph.Nodes[keep].Degree++;
        }
        graph.Nodes[keep].Position = (graph.Nodes[keep].Position + graph.Nodes[drop].Position) * 0.5;
    }

    /// <summary>
    /// Pull every end that meets other strokes onto its node's position, or splicing leaves a
    /// short hole where the crossing was. Leaf ends are left alone: they were extended out to
    /// the real ink edge a few steps ago and must not be pulled back in.
    /// </summary>
    private static void SnapToNodes(SkeletonGraph graph)
    {
        foreach (var edge in graph.Edges)
        {
            if (edge.Removed) continue;
            if (graph.BranchDegree(edge.NodeA) >= 2) edge.Points[0] = graph.Nodes[edge.NodeA].Position;
            if (!edge.IsLoop && graph.BranchDegree(edge.NodeB) >= 2) edge.Points[^1] = graph.Nodes[edge.NodeB].Position;
            edge.Measure();
        }
    }

    /// <summary>
    /// At a crossing the skeleton meets in one node of degree 4, and the graph has no idea that
    /// the two lines merely pass through each other -- so each line comes out as two half
    /// strokes and the pen lifts in the middle of it. Pair up the branches whose directions
    /// continue each other and splice them, restoring the line a person would say they drew.
    /// </summary>
    private static void MergeThroughJunctions(SkeletonGraph graph, TraceOptions options)
    {
        if (options.JunctionAngle <= 0) return;
        double minContinuity = Math.Cos(options.JunctionAngle * Math.PI / 180.0);

        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int node = 0; node < graph.Nodes.Count; node++)
            {
                if (graph.BranchDegree(node) < 3) continue;

                var incident = graph.Nodes[node].Edges;
                int bestFirst = -1, bestSecond = -1;
                double bestContinuity = minContinuity;

                for (int i = 0; i < incident.Count; i++)
                {
                    var first = graph.Edges[incident[i]];
                    if (first.Removed || first.IsLoop) continue;
                    if (!ExtendsAway(first, node)) continue;
                    Vector intoFirst = InwardTangent(first, node);
                    for (int j = i + 1; j < incident.Count; j++)
                    {
                        var second = graph.Edges[incident[j]];
                        if (second.Removed || second.IsLoop) continue;
                        if (!ExtendsAway(second, node)) continue;
                        // A stroke arriving along first leaves along second when the two outward
                        // directions are opposite.
                        double continuity = -Vector.Dot(intoFirst, InwardTangent(second, node));
                        if (continuity <= bestContinuity) continue;
                        bestContinuity = continuity;
                        bestFirst = incident[i];
                        bestSecond = incident[j];
                    }
                }

                if (bestFirst < 0) continue;
                Merge(graph, node, graph.Edges[bestFirst], graph.Edges[bestSecond]);
                merged = true;
            }
        }
    }

    /// <summary>
    /// Direction a stroke heads in as it leaves <paramref name="node"/> along this edge.
    ///
    /// Sampled a distance away rather than a fixed number of points away, because at a crossing
    /// the skeleton is a blob many pixels across and a tangent measured inside it points anywhere.
    /// Every threshold in this file depends on these directions, so the extra walk pays for itself.
    /// </summary>
    private static Vector InwardTangent(SkeletonEdge edge, int node)
    {
        var points = edge.Points;
        if (points.Count < 2) return Vector.Zero;

        Vector end = node == edge.NodeA ? points[0] : points[^1];
        double reach = Math.Max(3.0, 2.0 * edge.RadiusAt(node));
        Vector inner = end;
        double travelled = 0;
        if (node == edge.NodeA)
        {
            for (int i = 1; i < points.Count; i++)
            {
                travelled += (points[i] - points[i - 1]).Length;
                inner = points[i];
                if (travelled >= reach) break;
            }
        }
        else
        {
            for (int i = points.Count - 2; i >= 0; i--)
            {
                travelled += (points[i + 1] - points[i]).Length;
                inner = points[i];
                if (travelled >= reach) break;
            }
        }

        Vector direction = inner - end;
        return direction.Length < 1e-9 ? Vector.Zero : direction / direction.Length;
    }

    /// <summary>
    /// Remove dead-end branches that are short relative to the ink they hang off and that
    /// narrow toward the tip. The length test catches the fork thinning leaves at a stroke tip
    /// and at every corner; the taper test keeps a deliberate short tick of even width that
    /// happens to touch a line.
    /// </summary>
    private static void PruneSpurs(SkeletonGraph graph, TraceOptions options, double scale)
    {
        double absolute = options.MinSpur * scale;
        // A single pass is enough: removing a spur can only lower its junction's branch count,
        // and a branch of a node with two or fewer branches is not a spur.
        for (int i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            if (edge.Removed) continue;

            int junction, leaf;
            if (graph.BranchDegree(edge.NodeA) > 2 && graph.BranchDegree(edge.NodeB) == 1) { junction = edge.NodeA; leaf = edge.NodeB; }
            else if (graph.BranchDegree(edge.NodeB) > 2 && graph.BranchDegree(edge.NodeA) == 1) { junction = edge.NodeB; leaf = edge.NodeA; }
            else continue;

            double limit = Math.Max(absolute, options.SpurRatio * edge.RadiusAt(junction));
            if (edge.Length >= limit) continue;
            if (edge.RadiusAt(leaf) >= options.SpurTaper * edge.RadiusAt(junction)) continue;

            graph.Detach(edge);
            edge.Removed = true;
        }
    }

    /// <summary>
    /// Drop loops whose perimeter is no larger than the stroke that encloses them: those are
    /// thinning artifacts around a pinhole in the ink, while a genuinely drawn circle is far bigger.
    /// </summary>
    private static void DropTinyLoops(SkeletonGraph graph, TraceOptions options, double scale)
    {
        foreach (var edge in graph.Edges)
        {
            if (edge.Removed || !edge.IsLoop) continue;
            double limit = Math.Max(8.0 * edge.Radius, options.MinSpur * scale);
            if (edge.Length >= limit) continue;
            graph.Detach(edge);
            edge.Removed = true;
        }
    }

    /// <summary>
    /// The skeleton of a stroke stops roughly one half-width short of a flat end, where the
    /// medial axis of the two sides meets. Walk each free end along its own direction while the
    /// mask is still ink, so the drawn stroke reaches the tip.
    /// </summary>
    private static void ExtendEndpoints(SkeletonGraph graph, bool[] mask, int width, int height, double scale)
    {
        foreach (var edge in graph.Edges)
        {
            if (edge.Removed || edge.IsLoop) continue;

            Extend(graph, edge, edge.NodeA, mask, width, height, scale);
            Extend(graph, edge, edge.NodeB, mask, width, height, scale);
        }
    }

    private static void Extend(SkeletonGraph graph, SkeletonEdge edge, int node, bool[] mask, int width, int height, double scale)
    {
        if (graph.BranchDegree(node) != 1) return;

        Vector tip = node == edge.NodeA ? edge.Points[0] : edge.Points[^1];
        Vector direction = OutwardTangent(edge, node);
        if (direction == Vector.Zero) return;

        double reach = 2.5 * edge.RadiusAt(node) + scale;
        Vector furthest = tip;
        const double step = 0.5;
        for (double travelled = step; travelled <= reach; travelled += step)
        {
            Vector probe = tip + direction * travelled;
            int px = (int)Math.Round(probe.X), py = (int)Math.Round(probe.Y);
            if (px < 0 || py < 0 || px >= width || py >= height) break;
            if (!mask[py * width + px]) break;
            furthest = probe;
        }
        if (furthest == tip) return;

        if (node == edge.NodeA) edge.Points[0] = furthest;
        else edge.Points[^1] = furthest;
        edge.Measure();
    }

    /// <summary>
    /// Join free ends that face each other across a small gap: the step that makes a faint line
    /// come out as one stroke instead of a row of dashes when a threshold dip, a JPEG block
    /// boundary or a light patch of paper breaks the skeleton. Candidates are scored by gap
    /// length and how well both ends aim at each other, then accepted greedily.
    /// </summary>
    private static void BridgeGaps(SkeletonGraph graph, TraceOptions options, double scale)
    {
        if (options.JoinGap <= 0) return;

        var leaves = CollectLeaves(graph);
        if (leaves.Count < 2) return;

        double maxGap = options.JoinGap * scale;
        double minAim = Math.Cos(options.JoinAngle * Math.PI / 180.0);

        var grid = new Dictionary<long, List<int>>();
        for (int i = 0; i < leaves.Count; i++)
        {
            long key = CellKey(leaves[i].Position, maxGap);
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = new List<int>();
            bucket.Add(i);
        }

        var candidates = new List<Candidate>();
        for (int i = 0; i < leaves.Count; i++)
        {
            var first = leaves[i];
            int cx = (int)Math.Floor(first.Position.X / maxGap), cy = (int)Math.Floor(first.Position.Y / maxGap);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue(PackCell(cx + dx, cy + dy), out var bucket)) continue;
                    foreach (int j in bucket)
                    {
                        if (j <= i) continue;
                        var second = leaves[j];
                        if (second.Node == first.Node || second.Edge == first.Edge) continue;

                        double gap = (second.Position - first.Position).Length;
                        if (gap > maxGap || gap < 1e-9) continue;
                        Vector towards = (second.Position - first.Position) / gap;
                        if (Vector.Dot(first.Outward, towards) < minAim) continue;
                        if (Vector.Dot(second.Outward, -towards) < minAim) continue;

                        double aim = Vector.Dot(first.Outward, towards);
                        candidates.Add(new Candidate(gap + (1 - aim) * maxGap, i, j));
                    }
                }
            }
        }

        candidates.Sort(static (x, y) => x.Score.CompareTo(y.Score));

        var used = new bool[leaves.Count];
        foreach (var candidate in candidates)
        {
            if (used[candidate.A] || used[candidate.B]) continue;
            used[candidate.A] = true;
            used[candidate.B] = true;

            var first = leaves[candidate.A];
            var second = leaves[candidate.B];
            var bridge = new SkeletonEdge { NodeA = first.Node, NodeB = second.Node };
            bridge.Points.Add(first.Position);
            bridge.Points.Add(second.Position);
            bridge.Radii.Add(first.Radius);
            bridge.Radii.Add(second.Radius);
            bridge.Measure();
            graph.Attach(bridge);
        }
    }

    /// <summary>
    /// A node left with exactly two edges is a junction that no longer means anything (its other
    /// branches were pruned) or a pair of ends that just got bridged. Splicing the two edges
    /// through it is what actually produces "one line from the beginning to the end".
    ///
    /// Done by walking each run of degree-two nodes once and emitting a single spliced edge;
    /// merging one pair at a time re-copies the accumulated point list on every step.
    /// </summary>
    private static void CollapseDegreeTwo(SkeletonGraph graph)
    {
        // A set rather than an array sized up front: this pass appends the edges it splices, and a
        // merged edge can legally be reached by a later walk (it is then a single-edge chain and is
        // skipped).
        var consumed = new HashSet<int>();
        for (int start = 0; start < graph.Edges.Count; start++)
        {
            var first = graph.Edges[start];
            if (first.Removed || first.IsLoop || consumed.Contains(start)) continue;

            // Begin only at an end of the run; starting inside it would stop after one edge.
            int entry;
            if (graph.BranchDegree(first.NodeA) != 2) entry = first.NodeA;
            else if (graph.BranchDegree(first.NodeB) != 2) entry = first.NodeB;
            else continue;

            var chain = new List<(int Edge, int Entry)>();
            int index = start;
            int endNode = entry;
            while (true)
            {
                chain.Add((index, entry));
                consumed.Add(index);
                int far = graph.Edges[index].OtherEnd(entry);
                if (graph.BranchDegree(far) != 2) { endNode = far; break; }

                int nextIndex = -1;
                foreach (int candidate in graph.Nodes[far].Edges)
                {
                    var other = graph.Edges[candidate];
                    if (other.Removed || other.IsLoop || candidate == index || consumed.Contains(candidate)) continue;
                    nextIndex = candidate;
                    break;
                }
                if (nextIndex < 0) { endNode = far; break; }
                index = nextIndex;
                entry = far;
            }

            if (chain.Count == 1) continue;   // a single edge through nothing: leave it alone

            foreach (var (member, _) in chain)
            {
                graph.Detach(graph.Edges[member]);
                graph.Edges[member].Removed = true;
            }

            var merged = new SkeletonEdge { NodeA = chain[0].Entry, NodeB = endNode };
            foreach (var (member, at) in chain) AppendSpliced(merged, graph.Edges[member], at);
            merged.Measure();
            graph.Attach(merged);
        }
    }

    /// <summary>Append an edge's points, oriented so the run starts at <paramref name="entry"/>.</summary>
    private static void AppendSpliced(SkeletonEdge target, SkeletonEdge edge, int entry)
    {
        bool forward = edge.NodeA == entry;
        int count = edge.Points.Count;
        for (int step = 0; step < count; step++)
        {
            int i = forward ? step : count - 1 - step;
            // Consecutive edges share their join point, which is whichever end was appended first.
            if (step == 0 && target.Points.Count > 0 && (target.Points[^1] - edge.Points[i]).Length < 1e-9) continue;
            target.Points.Add(edge.Points[i]);
            target.Radii.Add(edge.Radii[i]);
        }
    }

    /// <summary>Splice <paramref name="first"/> and <paramref name="second"/> into one edge passing through <paramref name="node"/>.</summary>
    private static void Merge(SkeletonGraph graph, int node, SkeletonEdge first, SkeletonEdge second)
    {
        int farFirst = first.OtherEnd(node);
        int farSecond = second.OtherEnd(node);

        // Detach before reversing: Reverse swaps NodeA/NodeB, and Detach needs the ends the nodes
        // still know about.
        graph.Detach(first);
        graph.Detach(second);
        first.Removed = true;
        second.Removed = true;
        if (first.NodeB != node) first.Reverse();
        if (second.NodeA != node) second.Reverse();

        var result = new SkeletonEdge { NodeA = farFirst, NodeB = farSecond };
        foreach (var point in first.Points) result.Points.Add(point);
        foreach (var radius in first.Radii) result.Radii.Add(radius);
        for (int i = 0; i < second.Points.Count; i++)
        {
            // Both ends sit on the same node, so the join point appears twice; it is not always
            // bit-identical now that ends can be extended out to the ink edge.
            if (i == 0 && result.Points.Count > 0 && (result.Points[^1] - second.Points[0]).Length < 1e-9) continue;
            result.Points.Add(second.Points[i]);
            result.Radii.Add(second.Radii[i]);
        }
        result.Measure();
        graph.Attach(result);
    }

    private readonly record struct Leaf(int Edge, int Node, Vector Position, Vector Outward, double Radius);

    private readonly record struct Candidate(double Score, int A, int B);

    private static List<Leaf> CollectLeaves(SkeletonGraph graph)
    {
        var leaves = new List<Leaf>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Removed || edge.IsLoop) continue;
            if (graph.BranchDegree(edge.NodeA) == 1)
                leaves.Add(new Leaf(edge.Index, edge.NodeA, edge.Points[0], OutwardTangent(edge, edge.NodeA), edge.RadiusAtA));
            if (graph.BranchDegree(edge.NodeB) == 1)
                leaves.Add(new Leaf(edge.Index, edge.NodeB, edge.Points[^1], OutwardTangent(edge, edge.NodeB), edge.RadiusAtB));
        }
        return leaves;
    }

    /// <summary>
    /// Whether a branch runs far enough away from a junction to count as a continuation of a
    /// stroke through it. The rungs of a shallow-crossing ladder are only a few ink-widths long,
    /// and splicing two of those produces a stroke with a kink in it.
    /// </summary>
    private static bool ExtendsAway(SkeletonEdge edge, int node) =>
        edge.Length >= BranchReach * edge.RadiusAt(node);

    /// <summary>Direction a stroke is travelling in as it reaches <paramref name="node"/>, pointing outward past the end.</summary>
    private static Vector OutwardTangent(SkeletonEdge edge, int node) => -InwardTangent(edge, node);

    private static long CellKey(Vector position, double cell) =>
        PackCell((int)Math.Floor(position.X / cell), (int)Math.Floor(position.Y / cell));

    private static long PackCell(int cellX, int cellY) => ((long)cellX << 32) ^ (uint)cellY;
}
