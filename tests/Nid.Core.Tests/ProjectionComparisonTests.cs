using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #416: a comparison whose grids diverged is no comparison, without an exception for the readers that list it.</summary>
public class ProjectionComparisonTests
{
    private static readonly LocalDate Today = new(2026, 10, 27);

    private static ProjectionInputs Inputs(int months) => new(
        Today, months, [], Enumerable.Repeat(0m, months + 1).ToList(), [], 0m,
        new ProjectionAssumptions(new Dictionary<string, decimal> { [AllocationClasses.Cash] = 0m }, 0m, null, AllocationClasses.Cash, true), []);

    private static ProjectionScenarioReading Reading(int baseMonths, int scenarioMonths) => new(
        new ProjectionReading(Inputs(baseMonths), Currency.Eur, Today.PlusMonths(baseMonths), "none", [], [], []),
        Inputs(scenarioMonths),
        new ProjectionScenario(1, new ScenarioHorizon(1, null), new ProjectionOverrides(null, null, null, null, null), []),
        [], [], "none");

    [Fact]
    public void Grids_that_diverged_give_no_comparison()
    {
        Assert.Null(ProjectionComparisonView.TryFrom(Reading(12, 24)));
        Assert.Equal("0.00", ProjectionComparisonView.TryFrom(Reading(12, 12))!.GapNominal);
    }
}
