using PlotterHelper.Hatch;

namespace PlotterHelper.Cli;

/// <summary>
/// The option surface, declared once. <see cref="ArgParser.Parse"/> walks this table and
/// <see cref="ArgParser.PrintHelp"/> prints it, so a flag cannot be added to one and forgotten
/// in the other; <c>InPanel</c> records whether the Inkscape extension can reach it, which the
/// option-surface test checks against <c>plotter-helper.inx</c> and <c>plotter-helper.py</c>.
/// </summary>
internal static class OptionTable
{
    private static OptionSpec Text(string flag, OptionApply apply, string help, bool inPanel = false)
        => new() { Flag = flag, Apply = apply, Help = help, InPanel = inPanel };

    private static OptionSpec Bool(string flag, Action<Options, bool> set, string help, bool inPanel = false)
        => new() { Flag = flag, Apply = (o, v) => set(o, ArgParser.ParseBool(v)), Help = help, InPanel = inPanel };

    private static OptionSpec Number(string flag, Action<Options, double> set, double fallback, string help, bool inPanel = false)
        => new() { Flag = flag, Apply = (o, v) => set(o, ArgParser.ParseDouble(v!, fallback)), Help = help, InPanel = inPanel };

    private static OptionSpec Count(string flag, Action<Options, int> set, int fallback, string help, bool inPanel = false)
        => new() { Flag = flag, Apply = (o, v) => set(o, (int)ArgParser.ParseDouble(v!, fallback)), Help = help, InPanel = inPanel };

    private const string Header =
@"PlotterHelper -- SVG to G-code with hatch fill

Usage: PlotterHelper --mode cut|hatch|hatchcut --input drawing.svg --gcode out.gcode
       [--preview out.svg] [options...]

Modes:
  cut       Convert SVG paths to G-code.
  hatch     Generate Axidraw-style hatch fills; emit a preview SVG (and optionally
            G-code of just the hatches if --gcode is given).
  hatchcut  Hatch closed paths, then plot outlines + hatches as G-code.
";

    /// <summary>Flags that are not part of a mode-specific section.</summary>
    private static readonly OptionSection Common = new("Common options:", null,
    [
        Text("--mode", (o, v) => o.Mode = Enum.Parse<Mode>(v!, true),
            "  --mode cut|hatch|hatchcut  What to produce; see Modes above. Default cut.", inPanel: true),
        Text("--input", (o, v) => o.Input = v!,
            "  --input FILE              Source SVG, or a PNG when --trace is on."),
        Text("--gcode", (o, v) => o.Gcode = v,
            "  --gcode FILE              Where to write the G-code. Optional for --mode hatch,\n" +
            "                            which can produce a preview alone."),
        Text("--preview", (o, v) => o.Preview = v,
            "  --preview FILE            Write a preview SVG of the drawing here.", inPanel: true),
        Bool("--show-outlines-in-preview", (o, v) => o.ShowOutlinesInPreview = v,
            "  --show-outlines-in-preview BOOL  Include the original outlines in the preview.\n" +
            "                            Default true.", inPanel: true),
    ]);

    private static readonly OptionSection Tracing = new(
        "Raster tracing (--trace, input is a PNG instead of an SVG):",
        "  Traces line art by centerline: each stroke becomes ONE path running from end to\n" +
        "  end, instead of the two parallel outlines a region tracer (Potrace, Inkscape\n" +
        "  brightness trace) would give. Ink too thick to be a pen stroke is emitted as a\n" +
        "  closed region so --mode hatch fills it. Traced geometry is then handed to the\n" +
        "  normal pipeline, so every option below still applies.\n" +
        "  Only PNG input is decoded in-tree (see PngReader); transcode JPEG/WebP first.\n\n",
    [
        Bool("--trace", (o, v) => o.Trace = v,
            "  --trace BOOL              Input is a raster; trace it, then run --mode. Default false."),
        Text("--trace-svg", (o, v) => o.TraceSvg = v,
            "  --trace-svg FILE          Also write the intermediate traced SVG here (handy for\n" +
            "                            touching it up in Inkscape)."),
        Number("--trace-width", (o, v) => o.Tracer.TargetWidth = v, 200,
            "  --trace-width D           Output width in --trace-units. Default 200."),
        Number("--trace-height", (o, v) => o.Tracer.TargetHeight = v, 0,
            "  --trace-height D          Output height; 0 keeps the aspect ratio. Default 0."),
        Text("--trace-units", (o, v) => o.Tracer.OutputUnits = v ?? "mm",
            "  --trace-units mm|in|px    Units for --trace-width/height. Default mm."),
        Count("--trace-upscale", (o, v) => o.Tracer.Upscale = v, 3,
            "  --trace-upscale N         Trace at N x the source resolution (2-4 is useful).\n" +
            "                            Default 3."),
        Count("--trace-max-pixels", (o, v) => o.Tracer.MaxPixels = v, 8000000,
            "  --trace-max-pixels N      Ceiling on the working raster, in pixels. The effective --trace-\n" +
            "                            upscale is reduced until the trace fits this budget (never below\n" +
            "                            1x). Raise it for finer lines, lower it for speed. Default 8e6."),
        Count("--trace-window", (o, v) => o.Tracer.ThresholdWindow = v, 21,
            "  --trace-window N          Adaptive threshold window, source px. MUST stay smaller\n" +
            "                            than the gap between adjacent lines, or dense hatching\n" +
            "                            welds into a solid slab. Default 21."),
        Number("--trace-k", (o, v) => o.Tracer.ThresholdK = v, 0.2,
            "  --trace-k D               Sauvola sensitivity; higher = fainter ink, more noise.\n" +
            "                            Default 0.2."),
        Number("--trace-threshold", (o, v) => o.Tracer.GlobalThreshold = v, 0,
            "  --trace-threshold N       Fixed 0-255 cutoff instead of the adaptive threshold.\n" +
            "                            Default 0 (adaptive)."),
        Bool("--trace-invert", (o, v) => o.Tracer.Invert = v,
            "  --trace-invert BOOL       Trace white ink on black. Default false."),
        Number("--trace-min-area", (o, v) => o.Tracer.MinComponentArea = v, 4,
            "  --trace-min-area D        Discard ink blobs smaller than this, source px^2. Default 4."),
        Number("--trace-spur", (o, v) => o.Tracer.MinSpur = v, 3,
            "  --trace-spur D            Drop dead-end branches shorter than this, source px.\n" +
            "                            Default 3."),
        Number("--trace-spur-ratio", (o, v) => o.Tracer.SpurRatio = v, 1.8,
            "  --trace-spur-ratio D      ... or shorter than D x the local ink half-width. Default 1.8."),
        Number("--trace-junction-merge", (o, v) => o.Tracer.JunctionMerge = v, 2,
            "  --trace-junction-merge R  Fuse junction nodes closer than R x the local ink half-width\n" +
            "                            into one. A shallow crossing arrives as a ladder of nodes\n" +
            "                            a few pixels apart, which has to be collapsed first.\n" +
            "                            0 disables. Default 2."),
        Number("--trace-junction-angle", (o, v) => o.Tracer.JunctionAngle = v, 45,
            "  --trace-junction-angle DEG  How far two branches at a crossing may deviate from\n" +
            "                            continuing each other and still be spliced into one stroke.\n" +
            "                            This is what makes a line that crosses another come out as a\n" +
            "                            single stroke. 0 disables. Default 45."),
        Number("--trace-join-gap", (o, v) => o.Tracer.JoinGap = v, 2,
            "  --trace-join-gap D        Bridge free stroke ends across a gap up to this wide, source\n" +
            "                            px: this is what rejoins a line the threshold chopped into\n" +
            "                            dashes. 0 disables. Default 2."),
        Number("--trace-join-angle", (o, v) => o.Tracer.JoinAngle = v, 40,
            "  --trace-join-angle DEG    How far the two ends may deviate from a straight join.\n" +
            "                            Default 40."),
        Number("--trace-thick-width", (o, v) => o.Tracer.ThickWidth = v, 5,
            "  --trace-thick-width D     Mean ink wider than this (source px) is traced as a closed\n" +
            "                            region for hatching instead of as a centerline. Measured on the\n" +
            "                            traced raster, which reads a bit wider than the nominal line.\n" +
            "                            0 traces everything as centerlines. Default 5."),
        Number("--trace-min-length", (o, v) => o.Tracer.MinLength = v, 2,
            "  --trace-min-length D      Drop traced strokes shorter than this, source px. Default 2."),
        Number("--trace-simplify", (o, v) => o.Tracer.Simplify = v, 0.4,
            "  --trace-simplify D        Polyline simplification tolerance, source px. Default 0.4."),
        Number("--trace-fit-error", (o, v) => o.Tracer.FitError = v, 0.5,
            "  --trace-fit-error D       Maximum Bezier fit deviation, source px -- roughly the\n" +
            "                            drawing accuracy you keep. Default 0.5."),
        Count("--trace-smooth", (o, v) => o.Tracer.Smooth = v, 1,
            "  --trace-smooth N          Skeleton smoothing window before fitting. 0 disables.\n" +
            "                            Default 1."),
    ]);

    private static readonly OptionSection Cut = new("Cut / G-code options:", null,
    [
        Text("--unit", (o, v) => o.Unit = v,
            "  --unit mm|in              Output unit label (G21/G20). Default mm.", inPanel: true),
        Number("--travel-speed", (o, v) => o.TravelSpeed = v, 3000,
            "  --travel-speed N          Travel (pen-up) speed. Default 3000.", inPanel: true),
        Number("--cutting-speed", (o, v) => o.CuttingSpeed = v, 750,
            "  --cutting-speed N         Cutting (pen-down) speed. Default 750.", inPanel: true),
        Count("--passes", (o, v) => o.Passes = v, 1,
            "  --passes N                Number of passes. Default 1.", inPanel: true),
        Number("--pass-depth", (o, v) => o.PassDepth = v, 1,
            "  --pass-depth N            Z step between passes. Default 1."),
        Text("--tool-power-command", (o, v) => o.ToolPower = v!,
            "  --tool-power-command S    Default \"M3 S255;\"", inPanel: true),
        Text("--tool-off-command", (o, v) => o.ToolOff = v!,
            "  --tool-off-command S      Default \"M5;\"", inPanel: true),
        Number("--dwell-time", (o, v) => o.DwellTime = v, 0,
            "  --dwell-time MS           Dwell after the pen lowers at each stroke start. Default 0.", inPanel: true),
        Number("--dwell-after-pen-up", (o, v) => o.DwellAfterPenUp = v, 0,
            "  --dwell-after-pen-up MS   Dwell after the pen lifts at each stroke end. Default 0.", inPanel: true),
        Text("--approximation-tolerance", (o, v) => o.ApproximationTolerance = v!,
            "  --approximation-tolerance T  Curve flattening tolerance. Default 0.01.", inPanel: true),
        Text("--machine-origin", (o, v) => o.MachineOrigin = v!,
            "  --machine-origin ORIG     bottom-left|center|top-left. Default bottom-left.", inPanel: true),
        Bool("--invert-y", (o, v) => o.InvertY = v,
            "  --invert-y BOOL           Keep SVG y-down coordinates. Default false.", inPanel: true),
        Number("--bed-width", (o, v) => o.BedWidth = v, 0,
            "  --bed-width D            Machine bed width; defaults to the SVG canvas.", inPanel: true),
        Number("--bed-height", (o, v) => o.BedHeight = v, 0,
            "  --bed-height D           Machine bed height; defaults to the SVG canvas.", inPanel: true),
        Number("--horizontal-offset", (o, v) => o.HorizontalOffset = v, 0,
            "  --horizontal-offset D    G-code X offset. Default 0.", inPanel: true),
        Number("--vertical-offset", (o, v) => o.VerticalOffset = v, 0,
            "  --vertical-offset D      G-code Y offset. Default 0.", inPanel: true),
        Number("--scaling-factor", (o, v) => o.ScalingFactor = v, 1,
            "  --scaling-factor S        Default 1.", inPanel: true),
        Text("--header", (o, v) => o.HeaderText = v,
            "  --header TEXT            Pasted G-code header (split on newlines).", inPanel: true),
        Text("--footer", (o, v) => o.FooterText = v,
            "  --footer TEXT            Pasted G-code footer (split on newlines).", inPanel: true),
        Text("--header-path", (o, v) => o.HeaderPath = v,
            "  --header-path FILE       Header G-code from a file; used when --header is absent."),
        Text("--footer-path", (o, v) => o.FooterPath = v,
            "  --footer-path FILE       Footer G-code from a file; used when --footer is absent."),
        Bool("--do-z-axis-start", (o, v) => o.DoZAxisStart = v,
            "  --do-z-axis-start BOOL   Raise to --z-axis-start before the job. Default false."),
        Number("--z-axis-start", (o, v) => o.ZAxisStart = v, 0,
            "  --z-axis-start Z         Z height for that first move. Default 0."),
        Bool("--return-home-end", (o, v) => o.ReturnHomeEnd = v,
            "  --return-home-end BOOL   Home the axes (G28) after the job. Default false.", inPanel: true),
        Bool("--do-tool-off-start", (o, v) => o.DoToolOffStart = v,
            "  --do-tool-off-start BOOL Tool off before the job. Default true.", inPanel: true),
        Bool("--do-tool-off-end", (o, v) => o.DoToolOffEnd = v,
            "  --do-tool-off-end BOOL   Tool off after the job. Default true.", inPanel: true),
    ]);

    private static readonly OptionSection Hatch = new("Hatch options:", null,
    [
        Number("--hatch-spacing", (o, v) => o.HatchSpacing = v, 3,
            "  --hatch-spacing D         Distance between hatch lines. Default 3.", inPanel: true),
        Number("--hatch-angle", (o, v) => o.HatchAngle = v, 45,
            "  --hatch-angle DEG         Hatch angle. Default 45.", inPanel: true),
        Bool("--cross-hatch", (o, v) => o.CrossHatch = v,
            "  --cross-hatch BOOL        Add a perpendicular pass. Default false.", inPanel: true),

        // Tone: what the shade, stipple and scribble fills read. Without --tone they shade by the shape's own
        // boundary, which needs no image; with it they shade by the picture.
        Text("--tone", (o, v) => o.TonePath = v,
            "  --tone FILE               Raster to shade by, for --fill-style shade|stipple|scribble.", inPanel: false),
        Bool("--tone-invert", (o, v) => o.ToneInvert = v,
            "  --tone-invert BOOL        Treat light pixels as ink (white on black). Default false.", inPanel: false),
        Number("--tone-threshold", (o, v) => o.ToneThreshold = v, 0.05,
            "  --tone-threshold N        Tone at or below which the sheet reads as bare paper, 0..1. Above it ink follows the tone. Default 0.05.", inPanel: false),
        Number("--tone-depth", (o, v) => o.ToneDepth = v, 6.0,
            "  --tone-depth N            How far the shape's boundary tone reaches, in hatch spacings. Default 6.", inPanel: false),
        Text("--hatch-units", (o, v) => o.HatchUnits = v!,
            "  --hatch-units px|mm|in    Units for spacing/inset. Default mm.", inPanel: true),
        Bool("--connect-ends", (o, v) => o.ConnectEnds = v,
            "  --connect-ends BOOL       Join nearby ends with Beziers (pen-lift reduction). Default true.", inPanel: true),
        Number("--hatch-scope", (o, v) => o.HatchScope = v, 3,
            "  --hatch-scope R           End-connection range (in spacings). Default 3.", inPanel: true),
        Bool("--inset", (o, v) => o.Inset = v,
            "  --inset BOOL              Hold hatches back from edges. Default true.", inPanel: true),
        Bool("--auto-close", (o, v) => o.AutoClose = v,
            "  --auto-close BOOL         Treat open subpaths as closed when hatching (bridge\n" +
            "                            the gap) so shapes drawn without a closing Z still fill.\n" +
            "                            Default false."),
        Number("--inset-dist", (o, v) => o.InsetDist = v, 0.5,
            "  --inset-dist D            Inset distance. Default 0.5.", inPanel: true),
        Number("--hatch-tolerance", (o, v) => o.HatchTolerance = v, 2,
            "  --hatch-tolerance T       Input curve flattening tolerance. Default 2.", inPanel: true),
        Bool("--handle-viewbox", (o, v) => o.HandleViewBox = v,
            "  --handle-viewbox BOOL     Scale by SVG viewBox (axidraw-style). Default false.", inPanel: true),
        Bool("--include-outlines", (o, v) => o.IncludeOutlines = v,
            "  --include-outlines BOOL   Draw the original outlines in hatch/hatchcut G-code.\n" +
            "                            Default true.", inPanel: true),
        Count("--max-parallelism", (o, v) => o.MaxParallelism = v, -1,
            "  --max-parallelism N       Cap parallel hatch work. Default = all cores."),
    ]);

    private static readonly OptionSection Fill = new("Fill style (Phase 1: parallel, wave):", null,
    [
        Text("--fill-style", (o, v) => o.FillStyle = Enum.Parse<FillStyle>(v!, true),
            "  --fill-style STYLE        parallel|wave|concentric|spiral|hilbert|serpentine|fermat|moire|peano|engrave|contourspiral|hexlattice|squarespiral|truchet|gosper|dragon|shade|stipple|scribble. Default parallel.", inPanel: true),
        Number("--wave-amplitude", (o, v) => o.WaveAmplitude = v, 1,
            "  --wave-amplitude A        Sine-wave amplitude (wave style). Default 1.", inPanel: true),
        Number("--wave-wavelength", (o, v) => o.WaveWavelength = v, 0,
            "  --wave-wavelength W       Sine-wave wavelength (0 = 2x spacing). Default 0.", inPanel: true),
        Bool("--jitter", (o, v) => o.Jitter = v,
            "  --jitter BOOL             Apply seeded hand-drawn jitter to any style. Default false.", inPanel: true),
        Number("--jitter-amount", (o, v) => o.JitterAmount = v, 0.3,
            "  --jitter-amount D         Jitter std-dev. Default 0.3.", inPanel: true),
        Text("--jitter-seed", (o, v) => o.JitterSeed = ArgParser.ParseInt(v!, 12345),
            "  --jitter-seed N           Jitter RNG seed (reproducible). Default 12345.", inPanel: true),
    ]);

    private static readonly OptionSection MultiColor = new("Multi-color pen plotting:", null,
    [
        Bool("--group-by-color", (o, v) => o.GroupByColor = v,
            "  --group-by-color BOOL     Draw one pen color at a time. Outlines are colored by\n" +
            "                            stroke, hatch fills by the element's fill; matching\n" +
            "                            colors run together (e.g. red outline + red hatch),\n" +
            "                            then a pause separates the next color. Default false.", inPanel: true),
        Bool("--pause-between-colors", (o, v) => o.PauseBetweenColors = v,
            "  --pause-between-colors BOOL  Emit a pause between color groups (pen swap). Default false.", inPanel: true),
        Text("--pause-command", (o, v) => o.PauseCommand = v ?? "M0",
            "  --pause-command S         Pause command. Default \"M0\".", inPanel: true),
        Text("--per-color-config", (o, v) => o.PerColorConfigPath = v,
            "  --per-color-config FILE   JSON file mapping each pen's color (6-digit hex) to per-pen\n" +
            "                            overrides of any setting (fillStyle, hatchSpacing, angle,\n" +
            "                            crossHatch, inset, connectEnds, wave/jitter, passes,\n" +
            "                            passDepth, cuttingSpeed, travelSpeed, overcut, dwell,\n" +
            "                            toolPowerCommand, toolOffCommand). Only listed fields\n" +
            "                            override the globals; colors not listed are not hatched.\n" +
            "                            See examples/per-color-example.json. Default off.", inPanel: true),
        Bool("--color-announce", (o, v) => o.ColorAnnounce = v,
            "  --color-announce BOOL     Emit a host-console message naming the next pen color\n" +
            "                            before each pause (Klipper RESPOND). Default true."),
        Text("--color-announce-command", (o, v) => o.ColorAnnounceCommand = v ?? "RESPOND",
            "  --color-announce-command S  G-code command for the announce line (RESPOND or M117).\n" +
            "                            Default \"RESPOND\"."),
    ]);

    private static readonly OptionSection Tiling = new(
        "Tiling / paneling (cut + hatch; one G-code file per tile):", null,
    [
        Number("--tile-width", (o, v) => o.TileWidth = v, 0,
            "  --tile-width D            Tile width; >0 enables tiling. Default 0 (off).", inPanel: true),
        Number("--tile-height", (o, v) => o.TileHeight = v, 0,
            "  --tile-height D           Tile height; >0 enables tiling. Default 0 (off).", inPanel: true),
        Number("--tile-overlap", (o, v) => o.TileOverlap = v, 0,
            "  --tile-overlap D          Overlap between adjoining tiles. Default 0.", inPanel: true),
        Bool("--tile-alignment-marks", (o, v) => o.TileAlignmentMarks = v,
            "  --tile-alignment-marks BOOL  Draw corner L-marks per tile. Default false.", inPanel: true),
        Number("--tile-mark-size", (o, v) => o.TileMarkSize = v, 1,
            "  --tile-mark-size D        Alignment-mark size. Default 1.", inPanel: true),
    ]);

    private static readonly OptionSection Cutting = new("Cutting (drag-knife / vinyl):", null,
    [
        Number("--overcut", (o, v) => o.Overcut = v, 0,
            "  --overcut D              Extend closed paths past their start so corners close. Default 0 (off).", inPanel: true),
        Bool("--inner-before-outer", (o, v) => o.InnerBeforeOuter = v,
            "  --inner-before-outer BOOL  Cut interior holes before outer outlines. Default false.", inPanel: true),
        Bool("--optimize-travel", (o, v) => o.OptimizeTravel = v,
            "  --optimize-travel BOOL    Reorder outline strokes (greedy nearest-neighbor) to cut\n" +
            "                            pen-up travel. Reverses strokes when that helps. Default false.", inPanel: true),
        Bool("--optimize-travel-reverse", (o, v) => o.OptimizeTravelReverse = v,
            "  --optimize-travel-reverse BOOL  Allow reversing strokes in the travel optimization.\n" +
            "                            Default true (turn off if cut direction must be preserved).", inPanel: true),
        Bool("--connect-outlines", (o, v) => o.ConnectOutlines = v,
            "  --connect-outlines BOOL   During travel optimization, join consecutive strokes whose\n" +
            "                            end-to-start gap is within --connect-outlines-dist AND whose\n" +
            "                            straight connector lies inside the solid (closed-outline)\n" +
            "                            area, drawing it pen-down instead of lifting. Reduces pen\n" +
            "                            lifts; for pen plotting / engraving, not drag-knife cutting.\n" +
            "                            Default false. Only applies with --optimize-travel.", inPanel: true),
        Number("--connect-outlines-dist", (o, v) => o.ConnectOutlinesDist = v, 2,
            "  --connect-outlines-dist D Max gap (mm) to join outline ends through the fill. Default 2.", inPanel: true),
    ]);

    private static readonly OptionSection Pen = new("Pen / dot options:", null,
    [
        Bool("--double-pass-short", (o, v) => o.DoublePassShort = v,
            "  --double-pass-short BOOL  Re-trace short closed strokes twice without lifting the pen\n" +
            "                            (so ballpoint-pen dots deposit enough ink). Default false.", inPanel: true),
        Number("--double-pass-length", (o, v) => o.DoublePassLength = v, 2,
            "  --double-pass-length D    Length (mm) below which a closed stroke is drawn twice.\n" +
            "                            Default 2.", inPanel: true),
    ]);

    private static readonly OptionSection Klipper = new(
        "Klipper / Moonraker (upload instead of saving to disk):", null,
    [
        Text("--output-target", (o, v) => o.OutputTarget = v ?? "disk",
            "  --output-target disk|upload|upload-start  Default disk.", inPanel: true),
        Text("--moonraker-url", (o, v) => o.MoonrakerUrl = v ?? "",
            "  --moonraker-url URL        Moonraker base URL, e.g. http://192.168.1.50", inPanel: true),
        Text("--moonraker-api-key", (o, v) => o.MoonrakerApiKey = v ?? "",
            "  --moonraker-api-key KEY   Optional; only if not on Moonraker's trusted network.", inPanel: true),
        Text("--moonraker-filename", (o, v) => o.MoonrakerFilename = v ?? "",
            "  --moonraker-filename NAME G-code filename on the printer (default: <gcode basename>).", inPanel: true),
        Bool("--klipper-thumbnail", (o, v) => o.KlipperThumbnail = v,
            "  --klipper-thumbnail BOOL  Embed a PNG preview of the drawing in the upload so\n" +
            "                            Mainsail/Fluidd/KlipperScreen show it. Default true.", inPanel: true),
    ]);

    private static readonly OptionSection Diagnostics = new("Diagnostics:", null,
    [
        Bool("--progress", (o, v) => o.Progress = v,
            "  --progress BOOL           Write PROGRESS lines on stderr for the extension's dialog.\n" +
            "                            Default false."),
        Bool("--debug-stats", (o, v) => o.DebugStats = v,
            "  --debug-stats BOOL        Print the resolved settings and exit."),
    ]);

    internal static readonly IReadOnlyList<OptionSection> Sections =
    [
        Common, Tracing, Cut, Hatch, Fill, MultiColor, Tiling, Cutting, Pen, Klipper, Diagnostics,
    ];

    internal static readonly IReadOnlyList<OptionSpec> All =
        Sections.SelectMany(s => s.Options).ToList();

    internal static readonly IReadOnlyDictionary<string, OptionSpec> ByFlag =
        All.ToDictionary(s => s.Flag, StringComparer.Ordinal);

    internal static string HeaderText => Header;
}
