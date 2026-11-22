namespace PlotterHelper.Cli;

/// <summary>Unit-conversion helpers for translating hatch/length values into the
/// document (output) unit. Distances in the SVG/CLI are stored in user units; the
/// hatch options and cutter/pen thresholds are expressed in a chosen unit (mm by
/// default) and converted to doc units where the geometry expects them.</summary>
internal static class Units
{
    /// <summary>Convert a length given in <paramref name="fromUnit"/> into the document unit (<paramref name="docUnit"/>).</summary>
    internal static double ToDocUnits(double value, string fromUnit, string? docUnit)
    {
        double pxPerFrom = PxPerUnit(fromUnit);
        double pxPerDoc = PxPerUnit(docUnit ?? "mm");
        return value * pxPerFrom / pxPerDoc;
    }

    internal static double PxPerUnit(string unit) => unit switch
    {
        "mm" => 96.0 / 25.4,
        "in" => 96.0,
        _ => 1.0,
    };
}