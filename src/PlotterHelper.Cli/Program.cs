using PlotterHelper.Compiler;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using PlotterHelper.Trace;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using PlotterHelper.Cli.Features;
using PlotterHelper.Cli.Output;
using PlotterHelper.Cli.Pens;

namespace PlotterHelper.Cli;

/// <summary>
/// PlotterHelper -- a fast, .NET 10 SVG-to-G-code tool with Axidraw-style hatch
/// fill. Usable as a CLI or as the engine behind the bundled Inkscape extension
/// (see inkscape/plotter-helper.py).
/// </summary>
/// <remarks>
/// This class is the thin entry point: argument parsing lives in <see cref="ArgParser"/>,
/// document/compiler/hatch-option construction in <see cref="EngineBuilder"/>, stroke
/// emission (color grouping, travel optimization, connect-outlines, solid-area
/// gating) in <see cref="StrokeEmitter"/>, pen-swap announce lines in
/// <see cref="PenAnnounce"/>, G-code output/thumbnail in <see cref="GcodeOutput"/>,
/// per-color compilation in <see cref="PerColorCompiler"/>, progress in
/// <see cref="Progress"/>, unit conversion in <see cref="Units"/>, and debug stats in
/// <see cref="DebugStats"/>. The config type is <see cref="Options"/>.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (ArgParser.IsHelpRequest(args))
        {
            ArgParser.PrintHelp();
            // No arguments at all is a usage error; an explicit --help asked for this.
            return args.Length == 0 ? 2 : 0;
        }

        var opts = new Options();
        if (!ArgParser.Parse(args, opts))
            return 2;

        // Drop flags that cannot do anything here, and say so. Silently ignoring them is how a
        // panel ends up offering a control that has no effect.
        foreach (string note in OptionDependencies.Normalize(opts))
            Console.Error.WriteLine("PlotterHelper: note: " + note);

        if (string.IsNullOrEmpty(opts.Input) || !File.Exists(opts.Input))
        {
            Console.Error.WriteLine("PlotterHelper: input not found: '" + opts.Input + "'");
            return 2;
        }

        // The raster the shading fills read. Loaded here so the engine never touches the filesystem, and
        // only when asked for: every other fill is geometry-only.
        if (!string.IsNullOrWhiteSpace(opts.TonePath))
        {
            if (!File.Exists(opts.TonePath))
            {
                Console.Error.WriteLine($"PlotterHelper: tone image not found: '{opts.TonePath}'");
                return 2;
            }
            try { opts.ToneImage = PngReader.Read(opts.TonePath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"PlotterHelper: failed to read tone image: {ex.Message}");
                return 2;
            }

            // A tone image only means something to the fills that shade; say so rather than ignoring it.
            if (opts.FillStyle is not (FillStyle.Shade or FillStyle.Stipple or FillStyle.Scribble))
                Console.Error.WriteLine($"PlotterHelper: note: --tone has no effect on --fill-style {opts.FillStyle}");
        }

        if (string.IsNullOrWhiteSpace(opts.PerColorConfigPath) == false)
        {
            if (!File.Exists(opts.PerColorConfigPath))
            {
                Console.Error.WriteLine($"PlotterHelper: per-color config not found: '{opts.PerColorConfigPath}'");
                return 2;
            }
            try { opts.PerColor = PerColorConfig.Load(opts.PerColorConfigPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"PlotterHelper: failed to parse per-color config: {ex.Message}");
                return 2;
            }
        }

        if (opts.Trace && !TraceRaster(opts)) return 2;

        if (opts.DebugStats)
        {
            DebugStats.Print(opts);
            return 0;
        }

        try
        {
            return opts.Mode switch
            {
                Mode.Cut => RunCut(opts),
                Mode.Hatch or Mode.HatchCut => RunHatchLike(opts),
                _ => 2,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PlotterHelper: {ex.Message}");
            return 1;
        }
    }

    // ---- modes ----------------------------------------------------------------

    /// <summary>Trace the raster to an SVG, then hand that SVG to the normal pipeline so every
    /// mode works on raster input with no new code.</summary>
    private static bool TraceRaster(Options opts)
    {
        Progress.Report(opts, 1, "Reading raster...");
        var image = PngReader.Read(opts.Input);
        Progress.Report(opts, 3, $"Tracing {image.Width}x{image.Height}...");

        // The same --max-parallelism flag caps the tracer.
        opts.Tracer.MaxParallelism = opts.MaxParallelism;

        var result = RasterTracer.Trace(image, opts.Tracer);
        Progress.Report(opts, 18,
            $"Traced {result.StrokeCount} strokes, {result.RegionCount} filled regions (mean line {result.MeanStrokeWidth:0.##}px)");

        string traced = opts.TraceSvg
            ?? Path.Combine(Path.GetTempPath(), "plotterhelper-trace-" + Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(traced, result.Svg);

        opts.Input = traced;
        opts.HandleViewBox = true; // the traced SVG is sized in output units with a pixel viewBox
        return true;
    }

    private static int RunCut(Options opts)
    {
        Progress.Report(opts, 2, "Parsing SVG…");
        var (doc, _) = EngineBuilder.LoadDocument(opts);
        Progress.Report(opts, 8, $"Parsed {doc.Paths.Count} paths");
        double tolerance = opts.FlatteningTolerance;
        if (opts.PerColor != null && !Tiling.Enabled(opts))
            return PerColorCompiler.Run(opts, doc, hatchMode: false);
        if (opts.DoublePassShort)
            foreach (var path in doc.Paths) DoublePass.ApplyToPath(path, Units.ToDocUnits(opts.DoublePassLength, "mm", opts.Unit), tolerance);
        if (opts.Overcut > 0)
            foreach (var path in doc.Paths) Overcut.ApplyToPath(path, opts.Overcut, tolerance);
        if (opts.Preview != null)
            File.WriteAllText(opts.Preview, PreviewSvg.Write(doc, Array.Empty<HatchGroup>(), showOutlines: true, tolerance));
        if (Tiling.Enabled(opts))
        {
            // Inner-before-outer reorders whole closed paths before tiling so each
            // tile inherits the depth order (only meaningful when not also grouping
            // by color, which re-sorts by color).
            if (opts.InnerBeforeOuter && !opts.GroupByColor)
            {
                var ordered = InnerBeforeOuter.Reorder(doc.Paths, tolerance);
                doc.Paths.Clear(); doc.Paths.AddRange(ordered);
            }
            Tiling.Run(opts, Tiling.FlattenOutlines(doc, tolerance));
            Progress.Report(opts, 100, "Done");
            return 0;
        }
        var compiler = EngineBuilder.BuildCompiler(opts);
        StrokeEmitter.CompileGroupedByColor(opts, compiler, doc.Paths, 8, 99, "Flattening/compiling");
        GcodeOutput.PrepareThumbnail(opts, doc, new List<HatchGroup>(), showOutlines: true);
        GcodeOutput.Output(opts, compiler);
        Progress.Report(opts, 100, "Done");
        return 0;
    }

    /// <summary>
    /// The two hatch modes are one pipeline. They differ only in where three stages sit around
    /// <see cref="HatchEngine.Generate"/>, and that placement is load-bearing:
    ///
    ///   Hatch     double-pass AFTER hatching,  no overcut,  preview before tiling
    ///   HatchCut  double-pass and overcut BEFORE hatching,   preview last, no tiling
    ///
    /// Double-pass runs after hatching in Hatch mode so the hatch polygon builder sees the
    /// original (undoubled) subpaths, and only the compiled outlines are doubled.
    /// </summary>
    private static int RunHatchLike(Options opts)
    {
        bool cutMode = opts.Mode == Mode.HatchCut;

        Progress.Report(opts, 2, "Parsing SVG…");
        var (doc, hatchOpts) = EngineBuilder.LoadDocument(opts);
        Progress.Report(opts, 8, $"Parsed {doc.Paths.Count} paths");
        double tolerance = opts.FlatteningTolerance;

        if (opts.PerColor != null && !Tiling.Enabled(opts))
            return PerColorCompiler.Run(opts, doc, hatchMode: true);

        double DoublePassLength() => Units.ToDocUnits(opts.DoublePassLength, "mm", opts.Unit);

        // Overcut widens closed outlines so a drag-knife clears each corner, so it only
        // applies when cutting.
        if (cutMode && opts.Overcut > 0)
            foreach (var path in doc.Paths) Overcut.ApplyToPath(path, opts.Overcut, tolerance);

        // Before hatching in HatchCut, after it in Hatch -- see the summary above.
        if (cutMode && opts.DoublePassShort)
            foreach (var path in doc.Paths) DoublePass.ApplyToPath(path, DoublePassLength(), tolerance);

        var groups = HatchEngine.Generate(doc.Paths, hatchOpts,
            progress: (done, total) => Progress.Report(opts, 8 + 77 * done / Math.Max(1, total), $"Hatching element {done}/{total}"));

        if (!cutMode && opts.DoublePassShort)
            foreach (var path in doc.Paths) DoublePass.ApplyToPath(path, DoublePassLength(), tolerance);

        // The preview sits before tiling in Hatch mode, so it is still written when tiling
        // returns early; HatchCut has no tiling stage and writes it once the G-code is out.
        void WritePreviewIfAsked()
        {
            if (opts.Preview != null)
                File.WriteAllText(opts.Preview, PreviewSvg.Write(doc, groups, showOutlines: opts.ShowOutlinesInPreview, tolerance));
        }

        if (!cutMode) WritePreviewIfAsked();

        if (!cutMode && Tiling.Enabled(opts))
        {
            var flat = new List<Tiling.FlatPath>();
            if (opts.IncludeOutlines) flat.AddRange(Tiling.FlattenOutlines(doc, tolerance));
            flat.AddRange(Tiling.FlattenHatches(groups, tolerance));
            Tiling.Run(opts, flat);
            Progress.Report(opts, 100, "Done");
            return 0;
        }

        // Hatch compiles G-code only when there is somewhere to put it; HatchCut always does.
        if (cutMode || opts.Gcode != null || opts.OutputTarget != "disk")
        {
            var compiler = EngineBuilder.BuildCompiler(opts);
            // Outlines (colored by stroke) and hatch fill (colored by fill) merge into one pen
            // per color with --group-by-color; otherwise outlines come first, then a monochrome
            // hatch fill. --include-outlines=false drops the outlines in either mode.
            if (EmissionPlan.From(opts).PerColor)
                StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 85, 99, "Color groups");
            else
                StrokeEmitter.CompilePooledOutlinesAndHatches(opts, compiler, doc.Paths, groups, 85, 99, "Outlines + hatches");
            GcodeOutput.PrepareThumbnail(opts, doc, groups, showOutlines: opts.IncludeOutlines);
            GcodeOutput.Output(opts, compiler);
        }

        if (cutMode) WritePreviewIfAsked();

        Progress.Report(opts, 100, "Done");
        return 0;
    }
}