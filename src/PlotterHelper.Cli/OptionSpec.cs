namespace PlotterHelper.Cli;

/// <summary>Apply one flag's value to <see cref="Options"/>.</summary>
internal delegate void OptionApply(Options opts, string? value);

/// <summary>
/// One command-line flag. Every flag consumes the following token, even when that token is
/// another flag; that is the historical behaviour and the extension relies on it.
/// </summary>
internal sealed record OptionSpec
{
    public required string Flag { get; init; }
    public required OptionApply Apply { get; init; }

    /// <summary>Help text, printed verbatim (including its two-space indent).</summary>
    public required string Help { get; init; }

    /// <summary>True when the Inkscape panel has a widget for this flag.</summary>
    public bool InPanel { get; init; }

    /// <summary>The .inx &lt;param&gt; name: the panel spells flags with underscores.</summary>
    public string InxParam => Flag.TrimStart('-').Replace('-', '_');
}

/// <summary>A titled group of flags, printed as one help section.</summary>
internal sealed record OptionSection(string Title, string? Prose, IReadOnlyList<OptionSpec> Options);
