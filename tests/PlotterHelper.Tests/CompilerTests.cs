using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class CompilerTests
{
    [Fact]
    public void Emits_Header_Footer_And_PassSeparators()
    {
        var iface = new GcodeInterface();
        var compiler = new GcodeCompiler(iface, movementSpeed: 1000, cuttingSpeed: 300, passDepth: 2);
        compiler.AppendCurves(new[] { new Line(new(0, 0), new(10, 0)) });
        var gcode = compiler.Compile(passes: 2);
        var lines = gcode.Split('\n');
        Assert.Equal("G90;", lines[0]);
        Assert.Contains("M5;", lines);
        Assert.Contains("G1 F1000 X0.000000 Y0.000000;", lines);
        Assert.Contains("M3 S255;", lines);
        // Pass separator: relative move down by pass depth.
        Assert.Contains("G91;", lines);
        Assert.Contains("G1 Z-2.000000;", lines);
        Assert.Contains("G90;", lines);
        Assert.False(gcode.EndsWith('\n'), "no trailing newline (matches svg_to_gcode)");
    }

    [Fact]
    public void Dwell_Is_Placed_After_Tool_Down_At_New_Start()
    {
        // Two disjoint chains: the second triggers a lift -> travel -> pen-down ->
        // dwell -> first cut. The dwell must follow the tool-on (M3), not precede
        // the travel (the old svg_to_gcode placement dwelled at the previous end).
        var iface = new GcodeInterface();
        var compiler = new GcodeCompiler(iface, movementSpeed: 1000, cuttingSpeed: 300, passDepth: 0, dwellTime: 500);
        var a = new LineSegmentChain(); a.Append(new(0, 0)); a.Append(new(10, 0));
        var b = new LineSegmentChain(); b.Append(new(20, 20)); b.Append(new(30, 30));
        compiler.AppendLineChain(a);
        compiler.AppendLineChain(b);
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();
        int g4 = lines.FindIndex(l => l.StartsWith("G4 P"));
        Assert.True(g4 > 0, "no dwell emitted");
        // The line immediately before the dwell is the tool-on (M3 ...).
        Assert.StartsWith("M3 ", lines[g4 - 1]);
        // The line immediately after the dwell is the first cutting move (G1 X.. Y..),
        // i.e. the dwell sits between pen-down and the cut, not before the travel.
        Assert.StartsWith("G1 ", lines[g4 + 1]);
    }

    [Fact]
    public void Dwell_Is_Placed_After_Pen_Up_Before_Travel()
    {
        // Two disjoint chains: the second triggers a lift -> dwell -> travel ->
        // pen-down -> cut. The pen-up dwell must follow the tool-off (M5) and
        // precede the first travel move.
        var iface = new GcodeInterface();
        var compiler = new GcodeCompiler(iface, movementSpeed: 1000, cuttingSpeed: 300, passDepth: 0, dwellAfterPenUp: 250);
        var a = new LineSegmentChain(); a.Append(new(0, 0)); a.Append(new(10, 0));
        var b = new LineSegmentChain(); b.Append(new(20, 20)); b.Append(new(30, 30));
        compiler.AppendLineChain(a);
        compiler.AppendLineChain(b);
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();
        int g4 = lines.FindIndex(l => l.StartsWith("G4 P"));
        Assert.True(g4 > 0, "no dwell emitted");
        // The line immediately before the dwell is the tool-off (M5).
        Assert.StartsWith("M5", lines[g4 - 1]);
        // The line immediately after the dwell is the travel move (G1 ... F<travel>).
        Assert.StartsWith("G1 ", lines[g4 + 1]);
    }

    [Fact]
    public void Custom_Interface_Uses_User_Commands()
    {
        var iface = new CustomGcodeInterface("M5;", "M3 S1000;");
        Assert.Equal("M5;", iface.ToolOff());
        Assert.Equal("M3 S1000;", iface.SetToolPower(1));
    }
    [Fact]
    public void DoublePass_Duplicates_Short_Closed_Subpath_Only()
    {
        static SvgPath PathOf(params Curve[] curves)
        {
            var p = new SvgPath();
            foreach (var c in curves) p.Curves.Add(c);
            return p;
        }
        // Tiny closed triangle, perimeter ~1.5, below the 2 threshold.
        var dot = PathOf(
            new Line(new(0, 0), new(0.5, 0)),
            new Line(new(0.5, 0), new(0.25, 0.433)),
            new Line(new(0.25, 0.433), new(0, 0)));
        Assert.Equal(3, dot.Curves.Count);
        Cli.Features.DoublePass.ApplyToPath(dot, 2, 0.01);
        Assert.Equal(6, dot.Curves.Count);              // duplicated -> 6

        // Same dot, threshold 1 (1.5 > 1) -> NOT duplicated.
        var dot2 = PathOf(
            new Line(new(0, 0), new(0.5, 0)),
            new Line(new(0.5, 0), new(0.25, 0.433)),
            new Line(new(0.25, 0.433), new(0, 0)));
        Cli.Features.DoublePass.ApplyToPath(dot2, 1, 0.01);
        Assert.Equal(3, dot2.Curves.Count);

        // Long closed triangle (perimeter ~5.7 > 2) -> NOT duplicated.
        var big = PathOf(
            new Line(new(0, 0), new(2, 0)),
            new Line(new(2, 0), new(1, 1.732)),
            new Line(new(1, 1.732), new(0, 0)));
        Cli.Features.DoublePass.ApplyToPath(big, 2, 0.01);
        Assert.Equal(3, big.Curves.Count);

        // Short but OPEN (does not return to start) -> NOT duplicated.
        var open = PathOf(
            new Line(new(0, 0), new(0.3, 0)),
            new Line(new(0.3, 0), new(0.3, 0.3)));
        Cli.Features.DoublePass.ApplyToPath(open, 2, 0.01);
        Assert.Equal(2, open.Curves.Count);
    }

    [Fact]
    public void DoublePass_Dot_Retraces_Continuously_Without_Lifting()
    {
        // A tiny SVG circle (two arcs) -- the real "dot" case. Threshold 2mm,
        // circumference ~1.26mm -> the whole subpath is duplicated, and the
        // per-curve emission retraces it as one continuous pen-down stroke.
        string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"50\" height=\"50\">" +
                     "<circle cx=\"10\" cy=\"10\" r=\"0.2\" fill=\"none\" stroke=\"black\"/></svg>";
        var doc = SvgParser.ParseString(svg, handleViewBox: false,
            canvasHeight: 50, transformOrigin: true);

        static int CuttingMoves(string gcode)
        {
            var lines = gcode.Split('\n').Where(x => x.Length > 0).ToList();
            int m3 = lines.FindIndex(x => x.StartsWith("M3"));
            int m5 = lines.FindIndex(m3 + 1, x => x.StartsWith("M5"));
            return lines.Skip(m3 + 1).Take(m5 - m3 - 1).Count(x => x.StartsWith("G1") && x.Contains('X'));
        }

        // Control: undoubled circle.
        var ctrl = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        ctrl.AppendCurves(doc.Paths[0].Curves);
        int controlMoves = CuttingMoves(ctrl.Compile(1));

        // Doubled: apply DoublePass, then compile.
        var doubled = new GcodeCompiler(new GcodeInterface(), 1000, 300, 0);
        Cli.Features.DoublePass.ApplyToPath(doc.Paths[0], 2, 0.01);
        doubled.AppendCurves(doc.Paths[0].Curves);
        var outGcode = doubled.Compile(1);
        Assert.Equal(2 * controlMoves, CuttingMoves(outGcode));   // traced twice
        // One pen-down for the whole dot (no lift between the two passes).
        Assert.Equal(1, outGcode.Split('\n').Count(l => l.StartsWith("M3")));
    }
}
