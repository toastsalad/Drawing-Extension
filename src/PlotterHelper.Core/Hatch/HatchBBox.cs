namespace PlotterHelper.Hatch;

/// <summary>Axis-aligned bounds of an element's flattened polygons.</summary>
internal struct HatchBBox
{
    public double Xmin, Xmax, Ymin, Ymax;

    public readonly bool IsValid => Xmax > Xmin && Ymax > Ymin;
}
