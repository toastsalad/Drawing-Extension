namespace PlotterHelper.Geometry;

internal static class Formulas
{
    public const double TwoPi = 2.0 * Math.PI;

    /// <summary>Map a [0,1] tool power to a [0,255] spindle value.</summary>
    public static int PowerToSpindle(double power) => (int)Math.Round(255.0 * power);
}
