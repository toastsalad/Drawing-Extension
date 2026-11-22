using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class SvgColorTests
{
    [Theory]
    [InlineData("#FF0000", "FF0000")]
    [InlineData("#f00", "FF0000")]
    [InlineData("red", "FF0000")]
    [InlineData("rgb(255,0,0)", "FF0000")]
    [InlineData("rgb(100%,0%,0%)", "FF0000")]
    [InlineData("#3a7C", "33AA77")]
    [InlineData("blue", "0000FF")]
    [InlineData("#abcdef", "ABCDEF")]
    [InlineData("transparent", null)]
    [InlineData("none", null)]
    [InlineData("currentColor", null)]
    [InlineData("url(#g1)", null)]
    [InlineData("", null)]
    public void ToHex_Normalizes(string input, string? expected)
    {
        Assert.Equal(expected, SvgColor.ToHex(input));
    }
}
