using BenchmarkDotNet.Attributes;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Bench;

/// <summary>
/// The fills that walk the shape's distance field: concentric rings and the contour spiral that joins them.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="PipelineBench"/> because these do their own geometry from scratch rather than
/// going through the hatch grid, and because they are the only fills whose cost grows with the number of levels
/// rather than with the number of hatch lines.
/// </remarks>
[MemoryDiagnoser]
public class FieldBench
{
    private List<SvgPath> _drawing = null!;
    private List<SvgPath> _star = null!;

    [GlobalSetup]
    public void Setup()
    {
        _drawing = Load(Path.Combine(AppContext.BaseDirectory, "assets", "maze.svg"));
        _star = Load(Path.Combine(AppContext.BaseDirectory, "assets", "star.svg"));
    }

    private static List<SvgPath> Load(string path) =>
        SvgParser.ParseString(File.ReadAllText(path), handleViewBox: true, canvasHeight: 297,
            transformOrigin: true).Paths.ToList();

    private static HatchOptions Options(FillStyle style, double spacing) => new()
    {
        FillStyle = style,
        HatchSpacing = spacing,
        Inset = true,
        InsetDistance = 0.5,
    };

    [Benchmark]
    public int Concentric_Drawing_Fine() =>
        HatchEngine.Generate(_drawing, Options(FillStyle.Concentric, 1.25)).Sum(g => g.Paths.Count);

    [Benchmark]
    public int Concentric_Drawing_Coarse() =>
        HatchEngine.Generate(_drawing, Options(FillStyle.Concentric, 5)).Sum(g => g.Paths.Count);

    [Benchmark]
    public int ContourSpiral_Drawing_Fine() =>
        HatchEngine.Generate(_drawing, Options(FillStyle.ContourSpiral, 1.25)).Sum(g => g.Paths.Count);

    [Benchmark]
    public int Concentric_Star() =>
        HatchEngine.Generate(_star, Options(FillStyle.Concentric, 5)).Sum(g => g.Paths.Count);
}
