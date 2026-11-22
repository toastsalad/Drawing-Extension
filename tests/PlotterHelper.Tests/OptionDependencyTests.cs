using Xunit;
using Options = PlotterHelper.Cli.Options;
using Dependencies = PlotterHelper.Cli.OptionDependencies;

namespace PlotterHelper.Tests;

/// <summary>
/// Each of these flags only does something while another option is on. Before this table existed
/// the flag was dropped without a word, so a panel could offer a control that did nothing.
/// </summary>
public class OptionDependencyTests
{
    [Fact]
    public void A_plain_option_set_produces_no_notes_and_changes_nothing()
    {
        var opts = new Options { Input = "in.svg", Gcode = "out.gcode" };
        var notes = Dependencies.Normalize(opts);

        Assert.Empty(notes);
        Assert.False(opts.PauseBetweenColors);
        Assert.False(opts.ConnectOutlines);
        Assert.False(opts.CrossHatch);
        Assert.Null(opts.TraceSvg);
        Assert.False(opts.TileAlignmentMarks);
        Assert.Equal(0, opts.TileOverlap);
    }

    [Fact]
    public void PauseBetweenColors_Needs_Grouping()
    {
        var alone = new Options { PauseBetweenColors = true };
        var notes = Dependencies.Normalize(alone);
        Assert.Single(notes);
        Assert.Contains("--pause-between-colors", notes[0]);
        Assert.False(alone.PauseBetweenColors, "the flag does nothing, so it must not stay set");

        var grouped = new Options { PauseBetweenColors = true, GroupByColor = true };
        Assert.Empty(Dependencies.Normalize(grouped));
        Assert.True(grouped.PauseBetweenColors, "with grouping the flag is meaningful and must stay");
    }

    [Fact]
    public void ConnectOutlines_Needs_Travel_Optimization()
    {
        var alone = new Options { ConnectOutlines = true };
        Assert.Single(Dependencies.Normalize(alone));
        Assert.False(alone.ConnectOutlines);

        var optimized = new Options { ConnectOutlines = true, OptimizeTravel = true };
        Assert.Empty(Dependencies.Normalize(optimized));
        Assert.True(optimized.ConnectOutlines);
    }

    [Fact]
    public void CrossHatch_Needs_A_Hatch_Mode()
    {
        var cut = new Options { CrossHatch = true };
        Assert.Single(Dependencies.Normalize(cut));
        Assert.False(cut.CrossHatch);

        var hatch = new Options { CrossHatch = true, Mode = PlotterHelper.Cli.Mode.Hatch };
        Assert.Empty(Dependencies.Normalize(hatch));
        Assert.True(hatch.CrossHatch);
    }

    [Fact]
    public void TraceSvg_Needs_Tracing()
    {
        var alone = new Options { TraceSvg = "traced.svg" };
        Assert.Single(Dependencies.Normalize(alone));
        Assert.Null(alone.TraceSvg);

        var tracing = new Options { TraceSvg = "traced.svg", Trace = true };
        Assert.Empty(Dependencies.Normalize(tracing));
        Assert.Equal("traced.svg", tracing.TraceSvg);
    }

    [Fact]
    public void Tiling_Switches_Need_Both_Tile_Sides()
    {
        // Tiling itself needs a width and a height, so one side alone is still nothing.
        var oneSide = new Options { TileAlignmentMarks = true, TileOverlap = 2, TileWidth = 100 };
        var notes = Dependencies.Normalize(oneSide);
        Assert.Equal(2, notes.Count);
        Assert.False(oneSide.TileAlignmentMarks);
        Assert.Equal(0, oneSide.TileOverlap);

        var bothSides = new Options { TileAlignmentMarks = true, TileOverlap = 2, TileWidth = 100, TileHeight = 100 };
        Assert.Empty(Dependencies.Normalize(bothSides));
        Assert.True(bothSides.TileAlignmentMarks);
        Assert.Equal(2, bothSides.TileOverlap);
    }

    [Fact]
    public void Normalize_Is_Idempotent()
    {
        var opts = new Options { PauseBetweenColors = true, ConnectOutlines = true, CrossHatch = true };
        Assert.Equal(3, Dependencies.Normalize(opts).Count);
        Assert.Empty(Dependencies.Normalize(opts));
    }
}
