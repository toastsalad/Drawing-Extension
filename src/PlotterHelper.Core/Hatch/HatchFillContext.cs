using PlotterHelper.Geometry;

namespace PlotterHelper.Hatch;

/// <summary>
/// What one element's fill gets: the polygons to fill, the options that say how, and the path list to
/// append the result to. Holds no state between elements.
/// </summary>
internal sealed class HatchFillContext
{
    private readonly Func<IToneField> _toneBuilder;
    private IToneField? _tone;

    public HatchFillContext(
        List<HatchPath> paths,
        List<List<Vector>> polygons,
        HatchBBox bbox,
        HatchOptions options,
        double flattenTolerance,
        Func<IToneField> toneBuilder)
    {
        Paths = paths;
        Polygons = polygons;
        BBox = bbox;
        Options = options;
        FlattenTolerance = flattenTolerance;
        _toneBuilder = toneBuilder;
    }

    /// <summary>Output: the fill appends its paths here.</summary>
    public List<HatchPath> Paths { get; }

    /// <summary>Flattened closed subpaths of the element (holes via even-odd).</summary>
    public List<List<Vector>> Polygons { get; }

    public HatchBBox BBox { get; }

    public HatchOptions Options { get; }

    /// <summary>Flattening tolerance from <see cref="HatchOptions.Tolerance"/> (already divided by 100).</summary>
    public double FlattenTolerance { get; }

    /// <summary>
    /// Darkness over this element, 0 to 1. Built on first use: the shape-boundary field costs a raster
    /// pass over the outline, and the fills that do not shade should not pay for it.
    /// </summary>
    public IToneField Tone => _tone ??= _toneBuilder();
}
