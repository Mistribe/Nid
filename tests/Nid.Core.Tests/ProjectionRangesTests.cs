using System.Diagnostics;
using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Ranges spec §3: the projection's percentile bands and the share of runs whose savings last.</summary>
public class ProjectionRangesTests
{
    private static readonly LocalDate Today = new(2026, 11, 10);

    private static ProjectionInputs Inputs(int months, IReadOnlyDictionary<string, decimal>? volatility = null, decimal monthlySavings = 500m, int slices = 3)
    {
        var classes = new[] { AllocationClasses.Equity, AllocationClasses.Bond, AllocationClasses.Cash, AllocationClasses.Mixed };
        var savings = Enumerable.Range(0, slices)
            .Select(i => new SavingsSlice(new ItemId(Guid.NewGuid()), classes[i % classes.Length], AllocationLiquidity.Available, 10_000m))
            .ToList();
        var assumptions = ProjectionDefaults.Assumptions with { ClassVolatility = volatility };
        return new ProjectionInputs(Today, months, savings, Enumerable.Repeat(0m, months + 1).ToList(), [], monthlySavings, assumptions, []);
    }

    private static readonly IReadOnlyDictionary<string, decimal> Flat =
        AllocationClasses.All.ToDictionary(c => c, _ => 0m);

    [Fact]
    public void With_no_volatility_every_band_is_the_deterministic_projection()
    {
        var inputs = Inputs(120, Flat);
        var deterministic = HouseholdProjection.Project(inputs).Points;
        var ranges = ProjectionRanges.Simulate(inputs, runs: 50);

        Assert.Equal(deterministic.Count, ranges.Points.Count);
        Assert.All(deterministic.Zip(ranges.Points), pair =>
        {
            var (point, band) = pair;
            Assert.Equal(point.On, band.On);
            Assert.Equal((point.Nominal, point.Nominal, point.Nominal), (band.Low, band.Median, band.High));
            Assert.Equal((point.Real, point.Real, point.Real), (band.LowReal, band.MedianReal, band.HighReal));
        });
        Assert.Equal(1m, ranges.Success);
    }

    [Fact]
    public void The_same_inputs_and_seed_give_the_same_bands()
    {
        var inputs = Inputs(60);
        var first = ProjectionRanges.Simulate(inputs, runs: 200);
        var second = ProjectionRanges.Simulate(inputs, runs: 200);
        Assert.Equal(first.Points, second.Points);
        Assert.NotEqual(first.Points, ProjectionRanges.Simulate(inputs, runs: 200, seed: 10).Points);
    }

    [Fact]
    public void Volatility_spreads_the_bands_around_today()
    {
        var points = ProjectionRanges.Simulate(Inputs(120), runs: 500).Points;

        Assert.Equal(points[0].Low, points[0].High); // today is known
        var last = points[^1];
        Assert.True(last.Low < last.Median && last.Median < last.High);
        Assert.True(last.LowReal < last.Low); // inflation at 2 %
    }

    [Fact]
    public void Success_is_the_share_of_runs_whose_savings_last()
    {
        // 30,000 in equity and bonds, 400 a month spent: the deterministic savings last past the ten years, not every draw.
        var ranges = ProjectionRanges.Simulate(Inputs(120, monthlySavings: -400m, slices: 2), runs: 1000);
        Assert.InRange(ranges.Success, 0.01m, 0.99m);
        Assert.Equal(0m, ProjectionRanges.Simulate(Inputs(120, monthlySavings: -1_000m, slices: 2), runs: 100).Success);
    }

    [Fact]
    public void A_class_left_out_of_the_table_takes_its_default_volatility()
    {
        var assumptions = ProjectionDefaults.Assumptions with { ClassVolatility = new Dictionary<string, decimal> { [AllocationClasses.Bond] = 0.02m } };
        Assert.Equal(0.02m, assumptions.VolatilityOf(AllocationClasses.Bond));
        Assert.Equal(ProjectionDefaults.ClassVolatility[AllocationClasses.Equity], assumptions.VolatilityOf(AllocationClasses.Equity));
        Assert.Equal(0m, ProjectionDefaults.Assumptions.VolatilityOf("unknown"));
    }

    /// <summary>Ranges spec §5: forty years, twenty savings slices, every run, within the budget.</summary>
    [Fact]
    public void Forty_years_run_within_the_time_budget()
    {
        var inputs = Inputs(480, slices: 20);
        ProjectionRanges.Simulate(inputs, runs: 10); // warm-up
        var clock = Stopwatch.StartNew();
        ProjectionRanges.Simulate(inputs);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed.TotalMilliseconds:0} ms");
    }
}
