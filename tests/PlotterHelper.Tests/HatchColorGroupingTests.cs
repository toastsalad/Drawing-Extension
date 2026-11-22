using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class HatchColorGroupingTests
{
    private static SvgDocument Doc(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false, canvasHeight: 120, transformOrigin: true);

    private static Cli.Options Opts() => new()
    {
        Mode = Cli.Mode.Hatch,
        Unit = "mm",
        TravelSpeed = 1000,
        CuttingSpeed = 300,
        Passes = 1,
        PassDepth = 0,
        GroupByColor = true,
        PauseBetweenColors = true,
        PauseCommand = "M0",
        IncludeOutlines = true,
    };

    // Two disjoint squares: one red (stroke+fill), one blue (stroke+fill). With
    // --group-by-color the red outline and red hatch must run consecutively, then
    // a single M0 pen-swap pause, then the blue outline + blue hatch.
    private const string TwoColorSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='200' height='120'>" +
        "<rect x='10' y='10' width='40' height='40' stroke='red' fill='red'/>" +
        "<rect x='110' y='10' width='40' height='40' stroke='blue' fill='blue'/>" +
        "</svg>";

    [Fact]
    public void Red_Outline_And_Red_Hatch_Run_Together_Then_Pause_Then_Blue()
    {
        var doc = Doc(TwoColorSvg);
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });
        Assert.Equal(2, groups.Count); // both rects were filled

        var opts = Opts();
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        int redIdx = lines.FindIndex(l => l.Contains("color: red"));
        int blueIdx = lines.FindIndex(l => l.Contains("color: blue"));
        Assert.True(redIdx >= 0, "no red color group comment");
        Assert.True(blueIdx >= 0, "no blue color group comment");
        Assert.True(redIdx < blueIdx, "red group must come before blue group");

        // Exactly one pause (M0), and it sits between the two color groups -- never
        // inside one (that's the whole point: a red outline + red hatch share a pen).
        var m0Idx = lines.Where(l => l.Trim() == "M0").Select(_ => 1).Count();
        Assert.Equal(1, m0Idx);
        int m0Line = lines.FindIndex(l => l.Trim() == "M0");
        Assert.True(redIdx < m0Line && m0Line < blueIdx, "M0 must separate the red and blue groups");
    }

    [Fact]
    public void Matching_Outline_And_Hatch_Merge_Into_One_Color_Group()
    {
        // A single red-stroked, red-filled square: one color group, no pause at all.
        var doc = Doc("<svg xmlns='http://www.w3.org/2000/svg' width='100' height='120'>" +
                      "<rect x='10' y='10' width='40' height='40' stroke='red' fill='red'/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });

        var opts = Opts();
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        Assert.Single(lines, l => l.Contains("color: "));
        Assert.DoesNotContain(lines, l => l.Trim() == "M0"); // nothing to swap to
    }

    [Fact]
    public void Distinct_Outline_And_Fill_Colors_Form_Separate_Groups()
    {
        // stroke=red, fill=blue on the same shape: the outline is a red pen, the
        // hatch is a blue pen -> two groups with a pause between, even though it's
        // one element. Confirms hatch color follows the fill, not the stroke.
        var doc = Doc("<svg xmlns='http://www.w3.org/2000/svg' width='100' height='120'>" +
                      "<rect x='10' y='10' width='40' height='40' stroke='red' fill='blue'/></svg>");
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });

        var opts = Opts();
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        int redIdx = lines.FindIndex(l => l.Contains("color: red"));
        int blueIdx = lines.FindIndex(l => l.Contains("color: blue"));
        Assert.True(redIdx >= 0 && blueIdx >= 0, "outline (red) and hatch (blue) each get their own group");
        Assert.Equal(1, lines.Count(l => l.Trim() == "M0"));
        int m0Line = lines.FindIndex(l => l.Trim() == "M0");
        Assert.True(redIdx < m0Line && m0Line < blueIdx);
    }

    [Fact]
    public void Announce_Emits_Respond_Line_Naming_Next_Color()
    {
        // Two colors (red then blue): one pen swap, announced as the NEXT color
        // (blue) right before the M0 pause -- so the operator reads which pen to load.
        var doc = Doc(TwoColorSvg);
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });

        var opts = Opts();
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        int redIdx = lines.FindIndex(l => l.Contains("color: red"));
        int blueIdx = lines.FindIndex(l => l.Contains("color: blue"));
        int m0Line = lines.FindIndex(l => l.Trim() == "M0");
        // The first-pen announce names red at the very start; the swap announce
        // names blue right before the M0 pause. Find the swap announce specifically.
        int firstPenIdx = lines.FindIndex(l => l.StartsWith("RESPOND MSG=") && l.Contains("First pen"));
        int swapIdx = lines.FindIndex(l => l.StartsWith("RESPOND MSG=") && l.Contains("Pen swap: install blue"));
        Assert.True(firstPenIdx >= 0, "no first-pen announce");
        Assert.True(swapIdx >= 0, "no swap announce");
        Assert.Contains("RESPOND MSG=\"First pen: red (color 1 of 2)\"", lines);
        Assert.Contains("RESPOND MSG=\"Pen swap: install blue (color 2 of 2)\"", lines);
        // Order: first-pen announce ... red group ... swap announce ... M0 ... blue group.
        Assert.True(firstPenIdx < redIdx && redIdx < swapIdx && swapIdx < m0Line && m0Line < blueIdx);
    }

    [Fact]
    public void Announce_Off_Suppresses_Respond_But_Keeps_Pause()
    {
        var doc = Doc(TwoColorSvg);
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });

        var opts = Opts();
        opts.ColorAnnounce = false;
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        Assert.DoesNotContain(lines, l => l.StartsWith("RESPOND"));
        Assert.Equal(1, lines.Count(l => l.Trim() == "M0"));
    }

    [Fact]
    public void Announce_M117_Format_Is_Bare_Message()
    {
        var doc = Doc(TwoColorSvg);
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });

        var opts = Opts();
        opts.ColorAnnounceCommand = "M117";
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        // M117 is a bare message line (no MSG= quoting).
        Assert.Contains("M117 First pen: red (color 1 of 2)", lines);
        Assert.Contains("M117 Pen swap: install blue (color 2 of 2)", lines);
        Assert.DoesNotContain(lines, l => l.Contains("MSG="));
    }

    [Fact]
    public void FirstPenAnnounce_CutMode_At_Start()
    {
        // Cut mode (outlines only, grouped by stroke color) must also declare the
        // first pen at the very start, then name the next pen at the swap.
        var doc = Doc(TwoColorSvg);
        var opts = new Cli.Options
        {
            Mode = Cli.Mode.Cut,
            Unit = "mm",
            GroupByColor = true,
            PauseBetweenColors = true,
            PauseCommand = "M0",
        };
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileGroupedByColor(opts, compiler, doc.Paths, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        int firstPenIdx = lines.FindIndex(l => l.Contains("First pen: red (color 1 of 2)"));
        int swapIdx = lines.FindIndex(l => l.Contains("Pen swap: install blue (color 2 of 2)"));
        int redIdx = lines.FindIndex(l => l.Contains("color: red"));
        Assert.True(firstPenIdx >= 0, "no first-pen announce");
        Assert.True(swapIdx >= 0, "no swap announce");
        Assert.True(firstPenIdx < redIdx, "first-pen announce precedes the first color group");
    }

    [Fact]
    public void FirstPenAnnounce_Suppressed_When_Pauses_Off()
    {
        // Group-by-color ON but pause-between-colors OFF: no pen-swap workflow, so
        // neither the first-pen announce nor the swap announce is emitted.
        var doc = Doc(TwoColorSvg);
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });
        var opts = Opts();
        opts.PauseBetweenColors = false;
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        Assert.DoesNotContain(lines, l => l.Contains("First pen"));
        Assert.DoesNotContain(lines, l => l.StartsWith("RESPOND"));
        Assert.DoesNotContain(lines, l => l.Trim() == "M0");
    }

    [Fact]
    public void FirstPenAnnounce_Suppressed_When_Announce_Off()
    {
        // ColorAnnounce OFF suppresses every announce (first pen + swaps); the M0
        // pauses remain (PauseBetweenColors is still on).
        var doc = Doc(TwoColorSvg);
        var groups = HatchEngine.Generate(doc.Paths, new HatchOptions { HatchSpacing = 5, HatchAngle = 0, Inset = false, ConnectEnds = false });
        var opts = Opts();
        opts.ColorAnnounce = false;
        var compiler = Cli.EngineBuilder.BuildCompiler(opts);
        Cli.StrokeEmitter.CompileHatchGroupedByColor(opts, compiler, doc.Paths, groups, 0, 100, "test");
        var lines = compiler.Compile(1).Split('\n').Where(x => x.Length > 0).ToList();

        Assert.DoesNotContain(lines, l => l.Contains("First pen"));
        Assert.DoesNotContain(lines, l => l.StartsWith("RESPOND"));
        Assert.Equal(1, lines.Count(l => l.Trim() == "M0"));
    }
}
