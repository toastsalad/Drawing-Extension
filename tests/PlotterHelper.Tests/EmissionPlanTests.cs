using Xunit;
using Plan = PlotterHelper.Cli.Features.EmissionPlan;
using Order = PlotterHelper.Cli.Features.StrokeOrder;
using Grouping = PlotterHelper.Cli.Features.PenGrouping;
using Options = PlotterHelper.Cli.Options;

namespace PlotterHelper.Tests;

/// <summary>
/// The emission plan is the one place that says what a combination of
/// --optimize-travel / --inner-before-outer / --group-by-color does. Every emission path reads
/// the resolved plan instead of re-deriving the booleans, so these tests are the contract for all
/// of them at once.
/// </summary>
public class EmissionPlanTests
{
    private static Options Opts(bool groupByColor = false, bool optimizeTravel = false,
        bool innerBeforeOuter = false) => new()
    {
        GroupByColor = groupByColor,
        OptimizeTravel = optimizeTravel,
        InnerBeforeOuter = innerBeforeOuter,
    };

    [Fact]
    public void Defaults_Are_One_Pen_In_Document_Order()
    {
        var plan = Plan.From(Opts());
        Assert.Equal(Grouping.Single, plan.Grouping);
        Assert.Equal(Order.Source, plan.Order);
        Assert.False(plan.PerColor);
        Assert.False(plan.HolesFirst);
        Assert.False(plan.TravelOptimized);
    }

    [Fact]
    public void InnerBeforeOuter_Reorders_Holes_First_On_One_Pen()
    {
        var plan = Plan.From(Opts(innerBeforeOuter: true));
        Assert.Equal(Order.InnerBeforeOuter, plan.Order);
        Assert.True(plan.HolesFirst);
        Assert.False(plan.TravelOptimized);
    }

    [Fact]
    public void OptimizeTravel_Is_Its_Own_Order()
    {
        var plan = Plan.From(Opts(optimizeTravel: true));
        Assert.Equal(Order.TravelOptimized, plan.Order);
        Assert.True(plan.TravelOptimized);
        Assert.False(plan.HolesFirst);
    }

    [Fact]
    public void OptimizeTravel_Overrides_InnerBeforeOuter_Rather_Than_Combining_With_It()
    {
        // Shortest-travel and holes-first are different goals (pen plotting vs. drag-knife
        // cutting), so travel wins. This used to be stated in a comment on one entry point and
        // implied by the order of two ternaries on the others.
        var plan = Plan.From(Opts(optimizeTravel: true, innerBeforeOuter: true));
        Assert.Equal(Order.TravelOptimized, plan.Order);
        Assert.False(plan.HolesFirst, "holes-first must not also apply when travel ordering wins");
    }

    [Fact]
    public void Grouping_And_Order_Are_Independent_Axes()
    {
        foreach (var groupByColor in new[] { false, true })
        {
            foreach (var optimizeTravel in new[] { false, true })
            {
                foreach (var innerBeforeOuter in new[] { false, true })
                {
                    var plan = Plan.From(Opts(groupByColor, optimizeTravel, innerBeforeOuter));
                    Assert.Equal(groupByColor ? Grouping.PerColor : Grouping.Single, plan.Grouping);
                    Assert.Equal(optimizeTravel, plan.TravelOptimized);
                }
            }
        }
    }
}
