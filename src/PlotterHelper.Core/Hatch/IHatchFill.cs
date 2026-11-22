namespace PlotterHelper.Hatch;

/// <summary>
/// One fill style. <see cref="HatchEngine.Generate"/> owns the per-element skeleton
/// (flatten, fill, jitter, collect); a style only turns polygons into hatch paths.
/// </summary>
internal interface IHatchFill
{
    void Fill(HatchFillContext ctx);
}
