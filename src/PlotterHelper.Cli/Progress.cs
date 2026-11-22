namespace PlotterHelper.Cli;

/// <summary>
/// Progress reporting to stderr. With <c>--progress</c> the engine writes
/// <c>PROGRESS &lt;pct&gt;\t&lt;label&gt;</c> lines; the Inkscape shim drives a
/// zenity progress dialog from them. All writes go through a lock so parallel
/// hatch workers don't interleave lines.
/// </summary>
internal static class Progress
{
    private static readonly Lock Lock = new();

    /// <summary>Write a progress line to stderr: "PROGRESS &lt;pct&gt;\t&lt;label&gt;".</summary>
    internal static void Report(Options options, int percent, string label)
    {
        if (!options.Progress) return;
        percent = Math.Clamp(percent, 0, 100);
        lock (Lock)
        {
            Console.Error.WriteLine($"PROGRESS {percent}\t{label}");
            Console.Error.Flush();
        }
    }
}