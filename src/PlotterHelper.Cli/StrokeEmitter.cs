using PlotterHelper.Compiler;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using PlotterHelper.Cli.Features;
using PlotterHelper.Cli.Pens;

namespace PlotterHelper.Cli;

/// <summary>
/// Stroke emission: flattens outline curves and hatch polylines to chains and emits
/// them as G-code, honoring color grouping (M0 pen-swap pauses), travel optimization
/// (greedy nearest-neighbor, optional stroke reversal), inner-before-outer holes
/// ordering, and connect-outlines (pen-down connectors through the solid area; the
/// solid-area helpers live in <see cref="SolidGeometry"/>).
/// </summary>
internal static class StrokeEmitter
{
    /// <summary>Flatten+emit curves one by one with progress (replaces Compiler.AppendCurves for progress reporting).</summary>
    private static void CompileWithProgress(Options opts, GcodeCompiler compiler, IEnumerable<Curve> curves,
        int startPct, int endPct, string label)
    {
        var list = curves as IList<Curve> ?? curves.ToList();
        int count = list.Count;
        if (count == 0) return;
        double tolerance = opts.FlatteningTolerance;
        for (int curveIndex = 0; curveIndex < count; curveIndex++)
        {
            var chain = new LineSegmentChain();
            chain.AppendFlattened(list[curveIndex], tolerance);
            compiler.AppendLineChain(chain);
            if (opts.Progress && (curveIndex % 50 == 0 || curveIndex == count - 1))
                Progress.Report(opts, startPct + (endPct - startPct) * (curveIndex + 1) / count, $"{label} {curveIndex + 1}/{count}");
        }
    }

    /// <summary>
    /// Emit outline paths as G-code, with optional color grouping and travel optimization.
    /// </summary>
    /// <remarks>
    /// What happens is decided entirely by <see cref="EmissionPlan.From"/>; this method only
    /// walks the plan:
    /// <list type="bullet">
    /// <item><see cref="StrokeOrder.TravelOptimized"/> — strokes are reordered by a greedy
    /// nearest-neighbor pass (<see cref="TravelOptimizer"/>). Takes precedence over holes-first.</item>
    /// <item><see cref="PenGrouping.PerColor"/> — paths are emitted one color group at a time,
    /// with an M0 pause between groups for manual pen swaps. The pen lifts naturally between
    /// groups because each group starts far from the previous group’s end.</item>
    /// <item>Otherwise — flatten+emit every curve, holes-first if the plan says so.</item>
    /// </list>
    /// </remarks>
    internal static void CompileGroupedByColor(Options opts, GcodeCompiler compiler, IEnumerable<SvgPath> paths,
        int startPercent, int endPct, string label)
    {
        var pathList = paths as IList<SvgPath> ?? paths.ToList();
        if (pathList.Count == 0) return;
        double tolerance = opts.FlatteningTolerance;
        var plan = EmissionPlan.From(opts);

        // Travel optimization is its own emission path, and it overrides holes-first.
        if (plan.TravelOptimized)
        {
            CompileTravelOptimized(opts, compiler, pathList, startPercent, endPct, label);
            return;
        }

        // One pen: flatten+emit every curve in source order (holes-first if asked).
        if (!plan.PerColor)
        {
            var ordered = plan.HolesFirst ? InnerBeforeOuter.Reorder(pathList, tolerance) : pathList;
            CompileWithProgress(opts, compiler, ordered.SelectMany(p => p.Curves), startPercent, endPct, label);
            return;
        }

        // Grouped by color: emit each color group, pausing for a manual pen swap between
        // groups. The pen lifts naturally because the next group starts far from the last.
        var groups = ColorSort.GroupByColor(pathList);
        if (groups.Count == 0) return;

        PenAnnounce.AppendFirstPenAnnounce(opts, compiler, ColorSort.NormalizeColor(groups[0][0].Stroke), groups.Count);
        int totalCurves = pathList.Sum(p => p.Curves.Count);
        int done = 0;

        for (int selectedGroup = 0; selectedGroup < groups.Count; selectedGroup++)
        {
            var group = plan.HolesFirst ? InnerBeforeOuter.Reorder(groups[selectedGroup], tolerance) : groups[selectedGroup];
            compiler.AppendRaw($"; --- color: {ColorSort.NormalizeColor(group[0].Stroke)} ---");

            foreach (var curve in group.SelectMany(p => p.Curves))
            {
                var chain = new LineSegmentChain();
                chain.AppendFlattened(curve, tolerance);
                compiler.AppendLineChain(chain);
                if (opts.Progress && (++done % 50 == 0 || done == totalCurves))
                    Progress.Report(opts, startPercent + (endPct - startPercent) * done / Math.Max(1, totalCurves),
                        $"{label} {done}/{totalCurves}");
            }

            if (opts.PauseBetweenColors && selectedGroup < groups.Count - 1)
                PenAnnounce.AppendPenSwap(opts, compiler, ColorSort.NormalizeColor(groups[selectedGroup + 1][0].Stroke), selectedGroup + 2, groups.Count);
        }
    }

    /// <summary>
    /// Travel-optimized outline emission: flatten each curve to a chain, then greedily
    /// reorder (and optionally reverse) the chains so each stroke starts near the
    /// previous stroke’s end. NN runs within each color group (preserving the
    /// color-group order and the M0 pen-swap pauses between them). The tool position
    /// carries over between groups so the first stroke of a later group is chosen near
    /// the previous group’s end.
    /// </summary>
    private static void CompileTravelOptimized(Options opts, GcodeCompiler compiler, IList<SvgPath> pathList,
        int startPct, int endPct, string label)
    {
        var plan = EmissionPlan.From(opts);
        double tolerance = opts.FlatteningTolerance;
        List<List<SvgPath>> groups = plan.PerColor
            ? ColorSort.GroupByColor(pathList)
            : new List<List<SvgPath>> { pathList.ToList() };

        if (groups.Count > 0)
            PenAnnounce.AppendFirstPenAnnounce(opts, compiler, ColorSort.NormalizeColor(groups[0][0].Stroke), groups.Count);

        int totalCurves = pathList.Sum(path => path.Curves.Count);
        int done = 0;
        Vector cur = Vector.Zero; // assume machine home; carries across groups
        bool allowReverse = opts.OptimizeTravelReverse;

        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            if (plan.PerColor)
                compiler.AppendRaw($"; --- color: {ColorSort.NormalizeColor(group[0].Stroke)} (travel-optimized) ---");
            else
                compiler.AppendRaw("; --- travel-optimized ---");

            // Flatten every curve in the group to its own chain (one stroke each).
            var chains = new List<LineSegmentChain>();
            foreach (var path in group)
                foreach (var curve in path.Curves)
                {
                    var chain = new LineSegmentChain();
                    chain.AppendFlattened(curve, tolerance);
                    if (chain.Count >= 2) chains.Add(chain);
                }

            var ordered = TravelOptimizer.Optimize(chains, cur, allowReverse);
            cur = EmitOrderedChains(opts, compiler, ordered, group, cur,
                ref done, totalCurves, startPct, endPct, label, tolerance);

            if (opts.PauseBetweenColors && groupIndex < groups.Count - 1)
                PenAnnounce.AppendPenSwap(opts, compiler, ColorSort.NormalizeColor(groups[groupIndex + 1][0].Stroke), groupIndex + 2, groups.Count);
        }
    }

    /// <summary>
    /// Emit outlines + hatch fill merged by pen color, for multi-color pen plotting.
    /// Each outline is colored by its source element's stroke; each hatch fill is
    /// colored by its source element's fill. All work of one color is drawn
    /// consecutively (outlines first, then hatches) so a red outline and a red
    /// hatch run on the same pen with no swap; an optional M0 pause separates
    /// different colors. Colors appear in first-appearance (document) order.
    /// </summary>
    /// <remarks>
    /// Within each color group, outline emission honors <see cref="Options.InnerBeforeOuter"/>
    /// (holes-first) and <see cref="Options.OptimizeTravel"/> (greedy nearest-neighbor,
    /// carrying the tool position across groups); hatch fill keeps its own
    /// <see cref="PenLiftOptimizer"/> order. When <see cref="Options.IncludeOutlines"/> is
    /// false, only the (fill-colored) hatches are emitted, still grouped/paused.
    /// </remarks>
    internal static void CompileHatchGroupedByColor(Options opts, GcodeCompiler compiler,
        IList<SvgPath> paths, IList<HatchGroup> hatches,
        int startPct, int endPct, string label)
    {
        // Map each source element to the hatch group it produced (reference equality).
        var hatchBySource = new Dictionary<SvgPath, HatchGroup>(ReferenceEqualityComparer.Instance);
        foreach (var hatch in hatches)
            if (hatch.Source != null) hatchBySource[hatch.Source] = hatch;

        // Walk the document in order: for each element, its outline (stroke color)
        // then its hatch (fill color). This fixes the first-appearance order of each
        // color and the within-group order (outlines before hatches of that color).
        var order = new List<string>();
        var outlineBuckets = new Dictionary<string, List<SvgPath>>(StringComparer.OrdinalIgnoreCase);
        var hatchBuckets = new Dictionary<string, List<HatchGroup>>(StringComparer.OrdinalIgnoreCase);

        bool FirstSighting(string key) => !outlineBuckets.ContainsKey(key) && !hatchBuckets.ContainsKey(key);

        static void Add<T>(Dictionary<string, List<T>> buckets, string key, T item)
        {
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<T>();
            list.Add(item);
        }

        foreach (var path in paths)
        {
            if (opts.IncludeOutlines && path.Curves.Count > 0)
            {
                var key = ColorSort.NormalizeColor(path.Stroke);
                if (FirstSighting(key)) order.Add(key);
                Add(outlineBuckets, key, path);
            }
            if (hatchBySource.TryGetValue(path, out var hatchGroup))
            {
                var key = ColorSort.NormalizeColor(hatchGroup.Source.Fill);
                if (FirstSighting(key)) order.Add(key);
                Add(hatchBuckets, key, hatchGroup);
            }
        }

        if (order.Count == 0) return;

        // Declare the first pen's color at the very start of the multicolor run.
        PenAnnounce.AppendFirstPenAnnounce(opts, compiler, order[0], order.Count);

        int outlineCurves = outlineBuckets.Values.Sum(b => b.Sum(p => p.Curves.Count));
        int hatchCurves = hatchBuckets.Values.Sum(b => b.Sum(h => HatchFlattener.ToCurves(new[] { h }).Count));
        int totalCurves = outlineCurves + hatchCurves;
        int done = 0;
        double tolerance = opts.FlatteningTolerance;
        Vector cur = Vector.Zero; // tool position carried across color groups

        for (int groupIndex = 0; groupIndex < order.Count; groupIndex++)
        {
            var key = order[groupIndex];
            compiler.AppendRaw($"; --- color: {key} ---");

            outlineBuckets.TryGetValue(key, out List<SvgPath>? outlines);
            hatchBuckets.TryGetValue(key, out List<HatchGroup>? hatchGroups);
            cur = EmitColorGroup(opts, compiler, outlines, hatchGroups, cur,
                ref done, totalCurves, startPct, endPct, label, tolerance);

            if (opts.PauseBetweenColors && groupIndex < order.Count - 1)
                PenAnnounce.AppendPenSwap(opts, compiler, order[groupIndex + 1], groupIndex + 2, order.Count);
        }
    }

    /// <summary>
    /// Emit one color group's outline paths under the group's <see cref="EmissionPlan"/>.
    /// Returns the tool position after the last emitted stroke so the next color group can seed
    /// its travel optimizer / continue from here.
    /// </summary>
    private static Vector EmitOutlines(Options opts, GcodeCompiler compiler, List<SvgPath> outlines, Vector cur,
        ref int done, int totalCurves, int startPct, int endPct, string label, double tolerance)
    {
        var plan = EmissionPlan.From(opts);
        if (plan.TravelOptimized)
        {
            var ordered = TravelOptimizer.Optimize(BuildOutlineChains(outlines, tolerance), cur, opts.OptimizeTravelReverse);
            return EmitOrderedChains(opts, compiler, ordered, outlines, cur,
                ref done, totalCurves, startPct, endPct, label, tolerance);
        }

        var orderedPaths = plan.HolesFirst ? InnerBeforeOuter.Reorder(outlines, tolerance) : outlines;
        foreach (var path in orderedPaths)
            foreach (var curve in path.Curves)
                cur = EmitCurve(opts, compiler, curve, cur, ref done, totalCurves, startPct, endPct, label, tolerance);
        return cur;
    }

    /// <summary>
    /// Emit one color group's outlines + hatch fill into the given compiler, honoring
    /// <see cref="Options.OptimizeTravel"/> (pool all strokes of the color and
    /// nearest-neighbor order them) or else outlines-first then per-element hatches.
    /// Returns the tool position after the last emitted stroke. Used by both
    /// <see cref="CompileHatchGroupedByColor"/> (one shared compiler) and the
    /// per-color path (a compiler per pen).
    /// </summary>
    internal static Vector EmitColorGroup(Options opts, GcodeCompiler compiler,
        List<SvgPath>? outlines, IList<HatchGroup>? hatches, Vector cur,
        ref int done, int totalCurves, int startPct, int endPct, string label, double tolerance)
    {
        if (EmissionPlan.From(opts).TravelOptimized)
        {
            var pool = new List<LineSegmentChain>();
            var solidPaths = new List<SvgPath>();
            var seen = new HashSet<SvgPath>(ReferenceEqualityComparer.Instance);
            if (outlines != null)
            {
                foreach (var path in outlines) if (seen.Add(path)) solidPaths.Add(path);
                pool.AddRange(BuildOutlineChains(outlines, tolerance));
            }
            if (hatches != null)
            {
                foreach (var group in hatches) if (group.Source != null && seen.Add(group.Source)) solidPaths.Add(group.Source);
                pool.AddRange(BuildHatchChains(hatches, tolerance));
            }
            var ordered = TravelOptimizer.Optimize(pool, cur, opts.OptimizeTravelReverse);
            return EmitOrderedChains(opts, compiler, ordered, solidPaths, cur,
                ref done, totalCurves, startPct, endPct, label, tolerance);
        }

        if (outlines != null && outlines.Count > 0)
            cur = EmitOutlines(opts, compiler, outlines, cur, ref done, totalCurves, startPct, endPct, label, tolerance);
        if (hatches != null)
            foreach (var group in hatches)
                foreach (var curve in HatchFlattener.ToCurves(new[] { group }))
                    cur = EmitCurve(opts, compiler, curve, cur, ref done, totalCurves, startPct, endPct, label, tolerance);
        return cur;
    }

    /// <summary>
    /// Flatten one curve into a single stroke, emit it, advance the tool position to the
    /// stroke's end and the cumulative progress counter by one. Returns the tool position.
    /// </summary>
    private static Vector EmitCurve(Options opts, GcodeCompiler compiler, Curve curve, Vector cur,
        ref int done, int totalCurves, int startPct, int endPct, string label, double tolerance)
    {
        var chain = new LineSegmentChain();
        chain.AppendFlattened(curve, tolerance);
        compiler.AppendLineChain(chain);
        if (chain.Count >= 2) cur = chain[^1];
        done++;
        if (opts.Progress && (done % 50 == 0 || done == totalCurves))
            Progress.Report(opts, startPct + (endPct - startPct) * done / Math.Max(1, totalCurves),
                $"{label} {done}/{totalCurves}");
        return cur;
    }

    /// <summary>
    /// Emit the travel-optimized outline chains for one color group, optionally joining
    /// consecutive strokes whose end-to-start gap is within
    /// <see cref="Options.ConnectOutlinesDist"/> and whose straight connector lies
    /// inside the group's solid (closed-outline) area — the pen stays down through the
    /// fill instead of lifting. Returns the tool position after the last stroke.
    /// </summary>
    internal static Vector EmitOrderedChains(Options opts, GcodeCompiler compiler,
        List<LineSegmentChain> ordered, IList<SvgPath> groupPaths, Vector cur,
        ref int done, int totalCurves, int startPct, int endPct, string label, double tolerance)
    {
        void Report(int n)
        {
            if (opts.Progress && (n % 50 == 0 || n == totalCurves))
                Progress.Report(opts, startPct + (endPct - startPct) * n / Math.Max(1, totalCurves),
                    $"{label} {n}/{totalCurves}");
        }

        // Solid-area index built once per group (see SolidRegion.cs). Only built when
        // connecting is on and there is more than one chain to potentially join.
        SolidRegion? region = (opts.ConnectOutlines && ordered.Count > 1)
            ? new SolidRegion(SolidGeometry.BuildSolidPolygons(groupPaths, tolerance))
            : null;

        // No solid region to gate connectors through: emit each chain as its own stroke.
        if (region == null || region.IsEmpty)
        {
            foreach (var chain in ordered)
            {
                compiler.AppendLineChain(chain);
                Report(++done);
            }
            if (ordered.Count > 0) cur = ordered[^1][^1];
            return cur;
        }

        // With a solid region: accumulate consecutive chains into one pen-down stroke,
        // joining them when the end-to-start gap is within range AND lies inside the
        // solid area; otherwise flush the accumulator and lift for a travel move.
        double distSq = Units.ToDocUnits(opts.ConnectOutlinesDist, "mm", opts.Unit);
        distSq *= distSq;

        var acc = new LineSegmentChain();
        AppendAll(acc, ordered[0]);
        Report(++done);

        for (int i = 1; i < ordered.Count; i++)
        {
            var chain = ordered[i];
            Vector tail = acc[^1];
            Vector nextStart = chain[0];
            double gapSq = Vector.DistanceSquared(tail, nextStart);

            if (gapSq <= 1e-12)
                AppendSkipFirst(acc, chain);                    // already touching
            else if (gapSq <= distSq && region.SegmentInside(tail, nextStart))
            {
                acc.Append(nextStart);                          // pen-down connector through the fill
                AppendSkipFirst(acc, chain);
            }
            else
            {
                compiler.AppendLineChain(acc);                  // flush; lift + travel to next
                acc = new LineSegmentChain();
                AppendAll(acc, chain);
            }
            Report(++done);
        }

        compiler.AppendLineChain(acc);
        if (acc.Count >= 2) cur = acc[^1];
        return cur;
    }

    /// <summary>Append every vertex of <paramref name="src"/> to <paramref name="dst"/>.</summary>
    private static void AppendAll(LineSegmentChain dst, LineSegmentChain src)
    {
        foreach (var point in src.Points) dst.Append(point);
    }

    /// <summary>Append <paramref name="src"/>'s vertices except the first (which is
    /// already the connector endpoint or coincides with the chain tail).</summary>
    private static void AppendSkipFirst(LineSegmentChain dst, LineSegmentChain src)
    {
        for (int vertex = 1; vertex < src.Count; vertex++) dst.Append(src[vertex]);
    }

    /// <summary>Flatten each outline curve to its own chain (one stroke each); degenerate &lt;2-point chains dropped.</summary>
    internal static List<LineSegmentChain> BuildOutlineChains(IList<SvgPath> outlines, double tolerance)
    {
        var chains = new List<LineSegmentChain>();
        foreach (var path in outlines)
            foreach (var curve in path.Curves)
            {
                var chain = new LineSegmentChain();
                chain.AppendFlattened(curve, tolerance);
                if (chain.Count >= 2) chains.Add(chain);
            }
        return chains;
    }

    /// <summary>Flatten each hatch group’s paths to chains (one stroke per hatch polyline); degenerate &lt;2-point chains dropped.</summary>
    internal static List<LineSegmentChain> BuildHatchChains(IList<HatchGroup> hatchGroups, double tolerance)
    {
        var chains = new List<LineSegmentChain>();
        foreach (var group in hatchGroups)
            foreach (var curve in HatchFlattener.ToCurves(new[] { group }))
            {
                var chain = new LineSegmentChain();
                chain.AppendFlattened(curve, tolerance);
                if (chain.Count >= 2) chains.Add(chain);
            }
        return chains;
    }

    /// <summary>
    /// Non-grouped hatch mode. With --optimize-travel, pool ALL outline + hatch strokes
    /// as one nearest-neighbor set so they join by nearest ends (instead of
    /// outlines-first then per-element hatches). Otherwise emit outlines (honoring
    /// --inner-before-outer) then the hatch fill in generation order (legacy).
    /// </summary>
    internal static void CompilePooledOutlinesAndHatches(Options opts, GcodeCompiler compiler,
        IList<SvgPath> paths, IList<HatchGroup> groups, int startPct, int endPct, string label)
    {
        if (!EmissionPlan.From(opts).TravelOptimized)
        {
            int mid = (startPct + endPct) / 2;
            if (opts.IncludeOutlines)
                CompileGroupedByColor(opts, compiler, paths, startPct, mid, "Outlines");
            CompileWithProgress(opts, compiler, HatchFlattener.ToCurves(groups), mid, endPct, "Hatches");
            return;
        }

        var pool = new List<LineSegmentChain>();
        var solidPaths = new List<SvgPath>();
        var seen = new HashSet<SvgPath>(ReferenceEqualityComparer.Instance);
        double tolerance = opts.FlatteningTolerance;
        if (opts.IncludeOutlines)
        {
            foreach (var path in paths)
                if (seen.Add(path)) solidPaths.Add(path);
            pool.AddRange(BuildOutlineChains(paths, tolerance));
        }
        foreach (var group in groups)
            if (group.Source != null && seen.Add(group.Source)) solidPaths.Add(group.Source);
        pool.AddRange(BuildHatchChains(groups, tolerance));

        int total = pool.Count;
        int done = 0;
        var ordered = TravelOptimizer.Optimize(pool, Vector.Zero, opts.OptimizeTravelReverse);
        EmitOrderedChains(opts, compiler, ordered, solidPaths, Vector.Zero,
            ref done, total, startPct, endPct, label, tolerance);
    }
}