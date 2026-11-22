using PlotterHelper.Compiler;
using GcodeCompiler = PlotterHelper.Compiler.Compiler;
using PlotterHelper.Cli;

namespace PlotterHelper.Cli.Pens;

/// <summary>
/// Pen-swap pause + host-console color announce lines for multi-color pen plotting.
/// The announce defaults to Klipper's <c>RESPOND MSG=...</c> (echoes to the
/// Mainsail/Fluidd console); <c>M117</c> formats as a bare LCD display message.
/// The first pen's color is declared at the very start of a run (no pause) so the
/// operator knows which pen to load; subsequent swaps are announced + paused.
/// </summary>
internal static class PenAnnounce
{
    /// <summary>
    /// Emit a pen-swap pause, preceded (when enabled) by a host-console message
    /// naming the next pen color. The announce defaults to Klipper's RESPOND
    /// (MSG=...), which echoes to the Mainsail/Fluidd console; M117 formats as a
    /// bare LCD display message instead. The pause itself (lift + opts.PauseCommand,
    /// default M0) always follows so the operator can swap pens.
    /// </summary>
    internal static void AppendPenSwap(Options opts, GcodeCompiler compiler, string nextColor, int nextIndex, int total)
    {
        if (opts.ColorAnnounce && total > 0 && !string.IsNullOrEmpty(nextColor))
            compiler.AppendRaw(FormatAnnounceCommand(opts.ColorAnnounceCommand,
                "Pen swap: install " + nextColor + " (color " + nextIndex + " of " + total + ")"));
        compiler.AppendPause(opts.PauseCommand);
    }

    /// <summary>
    /// Declare the FIRST pen's color at the very start of a multicolor run, so the
    /// operator knows which pen to load before starting (<see cref="AppendPenSwap"/>
    /// only ever names the NEXT pen). No pause follows -- the pen is loaded before the
    /// run. Gated like the swap announces (group-by-color + pause-between-colors +
    /// color-announce).
    /// </summary>
    internal static void AppendFirstPenAnnounce(Options opts, GcodeCompiler compiler, string firstColor, int total)
    {
        if (opts.GroupByColor && opts.PauseBetweenColors && opts.ColorAnnounce
            && total > 0 && !string.IsNullOrEmpty(firstColor))
            compiler.AppendRaw(FormatAnnounceCommand(opts.ColorAnnounceCommand,
                "First pen: " + firstColor + " (color 1 of " + total + ")"));
    }

    /// <summary>Format a host-console announce line for the configured command.</summary>
    internal static string FormatAnnounceCommand(string? command, string msg)
    {
        // Strip line breaks so the message stays a single G-code line.
        msg = msg.Replace("\r", " ").Replace("\n", " ");
        string cmd = string.IsNullOrWhiteSpace(command) ? "RESPOND" : command.Trim();
        if (cmd.Equals("M117", StringComparison.OrdinalIgnoreCase))
            return "M117 " + msg;
        char quote = (char)34; // double-quote, kept out of the source to avoid escaping noise
        return cmd + " MSG=" + quote + msg + quote;
    }

    /// <summary>Lines form of <see cref="AppendPenSwap"/> for the per-color assembly
    /// path (which builds the G-code as a line list rather than via a compiler).</summary>
    internal static List<string> PenSwapLines(Options eff, string nextColor, int nextIndex, int total)
    {
        var lines = new List<string>();
        if (eff.ColorAnnounce && total > 0 && !string.IsNullOrEmpty(nextColor))
            lines.Add(FormatAnnounceCommand(eff.ColorAnnounceCommand,
                "Pen swap: install " + nextColor + " (color " + nextIndex + " of " + total + ")"));
        foreach (var offLine in eff.ToolOff.Replace("\\n", "\n").Split('\n'))
            if (offLine.Length > 0) lines.Add(offLine);
        if (!string.IsNullOrEmpty(eff.PauseCommand)) lines.Add(eff.PauseCommand);
        return lines;
    }

    /// <summary>Lines form of <see cref="AppendFirstPenAnnounce"/> for the per-color
    /// assembly path (which builds the G-code as a line list rather than via a compiler).</summary>
    internal static List<string> FirstPenLines(Options opts, string firstColor, int total)
    {
        var lines = new List<string>();
        if (opts.GroupByColor && opts.PauseBetweenColors && opts.ColorAnnounce
            && total > 0 && !string.IsNullOrEmpty(firstColor))
            lines.Add(FormatAnnounceCommand(opts.ColorAnnounceCommand,
                "First pen: " + firstColor + " (color 1 of " + total + ")"));
        return lines;
    }

    /// <summary>Human label for a normalized hex color key ("" -> "(default)").</summary>
    internal static string ColorName(string hex) => hex.Length == 0 ? "(default)" : "#" + hex;
}