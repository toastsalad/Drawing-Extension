using PlotterHelper.Hatch;
using PlotterHelper.Cli.Pens;
using PlotterHelper.Trace;

namespace PlotterHelper.Cli;

internal enum Mode { Cut, Hatch, HatchCut }

internal sealed class Options
{
    public Mode Mode = Mode.Cut;
    public string Input = "";
    public string? Gcode;
    public string? Preview;
    public bool ShowOutlinesInPreview = true;
    public string? Unit;
    public double TravelSpeed = 3000, CuttingSpeed = 750;
    public int Passes = 1;
    public double PassDepth = 1;
    public string ToolPower = "M3 S255;", ToolOff = "M5;";
    public double DwellTime;
    public double DwellAfterPenUp;
    public string ApproximationTolerance = "0.01";

    /// <summary>The curve-flattening tolerance in user units, parsed from
    /// <see cref="ApproximationTolerance"/>. Read it once per method and pass it down.
    /// This is not <see cref="Hatch.HatchOptions.Tolerance"/>: that one is a hatch-input
    /// tolerance (divided by 100 in <see cref="Hatch.HatchEngine.Generate"/>) and only
    /// affects hatch polygon building. Do not merge them.</summary>
    public double FlatteningTolerance => ArgParser.ParseDouble(ApproximationTolerance, Tolerances.Approximation);
    public string MachineOrigin = "bottom-left";
    public bool InvertY;
    public double? BedWidth, BedHeight;
    public double HorizontalOffset, VerticalOffset, ScalingFactor = 1;
    public string? HeaderPath, FooterPath;
    public string? HeaderText, FooterText; // pasted literal G-code (newline-split)
    public bool DoZAxisStart, ReturnHomeEnd, DoToolOffStart = true, DoToolOffEnd = true;
    public double ZAxisStart;
    // Hatch:
    public FillStyle FillStyle = FillStyle.Parallel;
    public double HatchSpacing = 3.0, HatchAngle = 45;
    public bool CrossHatch;
    public string HatchUnits = "mm";
    public bool ConnectEnds = true;
    public double HatchScope = 3.0;
    public bool Inset = true;
    public bool AutoClose = false;
    public double InsetDist = 0.5;
    public double HatchTolerance = 2.0;
    public double WaveAmplitude = 1.0, WaveWavelength;
    public bool Jitter;
    public double JitterAmount = 0.3;
    public int JitterSeed = 12345;
    // Per-color grouping (multi-color pen plotting):
    public bool GroupByColor;
    public bool PauseBetweenColors;
    public string PauseCommand = "M0";
    // Announce the next pen color in the host console (Klipper `RESPOND`) before
    // each pen-swap pause, so the operator sees which color to install.
    public bool ColorAnnounce = true;
    public string ColorAnnounceCommand = "RESPOND";
    // Tiling / paneling:
    public double TileWidth, TileHeight, TileOverlap, TileMarkSize;
    public bool TileAlignmentMarks;
    // Cutter (drag-knife) options:
    public double Overcut;
    public bool InnerBeforeOuter;
    // Travel optimization: greedy nearest-neighbor reorder of outline strokes to
    // minimize pen-up travel (optionally reversing strokes). Overrides
    // --inner-before-outer when on.
    public bool OptimizeTravel;
    public bool OptimizeTravelReverse = true;
    // Connect nearby outline ends through the solid area during travel
    // optimization: when the gap to the next reordered stroke is within this
    // distance AND the straight connector lies inside the filled (closed-
    // outline) region, draw it pen-down instead of lifting. Only applies with
    // --optimize-travel; the hatch fill is unaffected. Off by default.
    public bool ConnectOutlines;
    public double ConnectOutlinesDist = 2.0;   // threshold in mm (-> doc units)
    // Double-pass short closed strokes (ballpoint dots): re-trace tiny closed
    // loops with the pen down so the ink shows.
    public bool DoublePassShort;
    public double DoublePassLength = 2.0;   // threshold in mm (converted to doc units)
    // Klipper / Moonraker upload:
    public string OutputTarget = "disk";   // disk | upload | upload-start
    public string MoonrakerUrl = "";
    public string MoonrakerApiKey = "";
    public string MoonrakerFilename = "";
    // Klipper thumbnail: when true (default) a PNG preview of the drawing is
    // embedded in the uploaded G-code as a PrusaSlicer-style ; thumbnail
    // block so Mainsail/Fluidd/KlipperScreen show it (Moonraker extracts it).
    public bool KlipperThumbnail = true;
    public byte[]? ThumbnailPng;     // rendered by PrepareThumbnail before upload
    public bool HandleViewBox;
    public int MaxParallelism = -1;
    public bool IncludeOutlines = true;
    public bool DebugStats;
    public bool Progress;

    // ---- tone (the image-driven fills: shade, stipple, scribble) ----------------
    /// <summary>Raster to shade by. Null leaves the tone fills on the shape's own boundary tone.</summary>
    public string? TonePath;

    /// <summary>The raster itself, loaded in Main so the engine never touches the filesystem.</summary>
    public GrayImage? ToneImage;

    /// <summary>Treat light pixels as ink: for white-on-black artwork.</summary>
    public bool ToneInvert;

    /// <summary>How dark a point must be before an image-driven fill inks it.</summary>
    public double ToneThreshold = 0.05;

    /// <summary>How far the shape's own boundary tone reaches, as a multiple of the hatch spacing.</summary>
    public double ToneDepth = 6.0;

    // ---- raster tracing (--trace) -----------------------------------------------
    /// <summary>Input is a raster (PNG); trace it to an SVG first, then run the mode below.</summary>
    public bool Trace;

    /// <summary>Also keep the intermediate traced SVG here, so it can be opened in Inkscape.</summary>
    public string? TraceSvg;

    public TraceOptions Tracer = new();
    // Per-color / per-pen settings: a JSON config (hex -> overrides) loaded in
    // Main from --per-color-config. When set, hatch/hatchcut/cut compile per
    // color (per pen) with these overrides; colors not listed are not hatched.
    public string? PerColorConfigPath;
    public Dictionary<string, PerColorOpts>? PerColor;

    /// <summary>Copy so per-color overrides can mutate the clone without
    /// touching the global options. Value-type fields and immutable strings
    /// copy by value; <see cref="Tracer"/> is a reference type and is cloned
    /// explicitly. The PerColor map is shared and must not be mutated after
    /// load.</summary>
    public Options Clone()
    {
        var clone = (Options)MemberwiseClone();
        clone.Tracer = Tracer.Clone();
        return clone;
    }
}