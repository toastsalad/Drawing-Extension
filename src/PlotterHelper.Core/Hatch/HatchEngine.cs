// Ported from EggBot's eggbot_hatch.py, (c) Windell H. Oskay and contributors,
// licensed GPL-2.0-or-later; see LICENSE in the repository root.
using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Generates hatch fills for the closed polygons of SVG elements. A faithful, much
/// faster port of evil-mad/axidraw's eggbot_hatch.py, keeping its constants
/// (MinHatchFraction, BezierOvershootMultiplier, the colinear/alternating radian
/// tolerances) and connect-ends Bézier join math.
///
/// This class owns the per-element skeleton only: flatten the element into closed
/// polygons, hand them to the fill style named by <see cref="HatchOptions.FillStyle"/>
/// (one <see cref="IHatchFill"/> or <see cref="IGenerator"/> per style), apply the optional jitter, collect
/// the group. Parallel/Wave generate hatch lines over the element bbox at the requested
/// angle/spacing and clip them to the polygon interior against the ring set itself
/// (<see cref="HatchGeometry"/>/
/// <see cref="EvenOddRegion"/>) — robust on degenerate / self-overlapping subpaths where the original
/// scanline edge-crossing sweep miscounted. Concentric walks equal-distance contours of the
/// interior (<see cref="ContourField"/>); Spiral/Hilbert clip a curve to the polygon, dragon and
/// scribble place their strokes in cells. Per-element work is parallelized.
/// </summary>
public static class HatchEngine
{
    private static readonly Dictionary<FillStyle, IHatchFill> Fills = new()
    {
        [FillStyle.Parallel] = new ParallelFill(),
        [FillStyle.Wave] = new WaveFill(),
        [FillStyle.Concentric] = new ConcentricFill(),
        [FillStyle.Spiral] = new SpiralFill(),
        [FillStyle.Hilbert] = new HilbertFill(),
        [FillStyle.Serpentine] = new SerpentineFill(),
        [FillStyle.Fermat] = new FermatFill(),
        [FillStyle.Moire] = new MoireFill(),
        [FillStyle.Peano] = new PeanoFill(),
        [FillStyle.Engrave] = new EngravingFill(),
        [FillStyle.ContourSpiral] = new ContourSpiralFill(),
        [FillStyle.HexLattice] = new HexLatticeFill(),
        [FillStyle.SquareSpiral] = new SquareSpiralFill(),
        [FillStyle.Truchet] = new TruchetFill(),
        [FillStyle.Gosper] = new GosperFill(),
        [FillStyle.Dragon] = new DragonFill(),
        [FillStyle.Shade] = new ShadeFill(),
    };

    /// <summary>
    /// The styles that grow paths over the region instead of filling it with a clipped pattern. Kept a
    /// separate table because they take a different context: a region to ask about, not polygons to clip.
    /// </summary>
    private static readonly Dictionary<FillStyle, IGenerator> Generators = new()
    {
        [FillStyle.Stipple] = new StippleFill(),
        [FillStyle.Scribble] = new ScribbleFill(),
    };

    /// <summary>Generate hatch fills for a set of SVG elements.</summary>
    public static List<HatchGroup> Generate(IEnumerable<Svg.SvgPath> sources, HatchOptions opts, Action<int, int>? progress = null)
    {
        double flattenTol = opts.Tolerance / 100.0;
        int parallelism = opts.MaxParallelism <= 0 ? Environment.ProcessorCount : opts.MaxParallelism;

        var sourceList = sources as IList<Svg.SvgPath> ?? sources.ToList();
        var results = new HatchGroup[sourceList.Count];
        int done = 0;
        int total = sourceList.Count;

        Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, idx =>
        {
            var src = sourceList[idx];
            var group = new HatchGroup { Source = src };

            var polygons = BuildPolygons(src, flattenTol, opts.AutoClose);
            if (polygons.Count == 0) goto report;

            var bbox = BoundingBox(polygons);
            if (!bbox.IsValid) goto report;

            if (Generators.TryGetValue(opts.FillStyle, out IGenerator? generator))
            {
                double inset = opts.Inset ? Math.Abs(opts.InsetDistance) : 0.0;
                var region = HatchGeometry.BuildHatchRegion(polygons, inset, Math.Max(Math.Abs(opts.HatchSpacing) * 0.5, 0.25));
                if (region is null) goto report;
                generator.Generate(new GeneratorContext(group.Paths, region, bbox, opts,
                    ToneFor(polygons, bbox, opts)));
            }
            else
            {
                Fills[opts.FillStyle].Fill(new HatchFillContext(group.Paths, polygons, bbox, opts, flattenTol,
                    () => ToneFor(polygons, bbox, opts)));
            }

            if (group.Paths.Count == 0) goto report;

            if (opts.Jitter)
                ApplyJitter(group.Paths, opts);

            results[idx] = group;

        report:
            if (progress != null)
            {
                int doneCount = Interlocked.Increment(ref done);
                progress(doneCount, total);
            }
        });

        var groups = new List<HatchGroup>(total);
        foreach (var group in results) if (group != null) groups.Add(group);
        return groups;
    }

    // ---- polygon construction -------------------------------------------------

    /// <summary>Flatten an element's curves into closed subpath polygons (vertex lists).</summary>
    public static List<List<Vector>> BuildPolygons(Svg.SvgPath src, double tol, bool autoClose = false)
    {
        var polygons = new List<List<Vector>>();
        var subpath = new List<Vector>();
        Vector? lastEnd = null;

        foreach (var curve in src.Curves)
        {
            bool broken = lastEnd.HasValue && Vector.DistanceSquared(lastEnd.Value, curve.Start) > 1.0;
            if (broken)
            {
                TryCloseAndAdd(polygons, subpath, autoClose);
                subpath = new List<Vector>();
            }

            if (subpath.Count == 0)
                subpath.Add(curve.Start);
            curve.Flatten(subpath, tol);
            lastEnd = curve.End;
        }
        TryCloseAndAdd(polygons, subpath, autoClose);
        return polygons;
    }

    private static void TryCloseAndAdd(List<List<Vector>> polygons, List<Vector> subpath, bool autoClose)
    {
        if (subpath.Count < 3) return;
        // Closed if first and last vertices coincide (within 1 px²), matching the original.
        if (Vector.DistanceSquared(subpath[0], subpath[^1]) < 1.0)
            polygons.Add(subpath);
        else if (autoClose)
        {
            // Bridge a visually-closed open path by adding its start point as the
            // final vertex, so the fill sees a real polygon.
            subpath.Add(subpath[0]);
            polygons.Add(subpath);
        }
    }

    // ---- bounding box ---------------------------------------------------------

    /// <summary>
    /// The subset of <paramref name="sources"/> that have at least one closed subpath
    /// (so <see cref="Generate"/> would actually hatch them). Cheap: walks curve
    /// endpoints only -- no flattening, no intersection sweep -- so the per-color
    /// path can discover hatchable elements without running a full (potentially
    /// huge) global hatch just to throw it away.
    /// </summary>
    public static List<Svg.SvgPath> HatchableSources(IEnumerable<Svg.SvgPath> sources, bool autoClose = false)
    {
        var result = new List<Svg.SvgPath>();
        foreach (var src in sources)
            if (HasClosedSubpath(src, autoClose)) result.Add(src);
        return result;
    }

    /// <summary>True if the path has any closed subpath (first/last curve endpoints
    /// coincide within 1 unit², matching <see cref="BuildPolygons"/>'s rule).
    /// Uses exact curve endpoints -- no flattening drift. When <paramref name="autoClose"/>
    /// is set, any multi-segment subpath counts (it will be bridged shut).</summary>
    public static bool HasClosedSubpath(Svg.SvgPath src, bool autoClose = false)
    {
        Vector? subStart = null, subEnd = null;
        int subCurves = 0;
        bool AnyOk() => subStart.HasValue && subEnd.HasValue &&
            (Vector.DistanceSquared(subStart.Value, subEnd.Value) < 1.0 || (autoClose && subCurves >= 2));
        foreach (var curve in src.Curves)
        {
            bool broken = subEnd.HasValue && Vector.DistanceSquared(subEnd.Value, curve.Start) > 1.0;
            if (broken)
            {
                if (AnyOk()) return true;
                subStart = curve.Start;
                subCurves = 0;
            }
            subStart ??= curve.Start;
            subEnd = curve.End;
            subCurves++;
        }
        return AnyOk();
    }

    private static HatchBBox BoundingBox(List<List<Vector>> polygons)
    {
        var bbox = new HatchBBox
        {
            Xmin = double.PositiveInfinity,
            Ymin = double.PositiveInfinity,
            Xmax = double.NegativeInfinity,
            Ymax = double.NegativeInfinity
        };
        foreach (var poly in polygons)
            foreach (var point in poly)
            {
                if (point.X < bbox.Xmin) bbox.Xmin = point.X;
                if (point.X > bbox.Xmax) bbox.Xmax = point.X;
                if (point.Y < bbox.Ymin) bbox.Ymin = point.Y;
                if (point.Y > bbox.Ymax) bbox.Ymax = point.Y;
            }
        return bbox;
    }

    // ---- Jitter post-processor (style-agnostic) ----------------------------

    /// <summary>
    /// Apply deterministic Gaussian jitter to every control/end point of every
    /// path, giving any fill style a hand-drawn look. Same seed = same output.
    /// </summary>
    /// <summary>
    /// Darkness over an element: from the supplied image when there is one, otherwise from the shape's own
    /// boundary. The fills that shade ask for it through the context, so only they pay for it.
    /// </summary>
    private static IToneField ToneFor(List<List<Vector>> polygons, HatchBBox bbox, HatchOptions opts)
    {
        if (opts.ToneImage is Trace.GrayImage image) return new ImageToneField(image, bbox, opts.ToneInvert);

        double spacing = Math.Max(0.1, Math.Abs(opts.HatchSpacing));
        double depth = spacing * Math.Max(opts.ToneDepth, 0.1);
        return (IToneField?)BoundaryToneField.Build(polygons, bbox, depth) ?? new FlatToneField(1);
    }

    private static void ApplyJitter(List<HatchPath> paths, HatchOptions opts)
    {
        double amount = Math.Abs(opts.JitterAmount);
        if (amount <= 0) return;
        var rng = new Random(opts.JitterSeed);
        foreach (var path in paths)
        {
            path.Start = JitterPoint(path.Start, rng, amount);
            for (int op = 0; op < path.Ops.Count; op++)
            {
                switch (path.Ops[op])
                {
                    case LineTo line:
                        path.Ops[op] = new LineTo(JitterPoint(line.P, rng, amount));
                        break;
                    case CubicTo cubic:
                        path.Ops[op] = new CubicTo(
                            JitterPoint(cubic.C1, rng, amount),
                            JitterPoint(cubic.C2, rng, amount),
                            JitterPoint(cubic.P, rng, amount));
                        break;
                }
            }
        }
    }

    private static Vector JitterPoint(Vector point, Random rng, double amount)
    {
        double dx = amount * Gauss(rng);
        double dy = amount * Gauss(rng);
        return new Vector(point.X + dx, point.Y + dy);
    }

    /// <summary>Standard-normal sample via Box–Muller.</summary>
    private static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
