using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// Something that <em>grows</em> paths over a region rather than filling it with a clipped pattern.
/// </summary>
/// <remarks>
/// The distinction is not cosmetic. A fill is handed polygons and a spacing and returns a pattern cut to
/// the outline, so its whole contract is "cover the region". A generator is handed the region as something
/// it can ask questions of — am I still inside, how dark is it here — plus a seeded random source, and
/// returns whatever its rule produced; it is allowed to leave paper bare, and it is allowed to decide
/// where to put the next mark from where the last one landed.
///
/// That is why the stipple belongs here and the hatching does not: a stipple decides each mark
/// independently from the tone, whereas every line fill can be described as a set of curves clipped to
/// the shape. Trying to express a generator through <see cref="IHatchFill"/> would leave it holding
/// polygons it never uses.
/// </remarks>
internal interface IGenerator
{
    void Generate(GeneratorContext ctx);
}

/// <summary>What a generator gets: the region to work inside, the options, and a seeded random source.</summary>
internal sealed class GeneratorContext
{
    private const double Scale = HatchGeometry.Scale;
    private readonly EvenOddRegion _region;
    private readonly EvenOddRegion.Query _query;

    internal GeneratorContext(List<HatchPath> paths, EvenOddRegion region, HatchBBox bbox,
        HatchOptions options, IToneField tone)
    {
        Paths = paths;
        _region = region;
        _query = region.NewQuery();
        BBox = bbox;
        Options = options;
        Tone = tone;
        Random = new Random(options.JitterSeed);
    }

    /// <summary>Output: the generator appends its paths here.</summary>
    internal List<HatchPath> Paths { get; }

    internal HatchBBox BBox { get; }

    internal HatchOptions Options { get; }

    /// <summary>Darkness at a point, 0 to 1.</summary>
    internal IToneField Tone { get; }

    /// <summary>Seeded from the options, so a generator produces the same drawing every time.</summary>
    internal Random Random { get; }

    /// <summary>The shape, inset as requested: is this point on the paper the fill may use?</summary>
    internal bool Inside(Vector point) => _query.Contains(new Vector(point.X * Scale, point.Y * Scale));

    /// <summary>The hatch spacing in drawing units, which is the natural step for a walk.</summary>
    internal double Spacing => Math.Max(0.1, Math.Abs(Options.HatchSpacing));

    /// <summary>
    /// True when a raster is supplying the tone.
    /// </summary>
    /// <remarks>
    /// Generators use this to decide whether the tone should decide <em>where</em> marks go. On a shape with
    /// no image behind it the only tone is closeness to the outline, and gating on that fills nothing but a
    /// rim: a scribble or a stipple asked for on a shape means the whole shape.
    /// </remarks>
    internal bool HasToneImage => Options.ToneImage is not null;

    /// <summary>The tone threshold to gate marks by: only an image makes it meaningful.</summary>
    internal double SeedThreshold =>
        HasToneImage ? Math.Clamp(Options.ToneThreshold, 0, 1) : 0.0;

    /// <summary>
    /// How much ink this point asks for, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Without an image there is no tone to shade by, so the answer is "all of it": a stipple or a scribble
    /// asked for on a shape means the whole shape, not a rim of it. With an image the tone is rescaled above
    /// the threshold, so mid-grey asks for half the marks rather than none.
    /// </remarks>
    internal double InkLevel(Vector point) =>
        HasToneImage ? HatchGeometry.Level(Tone.Sample(point), SeedThreshold) : 1.0;

    /// <summary>Should a mark be placed at this point, given how much ink the tone asks for there?</summary>
    internal bool WantsMark(Vector point, double cell) =>
        InkLevel(point) > HatchGeometry.Dither(point, cell, (uint)Options.JitterSeed);
}
