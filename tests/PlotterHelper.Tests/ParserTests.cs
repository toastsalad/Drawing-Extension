using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class ParserTests
{
    private static SvgDocument Parse(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false, canvasHeight: 200, transformOrigin: true);

    [Fact]
    public void Parses_Line_H_V_Z()
    {
        var doc = Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
                        "<path d=\"M0 0 L 100 100 H 150 V 150 Z\"/></svg>");
        Assert.Single(doc.Paths);
        // 4 line curves: L, H, V, Z(close)
        Assert.Equal(4, doc.Paths[0].Curves.Count);
    }

    [Fact]
    public void Relative_Move_Then_Implicit_LineTo()
    {
        var doc = Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
                        "<path d=\"m 0,100 100,100 50,0\"/></svg>");
        // m then two implicit l -> 2 line curves
        Assert.Equal(2, doc.Paths[0].Curves.Count);
    }

    [Fact]
    public void Numbers_Without_Delimiters_Tokenize()
    {
        // "1.5.5" -> 1.5, 0.5 ; "5-3" -> 5, -3
        var doc = Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
                        "<path d=\"M0 0L1.5.5 5-3\"/></svg>");
        // L with two pairs -> 2 lines
        Assert.Equal(2, doc.Paths[0].Curves.Count);
    }

    [Fact]
    public void Rect_And_Circle_Convert_To_Paths()
    {
        var doc = Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
                        "<rect x=\"10\" y=\"10\" width=\"40\" height=\"30\"/>" +
                        "<circle cx=\"100\" cy=\"100\" r=\"20\"/></svg>");
        Assert.Equal(2, doc.Paths.Count);
        // rect -> 4 line curves (closed); circle -> 2 arc curves
        Assert.Equal(4, doc.Paths[0].Curves.Count);
        Assert.Equal(2, doc.Paths[1].Curves.Count);
    }

    [Fact]
    public void Group_Transform_Is_Composed()
    {
        var doc = SvgParser.ParseString(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
            "<g transform=\"translate(10,0)\"><path d=\"M0 0 L10 0\"/></g></svg>",
            handleViewBox: false, canvasHeight: 200, transformOrigin: true);
        // After translate(10,0) and the y-flip (height 200): (0,0)->(10,200), (10,0)->(20,200)
        var line = (Line)doc.Paths[0].Curves[0];
        Assert.Equal(10, line.Start.X, 6);
        Assert.Equal(200, line.Start.Y, 6);
        Assert.Equal(20, line.End.X, 6);
    }
}
