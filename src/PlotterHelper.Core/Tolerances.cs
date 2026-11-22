namespace PlotterHelper;

/// <summary>Global tunable tolerances, mirroring svg_to_gcode.TOLERANCES.</summary>
public static class Tolerances
{
    /// <summary>Maximum deviation allowed when approximating curves with line segments.</summary>
    public const double Approximation = 1e-2;

    /// <summary>Tolerance for considering two input points identical.</summary>
    public const double Input = 1e-3;

    /// <summary>Operational / rounding tolerance.</summary>
    public const double Operation = 1e-6;
}

/// <summary>Supported output units.</summary>
public static class Units
{
    public const string Millimeters = "mm";
    public const string Inches = "in";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Millimeters, Inches };
}
