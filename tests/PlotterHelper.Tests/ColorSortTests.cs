using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class ColorSortTests
{
    private static SvgPath P(string? stroke) => new() { Stroke = stroke };

    [Fact]
    public void Groups_By_Color_In_FirstAppearance_Order()
    {
        var groups = ColorSort.GroupByColor(new[] { P("red"), P("blue"), P("red"), P("green") });
        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[0].Count); // red appears twice
        Assert.Single(groups[1]); // blue
        Assert.Single(groups[2]); // green
        Assert.Equal("red", groups[0][0].Stroke);
    }

    [Fact]
    public void Normalizes_Missing_Color_To_Default()
    {
        Assert.Equal(ColorSort.DefaultKey, ColorSort.NormalizeColor(null));
        Assert.Equal(ColorSort.DefaultKey, ColorSort.NormalizeColor(""));
        Assert.Equal(ColorSort.DefaultKey, ColorSort.NormalizeColor("  "));
        Assert.Equal(ColorSort.DefaultKey, ColorSort.NormalizeColor("none"));
        Assert.Equal(ColorSort.DefaultKey, ColorSort.NormalizeColor("currentColor"));
        Assert.Equal("#ff0000", ColorSort.NormalizeColor("#ff0000"));
    }

    [Fact]
    public void Grouping_Is_Case_Insensitive()
    {
        var groups = ColorSort.GroupByColor(new[] { P("#FF0000"), P("#ff0000") });
        Assert.Single(groups);       // same color despite case difference
        Assert.Equal(2, groups[0].Count);
    }
}
