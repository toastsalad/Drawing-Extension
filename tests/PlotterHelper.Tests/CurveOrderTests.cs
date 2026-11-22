using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Hatch;
using PlotterHelper.Svg;

namespace PlotterHelper.Tests;

/// <summary>
/// The two curve fills whose expansion depth used to be a constant.
/// </summary>
/// <remarks>
/// Gosper and Dragon hardcoded their order while Hilbert and Peano derived theirs from the requested spacing.
/// The constant is right for a page and far too many for a small element: in the fill-picker swatch, an 88-unit
/// star, the whole curve was squeezed into a fraction of a spacing and both came out as solid black shapes,
/// which read as broken icons rather than as textures. The test is that spacing reaches them at all.
/// </remarks>
public class CurveOrderTests
{
    private static int Moves(FillStyle style, double spacing, double size)
    {
        var doc = SvgParser.ParseString(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{size}\" height=\"{size}\">" +
            $"<path d=\"M5,5 L{size - 5},5 L{size - 5},{size - 5} L5,{size - 5} Z\"/></svg>",
            handleViewBox: false, canvasHeight: size, transformOrigin: false);

        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions
        {
            FillStyle = style,
            HatchSpacing = spacing,
            Inset = false,
        });

        return groups.Sum(group => group.Paths.Sum(path => path.Ops.Count));
    }

    [Theory]
    [InlineData(FillStyle.Gosper)]
    [InlineData(FillStyle.Dragon)]
    public void A_Coarser_Spacing_Gives_A_Curve_With_Fewer_Steps(FillStyle style)
    {
        // A small element, where the hardcoded order used to swamp the spacing entirely.
        int fine = Moves(style, 2, 100);
        int coarse = Moves(style, 12, 100);

        Assert.True(fine > 0, $"{style}: drew nothing at a spacing of 2");
        Assert.True(coarse > 0, $"{style}: drew nothing at a spacing of 12");
        Assert.True(coarse < fine,
            $"{style}: the spacing should reach the curve order: fine={fine} steps, coarse={coarse} steps");
    }

    [Theory]
    [InlineData(FillStyle.Gosper)]
    [InlineData(FillStyle.Dragon)]
    public void A_Small_Shape_Does_Not_Get_A_Curve_Denser_Than_The_Whole_Area(FillStyle style)
    {
        // The swatch case: 88 units across at the plotting spacing. One step per cell of the grid means the
        // step count should stay in the order of (span/spacing)^2 rather than the thousands a page-scale
        // order produces.
        int steps = Moves(style, 7, 88);
        int cells = 12;                       // (88 / 7)^2, rounded
        Assert.True(steps < cells * 40,
            $"{style}: {steps} steps over a {cells}-cell shape is dense enough to read as a solid shape");
    }
}
