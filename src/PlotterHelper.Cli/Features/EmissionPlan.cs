using PlotterHelper.Cli;

namespace PlotterHelper.Cli.Features;

/// <summary>Who owns pen colour grouping: one pen for the whole drawing, or one pen per colour.</summary>
internal enum PenGrouping
{
    /// <summary>Every stroke goes through the same compiler and pen.</summary>
    Single,
    /// <summary>Strokes are emitted one colour at a time, optionally paused for a pen swap.</summary>
    PerColor,
}

/// <summary>How strokes are ordered for emission.</summary>
internal enum StrokeOrder
{
    /// <summary>Document order: the order the paths appear in the SVG.</summary>
    Source,
    /// <summary>Holes before the outlines that enclose them, so a cut piece falls free.</summary>
    InnerBeforeOuter,
    /// <summary>Greedy nearest-neighbour reorder, optionally reversing strokes, to cut pen-up travel.</summary>
    TravelOptimized,
}

/// <summary>
/// The emission strategy that a set of <see cref="Options"/> asks for, resolved once.
/// </summary>
/// <remarks>
/// Stroke emission is a small state machine whose state used to be implicit in a handful of
/// option booleans: every emission path re-derived the ordering from
/// <c>OptimizeTravel</c>/<c>InnerBeforeOuter</c>/<c>GroupByColor</c>, and each derived it in its
/// own nested ternary, so the one fact that actually matters — <c>OptimizeTravel</c> overrides
/// <c>InnerBeforeOuter</c>, because shortest-travel and holes-first are different goals — was
/// documented in a comment on one of the three entry points and merely implied in the others.
///
/// Resolving the pair of axes once here makes that precedence a single, testable rule instead of
/// a property of whoever wrote the last <c>if</c>.
/// </remarks>
internal readonly record struct EmissionPlan(PenGrouping Grouping, StrokeOrder Order)
{
    /// <summary>
    /// Read the plan out of the options. The order of the tests is the precedence: travel
    /// optimization wins over holes-first, which wins over document order.
    /// </summary>
    internal static EmissionPlan From(Options opts) => new(
        opts.GroupByColor ? PenGrouping.PerColor : PenGrouping.Single,
        opts.OptimizeTravel ? StrokeOrder.TravelOptimized
        : opts.InnerBeforeOuter ? StrokeOrder.InnerBeforeOuter
        : StrokeOrder.Source);

    /// <summary>Whether strokes get reordered into a nearest-neighbour tour.</summary>
    internal bool TravelOptimized => Order == StrokeOrder.TravelOptimized;

    /// <summary>Whether paths are reordered holes-first (only meaningful when not travel-optimized).</summary>
    internal bool HolesFirst => Order == StrokeOrder.InnerBeforeOuter;

    internal bool PerColor => Grouping == PenGrouping.PerColor;
}
