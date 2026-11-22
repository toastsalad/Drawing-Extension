using PlotterHelper.Compiler;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;

namespace PlotterHelper.Cli;

/// <summary>
/// Builds the runtime objects the pipeline drives: the parsed SVG document with the
/// root transform (origin/offsets/scale + bed fallback), the hatch options (in doc
/// units), and the configured G-code compiler (header/footer, speeds, dwell, passes).
/// </summary>
internal static class EngineBuilder
{
    internal static HatchOptions BuildHatchOptions(Options opts) => new()
    {
        HatchSpacing = Units.ToDocUnits(opts.HatchSpacing, opts.HatchUnits, opts.Unit),
        HatchAngle = opts.HatchAngle,
        CrossHatch = opts.CrossHatch,
        FillStyle = opts.FillStyle,
        WaveAmplitude = Units.ToDocUnits(opts.WaveAmplitude, opts.HatchUnits, opts.Unit),
        WaveWavelength = Units.ToDocUnits(opts.WaveWavelength == 0 ? opts.HatchSpacing * 2 : opts.WaveWavelength, opts.HatchUnits, opts.Unit),
        Jitter = opts.Jitter,
        JitterAmount = Units.ToDocUnits(opts.JitterAmount, opts.HatchUnits, opts.Unit),
        JitterSeed = opts.JitterSeed,
        ConnectEnds = opts.ConnectEnds,
        HatchScope = opts.HatchScope,
        Inset = opts.Inset,
        AutoClose = opts.AutoClose,
        InsetDistance = Units.ToDocUnits(opts.InsetDist, opts.HatchUnits, opts.Unit),
        Tolerance = opts.HatchTolerance,
        MaxParallelism = opts.MaxParallelism,
        // Tone: the raster the shading fills read, when one was supplied. Null leaves them on the
        // shape's own boundary tone.
        ToneImage = opts.ToneImage,
        ToneInvert = opts.ToneInvert,
        ToneThreshold = opts.ToneThreshold,
        ToneDepth = opts.ToneDepth,
    };

    internal static (SvgDocument, HatchOptions) LoadDocument(Options opts)
    {
        // Build the root transform on top of element transforms.
        // Bed dims: if supplied, use them; only center/top-left origins need the canvas
        // dims (for the transform), so read them cheaply (CanvasSize, no curve building).
        // The default bottom-left origin doesn't use the dims, so we skip the read and
        // parse once (canvasHeight=null => ParseRoot uses the SVG's own height).
        // Previously this always did a full throwaway probe parse just for the size.
        bool bedSet = opts.BedWidth > 0 && opts.BedHeight > 0;
        double bedW, bedH;
        if (bedSet) { bedW = opts.BedWidth!.Value; bedH = opts.BedHeight!.Value; }
        else if (opts.MachineOrigin is "center" or "top-left")
        {
            var (canvasW, canvasH) = SvgParser.CanvasSize(opts.Input);
            bedW = (opts.BedWidth > 0) ? opts.BedWidth.Value : canvasW;
            bedH = (opts.BedHeight > 0) ? opts.BedHeight.Value : canvasH;
        }
        else { bedW = 0; bedH = 0; }   // bottom-left, bed unset: transform ignores dims

        Affine root = Affine.Identity;
        root = root.Compose(Affine.Translation(opts.HorizontalOffset, opts.VerticalOffset));
        root = root.Compose(Affine.Scale(opts.ScalingFactor));
        if (opts.MachineOrigin == "center")
            root = root.Compose(Affine.Translation(-bedW / 2.0, bedH / 2.0));
        else if (opts.MachineOrigin == "top-left")
            root = root.Compose(Affine.Translation(0, bedH));

        bool transformOrigin = !opts.InvertY; // flip SVG y-down to cartesian y-up unless --invert-y
        // canvasHeight: supplied bed wins; otherwise let ParseRoot use the SVG height.
        double? canvasHeight = bedSet ? bedH : null;
        var doc = SvgParser.ParseFile(opts.Input, handleViewBox: opts.HandleViewBox,
            rootTransform: root, canvasHeight: canvasHeight, transformOrigin: transformOrigin);

        var hatchOpts = BuildHatchOptions(opts);
        return (doc, hatchOpts);
    }

    internal static GcodeCompiler BuildCompiler(Options opts)
    {
        var iface = new CustomGcodeInterface(opts.ToolOff, opts.ToolPower);

        var header = new List<string>();
        var footer = new List<string>();
        // Pasted literal header/footer text takes precedence over file paths.
        if (!string.IsNullOrWhiteSpace(opts.HeaderText)) header.AddRange(SplitLines(opts.HeaderText));
        else if (opts.HeaderPath != null && File.Exists(opts.HeaderPath)) header.AddRange(File.ReadAllLines(opts.HeaderPath));
        if (!string.IsNullOrWhiteSpace(opts.FooterText)) footer.AddRange(SplitLines(opts.FooterText));
        else if (opts.FooterPath != null && File.Exists(opts.FooterPath)) footer.AddRange(File.ReadAllLines(opts.FooterPath));

        if (opts.DoToolOffStart) header.Add(iface.ToolOff());
        if (opts.DoToolOffEnd) footer.Add(iface.ToolOff());
        header.Add(iface.SetMovementSpeed(opts.TravelSpeed));
        if (opts.DoZAxisStart) header.Add(iface.LinearMove(null, null, opts.ZAxisStart));
        if (opts.ReturnHomeEnd) footer.Add(iface.HomeAxes());

        return new GcodeCompiler(iface, opts.TravelSpeed, opts.CuttingSpeed, opts.PassDepth,
            dwellTime: opts.DwellTime, dwellAfterPenUp: opts.DwellAfterPenUp, unit: opts.Unit, customHeader: header, customFooter: footer,
            approximationTolerance: opts.FlatteningTolerance);
    }

    /// <summary>Split pasted G-code text into lines, normalizing CR/LF. Empty lines are kept (the compiler skips them).</summary>
    internal static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\\n", "\n").Split('\n');
}