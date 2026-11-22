namespace PlotterHelper.Cli;

/// <summary>
/// Option combinations that cannot do anything.
/// </summary>
/// <remarks>
/// Several flags only have an effect while another one is on: <c>--pause-between-colors</c> without
/// <c>--group-by-color</c>, <c>--connect-outlines</c> without <c>--optimize-travel</c>,
/// <c>--cross-hatch</c> outside a hatch mode, <c>--trace-svg</c> without <c>--trace</c>, and the
/// tiling switches without a tile size. Nothing rejected them and nothing warned either, so the
/// flag was silently dropped. That is how a panel can offer a control that does nothing and leave
/// the operator looking for the fault in their drawing.
///
/// <see cref="Normalize"/> clears each such flag and returns one line per flag it cleared. Clearing
/// changes no output, because the flag had no effect anyway; it makes the state say what it means,
/// so code further down cannot honour half of a combination.
///
/// Only flags whose default is off can be checked this way. A flag that defaults on, such as
/// <see cref="Options.OptimizeTravelReverse"/>, cannot be told apart from a flag the caller never
/// mentioned.
/// </remarks>
internal static class OptionDependencies
{
    /// <summary>One flag, the test for whether it was asked for, and the condition that gives it meaning.</summary>
    internal sealed record Dependency(
        string Flag,
        Func<Options, bool> IsSet,
        string Needs,
        Func<Options, bool> IsEnabled,
        Action<Options> Clear);

    private static bool HatchMode(Options opts) => opts.Mode != Mode.Cut;

    // Tiling runs only when both sides are set, so a single width does not enable it either.
    private static bool Tiling(Options opts) => opts.TileWidth > 0 && opts.TileHeight > 0;

    internal static readonly IReadOnlyList<Dependency> All =
    [
        new("--pause-between-colors", opts => opts.PauseBetweenColors,
            "--group-by-color", opts => opts.GroupByColor, opts => opts.PauseBetweenColors = false),
        new("--connect-outlines", opts => opts.ConnectOutlines,
            "--optimize-travel", opts => opts.OptimizeTravel, opts => opts.ConnectOutlines = false),
        new("--cross-hatch", opts => opts.CrossHatch,
            "--mode hatch or --mode hatchcut", HatchMode, opts => opts.CrossHatch = false),
        new("--trace-svg", opts => !string.IsNullOrEmpty(opts.TraceSvg),
            "--trace", opts => opts.Trace, opts => opts.TraceSvg = null),
        new("--tile-alignment-marks", opts => opts.TileAlignmentMarks,
            "--tile-width and --tile-height", Tiling, opts => opts.TileAlignmentMarks = false),
        new("--tile-overlap", opts => opts.TileOverlap > 0,
            "--tile-width and --tile-height", Tiling, opts => opts.TileOverlap = 0),
    ];

    /// <summary>Clear every flag that has no meaning in this combination and say which ones went.</summary>
    internal static IReadOnlyList<string> Normalize(Options opts)
    {
        var notes = new List<string>();
        foreach (var dependency in All)
        {
            if (!dependency.IsSet(opts)) continue;
            if (dependency.IsEnabled(opts)) continue;
            notes.Add($"{dependency.Flag} has no effect without {dependency.Needs}, so it was ignored");
            dependency.Clear(opts);
        }
        return notes;
    }
}
