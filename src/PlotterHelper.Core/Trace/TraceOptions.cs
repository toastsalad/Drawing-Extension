namespace PlotterHelper.Trace;

/// <summary>
/// Raster tracer settings. Every length is in source image pixels; the upscale factor is applied
/// internally, so one value keeps working from a thumbnail to a scan.
/// </summary>
public sealed class TraceOptions
{
    /// <summary>Trace at this multiple of the source resolution; 2-4 is the useful range.</summary>
    public int Upscale = 3;

    /// <summary>Thread cap; 0 or less means every core. The pixel stages give identical results
    /// at any count; the skeleton walk and graph surgery stay sequential.</summary>
    public int MaxParallelism = -1;

    /// <summary>Ceiling on the working raster, in pixels; the effective upscale is reduced to fit,
    /// never below 1x. 0 removes the ceiling.</summary>
    public int MaxPixels = 8_000_000;

    /// <summary>Adaptive threshold window in source px; must stay smaller than the gap between
    /// adjacent lines or dense hatching welds into a slab.</summary>
    public int ThresholdWindow = 21;

    /// <summary>Sauvola sensitivity; higher picks up fainter ink and more noise.</summary>
    public double ThresholdK = 0.2;

    /// <summary>When &gt; 0, use this fixed 0-255 cutoff instead of the adaptive threshold.</summary>
    public double GlobalThreshold;

    /// <summary>Trace white ink on black instead.</summary>
    public bool Invert;

    /// <summary>Ink blobs smaller than this area (source px^2) are noise.</summary>
    public double MinComponentArea = 4.0;

    /// <summary>Drop dead-end branches shorter than this (source px).</summary>
    public double MinSpur = 3.0;

    /// <summary>Also drop branches shorter than this multiple of the local ink half-width.</summary>
    public double SpurRatio = 1.8;

    /// <summary>A branch whose tip is this many times narrower than its base is a taper, not a mark.</summary>
    public double SpurTaper = 1.5;

    /// <summary>Fuse junction nodes within this multiple of the local ink half-width; 0 disables.</summary>
    public double JunctionMerge = 2.0;

    /// <summary>Max deviation in degrees between two branches still spliced into one stroke;
    /// this is what makes a line crossing another come out whole. 0 disables.</summary>
    public double JunctionAngle = 45.0;

    /// <summary>Bridge free stroke ends across a gap up to this wide (source px); 0 disables.</summary>
    public double JoinGap = 2.0;

    /// <summary>Max deviation in degrees from a straight join.</summary>
    public double JoinAngle = 40.0;

    /// <summary>Douglas-Peucker tolerance in source px.</summary>
    public double Simplify = 0.4;

    /// <summary>Maximum Bézier fit deviation in source px.</summary>
    public double FitError = 0.5;

    /// <summary>Moving-average window used to smooth the skeleton before fitting; 0 disables.</summary>
    public int Smooth = 1;

    /// <summary>Ink wider than this (source px) becomes a closed region for hatching instead of a
    /// centerline; 0 traces everything as centerlines.</summary>
    public double ThickWidth = 5.0;

    /// <summary>Drop traced strokes shorter than this (source px).</summary>
    public double MinLength = 2.0;

    /// <summary>Output width, in <see cref="OutputUnits"/>.</summary>
    public double TargetWidth = 200;

    /// <summary>Output height; 0 keeps the source aspect ratio.</summary>
    public double TargetHeight;

    /// <summary>mm, in or px; anything else falls back to mm.</summary>
    public string OutputUnits = "mm";

    public TraceOptions Clone() => (TraceOptions)MemberwiseClone();
}
