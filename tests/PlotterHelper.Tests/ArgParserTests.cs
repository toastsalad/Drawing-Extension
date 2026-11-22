using Xunit;
using PlotterHelper.Cli;

namespace PlotterHelper.Tests;

/// <summary>Parsing is now a loop over <see cref="OptionTable"/>; these pin the behaviour the
/// table has to reproduce, including the quirks the extension depends on.</summary>
public class ArgParserTests
{
    private static Options Parsed(params string[] args)
    {
        var opts = new Options();
        Assert.True(ArgParser.Parse(args, opts), "parse failed");
        return opts;
    }

    [Fact]
    public void A_flag_applies_the_following_token()
    {
        var opts = Parsed("--passes", "4", "--travel-speed", "1234.5");
        Assert.Equal(4, opts.Passes);
        Assert.Equal(1234.5, opts.TravelSpeed);
    }

    [Fact]
    public void Numeric_defaults_apply_when_the_value_is_not_a_number()
    {
        var opts = Parsed("--passes", "many", "--cutting-speed", "fast", "--hatch-angle", "45,5");
        Assert.Equal(1, opts.Passes);
        Assert.Equal(750, opts.CuttingSpeed);
        Assert.Equal(45.5, opts.HatchAngle); // a decimal comma is accepted
    }

    [Fact]
    public void Bools_accept_true_false_1_and_an_empty_token()
    {
        Assert.True(Parsed("--cross-hatch", "true").CrossHatch);
        Assert.False(Parsed("--cross-hatch", "false").CrossHatch);
        Assert.True(Parsed("--cross-hatch", "1").CrossHatch);
        // A missing token reads as "true": the extension always writes a value, but a hand-typed
        // "--cross-hatch" must not silently mean false.
        Assert.True(Parsed("--cross-hatch").CrossHatch);
    }

    [Fact]
    public void An_unknown_flag_fails_before_later_flags_are_applied()
    {
        var opts = new Options();
        Assert.False(ArgParser.Parse(["--nope", "1", "--passes", "9"], opts));
        Assert.Equal(1, opts.Passes);
    }

    [Fact]
    public void Every_declared_flag_parses_with_a_representative_value()
    {
        // Guards the table against a typo in a Flag or an Apply that throws on ordinary input.
        foreach (var spec in OptionTable.All)
        {
            var value = spec.Flag switch
            {
                "--mode" => "cut",
                "--fill-style" => "parallel",
                _ => "1",
            };
            var opts = new Options();
            var ex = Record.Exception(() => ArgParser.Parse([spec.Flag, value], opts));
            Assert.True(ex == null, $"{spec.Flag} threw {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Fact]
    public void An_empty_command_line_is_a_help_request_and_an_explicit_help_is_not_an_error()
    {
        Assert.True(ArgParser.IsHelpRequest([]));
        Assert.True(ArgParser.IsHelpRequest(["--help"]));
        Assert.False(ArgParser.IsHelpRequest(["--mode", "cut"]));
    }
}
