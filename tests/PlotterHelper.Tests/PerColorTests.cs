using Xunit;
using PlotterHelper.Geometry;
using PlotterHelper.Svg;
using PlotterHelper.Hatch;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using GcodeInterface = PlotterHelper.Compiler.GcodeInterface;
using CustomGcodeInterface = PlotterHelper.Compiler.CustomGcodeInterface;

namespace PlotterHelper.Tests;

public class PerColorTests
{
    private static SvgDocument Doc(string svg) =>
        SvgParser.ParseString(svg, handleViewBox: false, canvasHeight: 120, transformOrigin: true);

    [Fact]
    public void Config_Normalizes_Keys_And_Parses_Overrides()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ \"ff0000\": { \"hatchSpacing\": 4, \"fillStyle\": \"concentric\" }, \"#00FF00\": { \"passes\": 3 } }");
            var map = Cli.Pens.PerColorConfig.Load(path);
            Assert.True(map.ContainsKey("FF0000"));
            Assert.True(map.ContainsKey("00FF00"));
            Assert.Equal(4, map["FF0000"].HatchSpacing);
            Assert.Equal(FillStyle.Concentric, map["FF0000"].FillStyle);
            Assert.Equal(3, map["00FF00"].Passes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApplyOverrides_Only_Sets_Specified_Fields()
    {
        var opts = new Cli.Options { HatchSpacing = 3, HatchAngle = 45, CuttingSpeed = 750, Passes = 1 };
        var ov = new Cli.Pens.PerColorOpts { HatchSpacing = 8, Passes = 2 };
        var e = Cli.Pens.PerColorCompiler.ApplyOverrides(opts, ov);
        Assert.Equal(8, e.HatchSpacing);      // overridden
        Assert.Equal(2, e.Passes);            // overridden
        Assert.Equal(45, e.HatchAngle);        // preserved
        Assert.Equal(750, e.CuttingSpeed);     // preserved
        Assert.Equal(3, opts.HatchSpacing);    // original untouched
    }

    [Fact]
    public void Clone_Does_Not_Share_Tracer()
    {
        var opts = new Cli.Options { Tracer = { Smooth = 1 } };
        var clone = opts.Clone();
        clone.Tracer.Smooth = 5;
        Assert.Equal(1, opts.Tracer.Smooth);
    }

    [Fact]
    public void RunPerColor_Applies_PerPen_Settings_And_Skips_Unmapped_Hatch()
    {
        var doc = Doc("<svg xmlns='http://www.w3.org/2000/svg' width='300' height='120'>" +
            "<rect x='10' y='10' width='80' height='80' stroke='red' fill='red'/>" +
            "<rect x='110' y='10' width='80' height='80' stroke='blue' fill='blue'/>" +
            "<rect x='210' y='10' width='80' height='80' stroke='green' fill='green'/>" +
            "</svg>");

        var opts = new Cli.Options
        {
            Mode = Cli.Mode.Hatch,
            Unit = "mm",
            TravelSpeed = 1000,
            CuttingSpeed = 555,
            Passes = 1,
            PassDepth = 1,
            GroupByColor = true,
            PauseBetweenColors = true,
            PauseCommand = "M0",
            IncludeOutlines = true,
            PerColor = new()
            {
                { "FF0000", new Cli.Pens.PerColorOpts { FillStyle = FillStyle.Concentric, HatchSpacing = 4, Passes = 2, CuttingSpeed = 300 } },
                { "0000FF", new Cli.Pens.PerColorOpts { FillStyle = FillStyle.Parallel, HatchSpacing = 8, Passes = 1, CuttingSpeed = 800 } },
            },
            Gcode = Path.GetTempFileName()
        };
        try
        {
            Assert.Equal(0, Cli.Pens.PerColorCompiler.Run(opts, doc, hatchMode: true));
            var lines = File.ReadAllText(opts.Gcode).Split('\n');

            // Red has passes=2 (one Z pass-step); blue and green passes=1.
            Assert.Equal(1, lines.Count(l => l.StartsWith("G1 Z-")));
            // Three colors -> two pen-swap pauses.
            Assert.Equal(2, lines.Count(l => l == "M0"));
            // Per-pen cutting speeds are present.
            Assert.Contains(lines, l => l.Contains("F300 "));  // red
            Assert.Contains(lines, l => l.Contains("F800 "));  // blue
            // Announce names the next pen by hex.
            Assert.Contains(lines, l => l.Contains("install #0000FF"));
            Assert.Contains(lines, l => l.Contains("install #008000"));
            // The FIRST pen is declared at the very start of the multicolor run, before
            // the first pause (no "install" -- the pen is loaded before starting).
            int firstPenIdx = Array.FindIndex(lines, l => l.Contains("First pen: #FF0000 (color 1 of 3)"));
            int firstM0 = Array.FindIndex(lines, l => l == "M0");
            Assert.True(firstPenIdx >= 0, "no first-pen announce at the start");
            Assert.True(firstPenIdx < firstM0, "first-pen announce must precede the first pause");

            // Green is unmapped -> not hatched: after the second M0 (the green
            // section) there must be no red/blue cutting speeds, only the global
            // F555 outline + F1000 travel.
            int secondM0 = lines.Select((l, i) => (l, i)).Where(t => t.l == "M0").Skip(1).First().i;
            var tail = lines.Skip(secondM0 + 1).ToList();
            Assert.DoesNotContain(tail, l => l.Contains("F300 ") || l.Contains("F800 "));
            Assert.Contains(tail, l => l.Contains("F555 "));  // green outline at global speed
        }
        finally { File.Delete(opts.Gcode); }
    }
}
