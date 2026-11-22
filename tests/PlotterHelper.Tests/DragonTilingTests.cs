using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Tests;

/// <summary>
/// The dragon fill covers the shape, rather than drawing one curve across the middle of it.
/// </summary>
/// <remarks>
/// A dragon occupies about half of its own bounding box and the curve only visits part of that, so a single
/// curve fitted to the shape left the corners bare: on a 80x50 rectangle it inked about a third of the area.
/// It is a rep-tile, so the fill now lays copies out in a lattice -- mirrored every other column and row, which
/// covers the box without overlapping -- and clips them.
///
/// These check the two ways that can go wrong. Too few copies and the corners stay empty; too many, or turned
/// a quarter and packed at half pitch, and the same ground is inked twice, which costs plot time and makes the
/// tone uneven.
/// </remarks>
public class DragonTilingTests
{
    private static SvgDocument Rectangle() => SvgParser.ParseString(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"70\" viewBox=\"0 0 100 70\">" +
        "<rect x=\"10\" y=\"10\" width=\"80\" height=\"50\"/></svg>",
        handleViewBox: false, canvasHeight: 70, transformOrigin: true);

    private static List<HatchPath> Fill(double spacing) => HatchEngine.Generate(Rectangle().Paths, new HatchOptions
    {
        FillStyle = FillStyle.Dragon,
        HatchSpacing = spacing,
        Inset = false,
    })[0].Paths;

    private static (double Ink, List<Vector> Points) Measure(double spacing)
    {
        var paths = Fill(spacing);
        var points = new List<Vector>();
        double ink = 0;

        foreach (var path in paths)
        {
            var run = new List<Vector> { path.Start };
            foreach (var op in path.Ops) if (op is LineTo line) run.Add(line.P);
            for (int i = 1; i < run.Count; i++) ink += (run[i] - run[i - 1]).Length;
            points.AddRange(run);
        }

        return (ink, points);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void It_Inks_The_Whole_Rectangle_Not_A_Band_Across_It(double spacing)
    {
        var (_, points) = Measure(spacing);

        // Every quarter of the rectangle has ink in it. One curve across the middle leaves two of them empty.
        double midX = 50, midY = 35;
        var quadrants = new bool[4];
        foreach (var point in points)
        {
            int quadrant = (point.X > midX ? 1 : 0) + (point.Y > midY ? 2 : 0);
            quadrants[quadrant] = true;
        }

        Assert.All(quadrants, filled => Assert.True(filled, "a quarter of the shape was left empty"));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void It_Lays_Down_About_One_Strand_Per_Spacing(double spacing)
    {
        var (ink, _) = Measure(spacing);

        // A fill at spacing s puts about one strand per width s over the area, so ink per square millimetre
        // should be near 1/s. Overlapping copies show up immediately as a multiple of it.
        double ideal = 1.0 / spacing;
        double actual = ink / (80 * 50);

        Assert.True(actual > ideal * 0.5,
            $"too little ink for a fill: {actual:F2}/mm2 against {ideal:F2} wanted");
        Assert.True(actual < ideal * 1.6,
            $"the copies are overlapping: {actual:F2}/mm2 against {ideal:F2} wanted");
    }
}
