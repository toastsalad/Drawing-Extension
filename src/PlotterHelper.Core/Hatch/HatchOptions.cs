using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Trace;

namespace PlotterHelper.Hatch;

/// <summary>The pen-fill strategy used to fill a closed region.</summary>
public enum FillStyle
{
    /// <summary>Parallel straight hatch lines (axidraw/eggbot_hatch behavior).</summary>
    Parallel,

    /// <summary>Parallel hatch lines bowed into sine waves (amplitude/wavelength).</summary>
    Wave,

    /// <summary>Nested inset copies of the shape (offset rings). Phase 2.</summary>
    Concentric,

    /// <summary>A single Archimedean spiral clipped to the shape. Phase 2.</summary>
    Spiral,

    /// <summary>A Hilbert space-filling curve clipped to the shape. Phase 2.</summary>
    Hilbert,

    /// <summary>Parallel lines joined end to end into back-and-forth strokes (lawnmower infill), so
    /// the pen lifts only where a hole forces it.</summary>
    Serpentine,

    /// <summary>A single Fermat spiral (r = a·√θ) clipped to the shape: one continuous stroke whose
    /// turns crowd toward the centre.</summary>
    Fermat,

    /// <summary>Two hatch screens at slightly different pitch and angle, so they beat into broad
    /// bands: a moiré fill.</summary>
    Moire,

    /// <summary>A Peano space-filling curve clipped to the shape: the base-3 cousin of Hilbert, coarser
    /// texture, same single uninterrupted stroke.</summary>
    Peano,

    /// <summary>Layered hatch families at increasing angles, each drawn only where the shape is dark
    /// enough, darkness being closeness to the boundary.</summary>
    Engrave,

    /// <summary>Nested inset rings joined into one continuous stroke: the shape's own contour followed
    /// without a pen lift.</summary>
    ContourSpiral,

    /// <summary>Three hatch families at 60 degree steps: the engraver's cross-hatch.</summary>
    HexLattice,

    /// <summary>A square spiral: straight runs on a square lattice, growing as it winds out.</summary>
    SquareSpiral,

    /// <summary>A grid of quarter-arc tiles, randomly oriented: flowing curls with no straight lines.</summary>
    Truchet,

    /// <summary>The Gosper curve (flowsnake): a space-filling curve on a hexagonal lattice.</summary>
    Gosper,

    /// <summary>The dragon curve: paper-folded, denser and more organic than the grid curves.</summary>
    Dragon,

    /// <summary>Single hatch family at half spacing, inked only where the image is dark: photographic tone.</summary>
    Shade,

    /// <summary>Dots on a jittered lattice, denser where the image is dark: stippling rather than lines.</summary>
    Stipple,

    /// <summary>A drifting random walk, one continuous stroke per seed, denser where the image is dark.</summary>
    Scribble,
}

/// <summary>Configuration for hatch generation. Distances are in SVG user units (px @ 96dpi).</summary>
public sealed class HatchOptions
{
    /// <summary>The fill strategy.</summary>
    public FillStyle FillStyle { get; set; } = FillStyle.Parallel;

    /// <summary>Distance between parallel hatch lines (also the spiral/ring spacing for Concentric/Spiral).</summary>
    public double HatchSpacing { get; set; } = 3.0;

    /// <summary>Hatch line angle in degrees from horizontal (90 = vertical).</summary>
    public double HatchAngle { get; set; } = 45;

    /// <summary>Add a second set of hatches perpendicular to the first.</summary>
    public bool CrossHatch { get; set; }

    /// <summary>Sine-wave amplitude for <see cref="FillStyle.Wave"/> (user units, 0 = straight).</summary>
    public double WaveAmplitude { get; set; } = 1.0;

    /// <summary>Sine-wave wavelength for <see cref="FillStyle.Wave"/> (user units; 0 = 2× spacing).</summary>
    public double WaveWavelength { get; set; }

    /// <summary>Apply seeded per-vertex jitter to any fill style for a hand-drawn look.</summary>
    public bool Jitter { get; set; }

    /// <summary>Standard deviation of the Gaussian jitter, in user units.</summary>
    public double JitterAmount { get; set; } = 0.3;

    /// <summary>Seed for the deterministic jitter RNG (same seed => same output).</summary>
    public int JitterSeed { get; set; } = 12345;

    // ---- tone (the image-driven fills) ---------------------------------------------

    /// <summary>
    /// The raster the tone-driven fills shade by, mapped onto the element's bounding box. Null means the
    /// shape has no image behind it, and those fills fall backto the shape's own boundary tone.
    /// </summary>
    public GrayImage? ToneImage { get; set; }

    /// <summary>Treat light pixels as ink instead of dark ones (white-on-black artwork).</summary>
    public bool ToneInvert { get; set; }

    /// <summary>How dark a point must be before an image-driven fill inks it.</summary>
    /// <summary>
    /// Tone at or below which a point is treated as bare paper, 0..1.
    /// </summary>
    /// <remarks>
    /// This is a paper floor, not a cutoff. Everything above it is inked in proportion to its tone, so a
    /// gradient fades from nothing to solid instead of switching on at mid-grey. It used to default to 0.5,
    /// which meant a photograph's mid-tones were dropped outright and the shading stopped at a hard edge.
    /// </remarks>
    public double ToneThreshold { get; set; } = 0.05;

    /// <summary>How far the shape's own boundary tone reaches, as a multiple of the hatch spacing.</summary>
    public double ToneDepth { get; set; } = 6.0;

    /// <summary>Join nearby line ends with Bézier curves to reduce pen lifts (Parallel style only).</summary>
    public bool ConnectEnds { get; set; } = true;

    /// <summary>Range (in hatch spacings) over which end connections may be made.</summary>
    public double HatchScope { get; set; } = 3.0;

    /// <summary>Hold hatch ends back from the polygon boundary ("inset").</summary>
    public bool Inset { get; set; } = true;

    /// <summary>Treat subpaths as closed for hatching even when their first and last
    /// points don't coincide, connecting the gap (SVGs from many editors draw shapes
    /// as open paths, which would otherwise never be filled).</summary>
    public bool AutoClose { get; set; }

    /// <summary>How far to hold back from the boundary.</summary>
    public double InsetDistance { get; set; } = 0.5;

    /// <summary>Hatch-input flattening tolerance in user units (raw value; divided by 100
    /// internally to match the original), used only when building hatch polygons. This is
    /// not the outline flattening tolerance from --approximation-tolerance in the CLI; do not
    /// merge them.</summary>
    public double Tolerance { get; set; } = 2.0;

    /// <summary>Maximum degree of parallelism for the per-element / per-hatch-line work.</summary>
    public int MaxParallelism { get; set; } = -1;
}

/// <summary>A single path operation in a hatch fill: a line or a cubic Bézier join.</summary>
public abstract record HatchOp;

public sealed record LineTo(Vector P) : HatchOp;

public sealed record CubicTo(Vector C1, Vector C2, Vector P) : HatchOp;

/// <summary>A pen path: a start point followed by a sequence of line/cubic ops.</summary>
public sealed class HatchPath
{
    public Vector Start;
    public List<HatchOp> Ops { get; } = new();
}

/// <summary>The hatch fill for one source SVG element.</summary>
public sealed class HatchGroup
{
    public SvgPath Source { get; init; } = null!;
    public List<HatchPath> Paths { get; } = new();
}
