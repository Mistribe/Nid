using Nid.Core.Application.Projection;
using Nid.Core.Contracts;

namespace Nid.Core.Tests;

public class ProjectionAssumptionRulesTests
{
    private static ProjectionAssumptions With(Func<ProjectionAssumptions, ProjectionAssumptions> change) => change(ProjectionDefaults.Assumptions);

    private static ProjectionAssumptions WithRate(string @class, decimal rate)
        => With(a => a with { ClassRates = new Dictionary<string, decimal>(a.ClassRates) { [@class] = rate } });

    private static string Only(ProjectionAssumptions a) => Assert.Single(ProjectionRules.Check(a)).Field;

    [Fact]
    public void Defaults_are_valid() => Assert.Empty(ProjectionRules.Check(ProjectionDefaults.Assumptions));

    [Fact]
    public void Every_class_is_required()
    {
        var rates = ProjectionDefaults.ClassRates.Where(r => r.Key != AllocationClasses.Equity).ToDictionary(r => r.Key, r => r.Value);

        Assert.Equal("classRates.equity", Only(With(a => a with { ClassRates = rates })));
    }

    [Fact]
    public void Unknown_class_is_refused()
    {
        var rates = new Dictionary<string, decimal>(ProjectionDefaults.ClassRates) { ["crypto"] = 0.1m };

        Assert.Equal("classRates.crypto", Only(With(a => a with { ClassRates = rates })));
    }

    [Theory]
    [InlineData(0.51)]
    [InlineData(-0.51)]
    public void Rate_above_50_percent_is_refused(double rate)
        => Assert.Equal("classRates.equity", Only(WithRate(AllocationClasses.Equity, (decimal)rate)));

    [Fact]
    public void Rate_at_the_limits_is_accepted()
    {
        Assert.Empty(ProjectionRules.Check(WithRate(AllocationClasses.Equity, 0.5m)));
        Assert.Empty(ProjectionRules.Check(WithRate(AllocationClasses.Equity, -0.5m)));
    }

    [Theory]
    [InlineData(-0.06)]
    [InlineData(0.21)]
    public void Inflation_outside_minus5_to_20_is_refused(double inflation)
        => Assert.Equal("inflation", Only(With(a => a with { Inflation = (decimal)inflation })));

    [Fact]
    public void Unknown_savings_class_is_refused()
        => Assert.Equal("savingsClass", Only(With(a => a with { SavingsClass = "gold" })));
}
