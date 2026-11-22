using System.Globalization;
using System.Text;

namespace PlotterHelper.Cli;

/// <summary>CLI argument parsing and help text, both driven by <see cref="OptionTable"/>.</summary>
internal static class ArgParser
{
    /// <summary>True when the command line asks for the usage text instead of a job.</summary>
    internal static bool IsHelpRequest(string[] args) =>
        args.Length == 0 || args[0] is "-h" or "--help" or "help";

    internal static bool Parse(string[] args, Options opts)
    {
        for (int argIndex = 0; argIndex < args.Length; argIndex++)
        {
            string arg = args[argIndex];
            if (!OptionTable.ByFlag.TryGetValue(arg, out OptionSpec? spec))
            {
                Console.Error.WriteLine($"PlotterHelper: unknown argument '{arg}'");
                return false;
            }

            // Every flag consumes the next token, which is the longstanding behaviour the
            // Inkscape extension relies on (it always writes "--flag value").
            string? value = argIndex + 1 < args.Length ? args[argIndex + 1] : null;
            try
            {
                spec.Apply(opts, value);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
            {
                // An unparseable value used to escape as an unhandled exception, so the whole run
                // aborted with a stack trace instead of saying which argument was wrong. That is how
                // a stale build rejecting a newer --fill-style value looked like a silent no-op: the
                // caller saw a failed process and substituted its own output.
                Console.Error.WriteLine($"PlotterHelper: bad value '{value}' for {arg}");
                return false;
            }
            argIndex++;
        }
        return true;
    }

    internal static bool ParseBool(string? text) =>
        string.IsNullOrEmpty(text) || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";

    internal static double ParseDouble(string text, double fallback)
    {
        if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return value;
        return fallback;
    }

    internal static int ParseInt(string text, int fallback)
    {
        if (int.TryParse(text.Replace(',', '.'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
        return fallback;
    }

    internal static void PrintHelp()
    {
        var text = new StringBuilder(OptionTable.HeaderText);
        foreach (OptionSection section in OptionTable.Sections)
        {
            text.Append('\n').Append(section.Title).Append('\n');
            if (section.Prose != null) text.Append(section.Prose);
            foreach (OptionSpec spec in section.Options)
                text.Append(spec.Help).Append('\n');
        }
        Console.Error.WriteLine(text.ToString());
    }
}
