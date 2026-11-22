using System.Text.RegularExpressions;
using Xunit;

namespace PlotterHelper.Tests;

/// <summary>
/// The option surface is declared once, in <see cref="PlotterHelper.Cli.OptionTable"/>, and read
/// from there by both the parser and the help text. The Inkscape side is still hand-written:
/// plotter-helper.py forwards flags and plotter-helper.inx offers widgets, so these tests check
/// the table against both files. A flag that is added without a widget, or a widget that points
/// at nothing, fails here instead of producing a silent wrong drawing.
/// </summary>
public class OptionSurfaceTests
{
    /// <summary>
    /// Flags the extension consumes itself and never forwards to the engine: notebook/tab state,
    /// output naming, and the panel's own save-config action.
    /// </summary>
    private static readonly HashSet<string> PanelOnly = new(StringComparer.Ordinal)
    {
        "--tab", "--hatch-sub", "--features-sub",
        "--directory", "--filename", "--filename-suffix",
        "--save-per-color-config",
    };

    /// <summary>
    /// Flags the extension can send although no panel widget sets them. These are panel features
    /// waiting to be wired up, not parser features to delete; the list is a ratchet.
    /// </summary>
    private static readonly HashSet<string> ReachableWithoutWidget = new(StringComparer.Ordinal)
    {
        "--do-z-axis-start", "--z-axis-start", "--pass-depth",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlotterHelper.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static HashSet<string> All(string text, string pattern, Func<string, string>? normalise = null)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, pattern))
            flags.Add(normalise == null ? match.Groups[1].Value : normalise(match.Groups[1].Value));
        return flags;
    }

    // The panel spells flags with underscores and .inx params carry no dashes; the CLI uses
    // hyphens everywhere.
    private static string Dashed(string name)
    {
        var flag = name.StartsWith("--", StringComparison.Ordinal) ? name : "--" + name;
        return flag.Replace('_', '-');
    }

    private static HashSet<string> SpecFlags() =>
        PlotterHelper.Cli.OptionTable.All.Select(s => s.Flag).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ExtensionFlags() => All(
        Read(Path.Combine("inkscape", "plotter-helper.py")),
        "add_argument\\(\"(--[a-z0-9_]+)\"",
        Dashed);

    private static HashSet<string> PanelParams() => All(
        Read(Path.Combine("inkscape", "plotter-helper.inx")),
        "<param name=\"([a-z0-9_]+)\"");

    private static string HelpText()
    {
        var original = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            PlotterHelper.Cli.ArgParser.PrintHelp();
        }
        finally
        {
            Console.SetError(original);
        }
        return buffer.ToString();
    }

    private static void AssertSubset(IEnumerable<string> inner, IEnumerable<string> outer, string what)
    {
        var missing = inner.Except(outer).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, $"{what}:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void The_table_declares_each_flag_once()
    {
        var flags = PlotterHelper.Cli.OptionTable.All.Select(s => s.Flag).ToList();
        Assert.Equal(flags.Count, flags.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(flags.Count, PlotterHelper.Cli.OptionTable.ByFlag.Count);
    }

    [Fact]
    public void Every_flag_has_a_help_line_of_its_own()
    {
        var lines = HelpText().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        foreach (var spec in PlotterHelper.Cli.OptionTable.All)
        {
            Assert.True(
                lines.Any(l => l.StartsWith("  " + spec.Flag + " ", StringComparison.Ordinal)),
                $"{spec.Flag} has no help line of its own");
        }
    }

    [Fact]
    public void Every_flag_the_panel_sends_is_understood()
    {
        var sendable = ExtensionFlags().Where(f => !PanelOnly.Contains(f));
        AssertSubset(sendable, SpecFlags(), "the extension sends flags the parser has no case for");
    }

    [Fact]
    public void The_panel_and_the_table_agree_on_which_flags_have_widgets()
    {
        var inx = PanelParams();

        // Declared in the panel, and present in the .inx under the derived name.
        AssertSubset(
            PlotterHelper.Cli.OptionTable.All.Where(s => s.InPanel).Select(s => s.InxParam),
            inx, "flags marked InPanel with no .inx param");

        // Present in the .inx, and claimed by a spec (panel-only params aside).
        var panelOnlyParams = PanelOnly.Select(p => p.TrimStart('-').Replace('-', '_'));
        AssertSubset(inx.Except(panelOnlyParams),
            PlotterHelper.Cli.OptionTable.All.Where(s => s.InPanel).Select(s => s.InxParam),
            ".inx params no flag claims");
    }

    [Fact]
    public void Only_the_listed_flags_are_reachable_without_a_widget()
    {
        var extension = ExtensionFlags();
        var reachableWithoutWidget = PlotterHelper.Cli.OptionTable.All
            .Where(s => !s.InPanel && extension.Contains(s.Flag))
            .Select(s => s.Flag)
            .ToHashSet(StringComparer.Ordinal);

        AssertSubset(ReachableWithoutWidget, reachableWithoutWidget,
            "listed as reachable without a widget but the extension no longer sends it");
        AssertSubset(reachableWithoutWidget, ReachableWithoutWidget,
            "reachable from the panel but marked InPanel=false");
    }
}
