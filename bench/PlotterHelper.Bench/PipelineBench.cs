using BenchmarkDotNet.Attributes;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using PlotterHelper.Cli;            // EngineBuilder/StrokeEmitter/etc., TravelOptimizer (InternalsVisibleTo)
using PlotterHelper.Cli.Features;   // SolidRegion
using Mode = PlotterHelper.Cli.Mode;
using Options = PlotterHelper.Cli.Options;

namespace PlotterHelper.Bench;

/// <summary>
/// BenchmarkDotNet benchmarks for the PlotterHelper pipeline, broken into the same
/// stages the CLI runs. Each benchmark calls the real (internal) production method so
/// optimisations are measured against the code that ships, not a copy.
/// </summary>
[MemoryDiagnoser]
public class PipelineBench
{
    /// <summary>Set by --svg to restrict the [ParamsSource] set to one file.</summary>
    public static string? SvgOverride { get; set; }

    /// <summary>SVGs to benchmark: every *.svg in assets/, filtered by --svg.</summary>
    public IEnumerable<string> SvgFiles()
    {
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "assets");
        var files = Directory.GetFiles(assetsDir, "*.svg").OrderBy(f => f).ToArray();
        if (string.IsNullOrEmpty(SvgOverride)) return files;

        // Bare name -> resolve against assets/; absolute path -> use as-is.
        var o = Path.IsPathRooted(SvgOverride) ? SvgOverride
                : Path.GetFullPath(Path.Combine(assetsDir, SvgOverride));
        var match = files.FirstOrDefault(f => string.Equals(f, o, StringComparison.OrdinalIgnoreCase));
        return match is not null || File.Exists(o) ? new[] { match ?? o } : files;
    }

    [ParamsSource(nameof(SvgFiles))]
    public string Svg { get; set; } = "";

    // ---- precomputed state (rebuilt per [Params] value in GlobalSetup) ----
    private Options _base = null!;          // hatch + optimize-travel + connect-outlines defaults
    private Options _cutOpts = null!;      // cut-mode defaults
    private SvgDocument _doc = null!;
    private HatchOptions _hatchOpts = null!;
    private List<HatchGroup> _groups = null!;       // parallel hatch groups
    private List<LineSegmentChain> _pool = null!;   // outline + hatch chains (the NN input)
    private List<SvgPath> _solidPaths = null!;      // deduped outline + hatch-source paths
    private List<LineSegmentChain> _ordered = null!;// travel-optimized pool
    private Paths64 _solid = null!;
    private List<(Vector A, Vector B)> _rawSegments = null!;
    private Paths64 _hatchClip = null!;
    private HatchOptions _connectOpts = null!;
    private SolidRegion _region = null!;   // the fast solid-area index
    private Vector _a, _b;                           // two points inside the solid (for the single-call bench)

    [GlobalSetup]
    public void Setup()
    {
        _base = MakeOptions(Mode.Hatch, FillStyle.Parallel, optimize: true, connect: true);
        _base.Input = Svg;
        _cutOpts = MakeOptions(Mode.Cut, FillStyle.Parallel, optimize: false, connect: false);
        _cutOpts.Input = Svg;

        // Parse + transform exactly like the CLI.
        (_doc, _hatchOpts) = EngineBuilder.LoadDocument(_base);

        // Hatch (parallel) -- the default fill style, also feeds the compile stages.
        _groups = HatchEngine.Generate(_doc.Paths, _hatchOpts);

        // Reproduce StrokeEmitter.CompilePooledOutlinesAndHatches' optimize-travel pool so we
        // can benchmark its sub-stages (flatten / NN / emit) in isolation.
        _solidPaths = new List<SvgPath>();
        var seen = new HashSet<SvgPath>(ReferenceEqualityComparer.Instance);
        foreach (var p in _doc.Paths) if (seen.Add(p)) _solidPaths.Add(p);
        foreach (var g in _groups) if (g.Source != null && seen.Add(g.Source)) _solidPaths.Add(g.Source);

        _pool = new List<LineSegmentChain>();
        _pool.AddRange(StrokeEmitter.BuildOutlineChains(_doc.Paths, 0.01));
        _pool.AddRange(StrokeEmitter.BuildHatchChains(_groups, 0.01));

        _ordered = TravelOptimizer.Optimize(_pool, Vector.Zero, _base.OptimizeTravelReverse);

        // The connect-ends stages, isolated: the raw hatch segments, and the clip they are
        // verified against. Connect mutates its own working copy, so both benchmarks need fresh input.
        _connectOpts = EngineBuilder.BuildHatchOptions(_base);
        var loose = HatchEngine.Generate(_doc.Paths, new HatchOptions
        {
            FillStyle = FillStyle.Parallel,
            HatchSpacing = _connectOpts.HatchSpacing,
            HatchAngle = _connectOpts.HatchAngle,
            Inset = _connectOpts.Inset,
            InsetDistance = _connectOpts.InsetDistance,
            ConnectEnds = false,
            HatchScope = _connectOpts.HatchScope,
            Tolerance = _connectOpts.Tolerance,
            MaxParallelism = _connectOpts.MaxParallelism,
        });
        _rawSegments = new List<(Vector A, Vector B)>();
        foreach (var group in loose)
            foreach (var hatchPath in group.Paths)
            {
                Vector pen = hatchPath.Start;
                foreach (var op in hatchPath.Ops)
                    if (op is LineTo line) { _rawSegments.Add((pen, line.P)); pen = line.P; }
            }
        _hatchClip = HatchClipper.ToPaths64(
            HatchClipper.Inflate(HatchClipper.Union(_doc.Paths[0].Curves.Count > 0
                ? HatchEngine.BuildPolygons(_doc.Paths[0], _connectOpts.Tolerance / 100.0, _connectOpts.AutoClose)
                : []), -_connectOpts.InsetDistance, 0.25), 1_000_000.0);
        _solid = SolidGeometry.BuildSolidArea(_solidPaths, 0.01);
        _region = new SolidRegion(SolidGeometry.BuildSolidPolygons(_solidPaths, 0.01));

        // Pick an interior segment for the single-call solid benches: centroid of
        // the first ring's first three vertices (interior for a convex ring) + a
        // 1-unit step that stays inside on any reasonable outline.
        _a = new Vector(0, 0); _b = new Vector(1, 0);
        if (_solid.Count > 0)
        {
            var ring = _solid[0];
            if (ring.Count >= 3)
            {
                double cx = (ring[0].X + ring[1].X + ring[2].X) / 3_000_000.0;
                double cy = (ring[0].Y + ring[1].Y + ring[2].Y) / 3_000_000.0;
                _a = new Vector(cx, cy);
                _b = new Vector(cx + 1, cy);
            }
        }
    }

    // ---- parse ----
    [Benchmark] public (SvgDocument, HatchOptions) ParseAndTransform() => EngineBuilder.LoadDocument(CloneWithInput(_base));

    // ---- hatch per fill style ----
    [Benchmark] public List<HatchGroup> Hatch_Parallel() => HatchEngine.Generate(_doc.Paths, HatchOpts(FillStyle.Parallel, connect: true, inset: true));
    [Benchmark] public List<HatchGroup> Hatch_Parallel_NoConnect() => HatchEngine.Generate(_doc.Paths, HatchOpts(FillStyle.Parallel, connect: false, inset: true));
    [Benchmark] public List<HatchGroup> Hatch_Parallel_NoInset() => HatchEngine.Generate(_doc.Paths, HatchOpts(FillStyle.Parallel, connect: false, inset: false));
    [Benchmark] public List<HatchGroup> Hatch_Wave() => HatchEngine.Generate(_doc.Paths, HatchOpts(FillStyle.Wave, connect: true, inset: true));
    [Benchmark] public List<HatchGroup> Hatch_Concentric() => HatchEngine.Generate(_doc.Paths, HatchOpts(FillStyle.Concentric, connect: true, inset: true));
    [Benchmark] public List<HatchGroup> Hatch_Spiral() => HatchEngine.Generate(_doc.Paths, HatchOpts(FillStyle.Spiral, connect: true, inset: true));
    [Benchmark] public List<HatchGroup> Hatch_Hilbert() => HatchEngine.Generate(_doc.Paths, HatchOpts(FillStyle.Hilbert, connect: true, inset: true));

    // ---- connect-ends stages ----
    [Benchmark] public int ConnectOnly()
    {
        var paths = new List<HatchPath>(_rawSegments.Count);
        Hatch.PenLiftOptimizer.Connect(paths, _rawSegments, _connectOpts);
        return paths.Count;
    }

    [Benchmark] public int ClipConnectedOnly()
    {
        var paths = new List<HatchPath>(_groups.Sum(g => g.Paths.Count));
        foreach (var group in _groups)
            foreach (var hatchPath in group.Paths)
            {
                var copy = new HatchPath { Start = hatchPath.Start };
                copy.Ops.AddRange(hatchPath.Ops);
                paths.Add(copy);
            }
        Hatch.HatchGeometry.ClipPathsToPolygon(paths, _hatchClip, _connectOpts.Tolerance / 100.0);
        return paths.Count;
    }

    // ---- flatten / optimize / solid ----
    [Benchmark] public List<LineSegmentChain> FlattenOutlines() => StrokeEmitter.BuildOutlineChains(_doc.Paths, 0.01);
    [Benchmark] public List<LineSegmentChain> TravelOptimize() => TravelOptimizer.Optimize(_pool, Vector.Zero, _base.OptimizeTravelReverse);
    [Benchmark] public Paths64 BuildSolidAreaBench() => SolidGeometry.BuildSolidArea(_solidPaths, 0.01);
    [Benchmark] public bool SolidRegion_Build() => !new SolidRegion(SolidGeometry.BuildSolidPolygons(_solidPaths, 0.01)).IsEmpty;
    [Benchmark] public bool SegmentWithinSolid_Single() => SolidGeometry.SegmentWithinSolid(_a, _b, _solid);
    [Benchmark] public bool SolidRegion_SegmentInside() => _region.SegmentInside(_a, _b);

    // ---- emit (the connect-outlines hot path) ----
    [Benchmark] public Vector EmitOrdered_NoConnect() => EmitOrdered(connect: false);
    [Benchmark] public Vector EmitOrdered_Connect() => EmitOrdered(connect: true);

    // ---- end-to-end compile stages ----
    [Benchmark] public string CompileHatchPooled() => CompileHatch(connect: true);
    [Benchmark] public string CompileCut() => CompileCutRun();

    // ---- whole-mode runs (parse + hatch + compile), like invoking the CLI ----
    [Benchmark] public string EndToEnd_HatchParallel_Connect() => FullRun(Mode.Hatch, FillStyle.Parallel, optimize: true, connect: true);
    [Benchmark] public string EndToEnd_Cut() => FullRun(Mode.Cut, FillStyle.Parallel, optimize: false, connect: false);

    // ---- helpers ----

    private Vector EmitOrdered(bool connect)
    {
        var opts = CloneWithInput(_base);
        opts.ConnectOutlines = connect;
        var compiler = EngineBuilder.BuildCompiler(opts);
        int done = 0;
        return StrokeEmitter.EmitOrderedChains(opts, compiler, _ordered, _solidPaths,
            Vector.Zero, ref done, _ordered.Count, 0, 100, "bench", 0.01);
    }

    private string CompileHatch(bool connect)
    {
        var opts = CloneWithInput(_base);
        opts.ConnectOutlines = connect;
        var compiler = EngineBuilder.BuildCompiler(opts);
        StrokeEmitter.CompilePooledOutlinesAndHatches(opts, compiler, _doc.Paths, _groups, 0, 100, "bench");
        return compiler.Compile();
    }

    private string CompileCutRun()
    {
        var opts = CloneWithInput(_cutOpts);
        var compiler = EngineBuilder.BuildCompiler(opts);
        StrokeEmitter.CompileGroupedByColor(opts, compiler, _doc.Paths, 0, 100, "bench");
        return compiler.Compile();
    }

    private string FullRun(Mode mode, FillStyle style, bool optimize, bool connect)
    {
        var opts = MakeOptions(mode, style, optimize, connect);
        opts.Input = Svg;
        var (doc, hatchOpts) = EngineBuilder.LoadDocument(opts);
        var compiler = EngineBuilder.BuildCompiler(opts);
        if (mode == Mode.Cut)
        {
            StrokeEmitter.CompileGroupedByColor(opts, compiler, doc.Paths, 0, 100, "bench");
        }
        else
        {
            var groups = HatchEngine.Generate(doc.Paths, hatchOpts);
            StrokeEmitter.CompilePooledOutlinesAndHatches(opts, compiler, doc.Paths, groups, 0, 100, "bench");
        }
        return compiler.Compile();
    }

    /// <summary>Clone opts and keep the input path (LoadDocument reads from disk).</summary>
    private Options CloneWithInput(Options src)
    {
        var o = src.Clone();
        o.Input = Svg;
        return o;
    }

    /// <summary>A copy of _hatchOpts with a different fill style / connect / inset.</summary>
    private HatchOptions HatchOpts(FillStyle s, bool connect, bool inset)
    {
        var h = EngineBuilder.BuildHatchOptions(_base);
        h.FillStyle = s;
        h.ConnectEnds = connect;
        h.Inset = inset;
        return h;
    }

    /// <summary>Build CLI Options mirroring the documented defaults for a given mode.</summary>
    private static Options MakeOptions(Mode mode, FillStyle style, bool optimize, bool connect) => new()
    {
        Mode = mode,
        Input = "",
        Unit = "mm",
        TravelSpeed = 3000,
        CuttingSpeed = 750,
        Passes = 1,
        PassDepth = 1,
        ToolPower = "M3 S255;",
        ToolOff = "M5;",
        ApproximationTolerance = "0.01",
        MachineOrigin = "bottom-left",
        FillStyle = style,
        HatchSpacing = 3.0,
        HatchAngle = 45,
        HatchUnits = "mm",
        ConnectEnds = true,
        HatchScope = 3.0,
        Inset = true,
        InsetDist = 0.5,
        HatchTolerance = 2.0,
        WaveAmplitude = 1.0,
        WaveWavelength = 0.0,
        IncludeOutlines = true,
        OptimizeTravel = optimize,
        OptimizeTravelReverse = true,
        ConnectOutlines = connect,
        ConnectOutlinesDist = 2.0,
        DoToolOffStart = true,
        DoToolOffEnd = true,
    };
}
