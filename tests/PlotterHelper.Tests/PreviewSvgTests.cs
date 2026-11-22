using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class PreviewSvgTests
{
    private static SvgDocument Doc(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false, canvasHeight: 120, transformOrigin: true);

    [Fact]
    public void Preview_Emits_Path_Elements_And_Is_Valid_Xml()
    {
        // Regression: PreviewSvg.Write used to append bare path data ("M ... L ...")
        // straight into the parent <g> instead of wrapping it in <path d="..."/>,
        // so every renderer ignored it and the preview was blank.
        var doc = Doc("<svg xmlns='http://www.w3.org/2000/svg' width='100' height='120'>" +
                      "<rect x='10' y='10' width='40' height='40' stroke='red' fill='red'/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });

        var svg = Cli.Output.PreviewSvg.Write(doc, groups, showOutlines: true, 0.01);

        Assert.StartsWith("<?xml", svg);
        Assert.Contains("<path d=\"", svg); // outlines + hatches are real path elements
        Assert.Contains("M 10 110 L 50 110", svg); // the red rect outline, y-flipped
        // Well-formed XML (parses without throwing).
        _ = System.Xml.Linq.XDocument.Parse(svg);
    }
}
