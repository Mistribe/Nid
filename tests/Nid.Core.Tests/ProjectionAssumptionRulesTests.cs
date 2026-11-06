using Nid.Core.Application.Projection;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

public class ProjectionAssumptionRulesTests
{
    private static readonly LocalDate Today = new(2026, 11, 6);

    private static ProjectionAssumptions With(Func<ProjectionAssumptions, ProjectionAssumptions> change) => change(ProjectionDefaults.Assumptions);

    private static ProjectionAssumptions WithRate(string @class, decimal rate)
        => With(a => a with { ClassRates = new Dictionary<string, decimal>(a.ClassRates) { [@class] = rate } });

    private static string Only(ProjectionAssumptions a) => Assert.Single(ProjectionRules.Check(a, Today)).Field;

    [Fact]
    public void Defaults_are_valid() => Assert.Empty(ProjectionRules.Check(ProjectionDefaults.Assumptions, Today));

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
        Assert.Empty(ProjectionRules.Check(WithRate(AllocationClasses.Equity, 0.5m), Today));
        Assert.Empty(ProjectionRules.Check(WithRate(AllocationClasses.Equity, -0.5m), Today));
    }

    [Theory]
    [InlineData(-0.06)]
    [InlineData(0.21)]
    public void Inflation_outside_minus5_to_20_is_refused(double inflation)
        => Assert.Equal("inflation", Only(With(a => a with { Inflation = (decimal)inflation })));

    [Fact]
    public void Unknown_savings_class_is_refused()
        => Assert.Equal("savingsClass", Only(With(a => a with { SavingsClass = "gold" })));

    private static ProjectionAssumptions Inflation(int year, decimal rate)
        => With(a => a with { InflationByYear = new Dictionary<int, decimal> { [year] = rate } });

    private static ProjectionAssumptions ClassRate(string @class, int year, decimal rate) => With(a => a with
    {
        ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>> { [@class] = new Dictionary<int, decimal> { [year] = rate } },
    });

    public static TheoryData<string, string> OutOfRange => new()
    {
        { "past year", "inflationByYear.2025" },
        { "year beyond current + 4", "inflationByYear.2031" },
        { "inflation 21 %", "inflationByYear.2026" },
        { "inflation -6 %", "inflationByYear.2027" },
        { "class without yearly rates", "classRatesByYear.equity" },
        { "cash rate 51 %", "classRatesByYear.cash.2027" },
        { "cash past year", "classRatesByYear.cash.2025" },
        { "money market year beyond current + 4", "classRatesByYear.money_market.2031" },
        { "income growth 11 %", "incomeGrowth" },
        { "income growth -6 %", "incomeGrowth" },
    };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void Check_refuses_out_of_range_yearly_values(string @case, string field)
    {
        var a = @case switch
        {
            "past year" => Inflation(2025, 0.02m),
            "year beyond current + 4" => Inflation(2031, 0.02m),
            "inflation 21 %" => Inflation(2026, 0.21m),
            "inflation -6 %" => Inflation(2027, -0.06m),
            "class without yearly rates" => ClassRate(AllocationClasses.Equity, 2027, 0.03m),
            "cash rate 51 %" => ClassRate(AllocationClasses.Cash, 2027, 0.51m),
            "cash past year" => ClassRate(AllocationClasses.Cash, 2025, 0.03m),
            "money market year beyond current + 4" => ClassRate(AllocationClasses.MoneyMarket, 2031, 0.03m),
            "income growth 11 %" => With(x => x with { IncomeGrowth = 0.11m }),
            "income growth -6 %" => With(x => x with { IncomeGrowth = -0.06m }),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        Assert.Equal(field, Only(a));
    }

    [Fact]
    public void Yearly_values_at_the_limits_are_accepted()
    {
        var a = With(x => x with
        {
            InflationByYear = new Dictionary<int, decimal> { [2026] = 0.2m, [2030] = -0.05m },
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>>
            {
                [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2026] = 0.5m },
                [AllocationClasses.MoneyMarket] = new Dictionary<int, decimal> { [2030] = -0.5m },
            },
            IncomeGrowth = 0.1m,
        });

        Assert.Empty(ProjectionRules.Check(a, Today));
        Assert.Empty(ProjectionRules.Check(a with { IncomeGrowth = -0.05m }, Today));
    }

    [Fact]
    public void WithoutPastYears_drops_the_years_before_this_one()
    {
        var a = With(x => x with
        {
            InflationByYear = new Dictionary<int, decimal> { [2025] = 0.03m, [2026] = 0.025m },
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>>
            {
                [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2025] = 0.04m, [2027] = 0.03m },
            },
        });

        var kept = ProjectionRules.WithoutPastYears(a, Today);

        Assert.Equal(new Dictionary<int, decimal> { [2026] = 0.025m }, kept.InflationByYear);
        Assert.Equal(new Dictionary<int, decimal> { [2027] = 0.03m }, kept.ClassRatesByYear![AllocationClasses.Cash]);
        Assert.Empty(ProjectionRules.Check(kept, Today));
    }

    [Fact]
    public void Scenario_overrides_are_refused_under_assumptions()
    {
        var overrides = new ProjectionOverrides(null, null, null, null, null,
            InflationByYear: new Dictionary<int, decimal> { [2031] = 0.02m });
        var doc = new ProjectionScenario(1, new ScenarioHorizon(10, null), overrides, []);

        var refused = ProjectionScenarioRefusals.Of(doc, ProjectionDefaults.Assumptions, Today);

        Assert.Equal("assumptions.inflationByYear.2031", Assert.Single(FieldErrors.Of(refused!)!).Path);
    }

    [Fact]
    public void Scenario_is_not_refused_for_a_past_year_of_the_household()
    {
        var household = Inflation(2025, 0.03m);
        var doc = new ProjectionScenario(1, new ScenarioHorizon(10, null), new ProjectionOverrides(null, 0.03m, null, null, null), []);

        Assert.Null(ProjectionScenarioRefusals.Of(doc, household, Today));
    }

    [Fact]
    public void A_null_class_table_is_absent()
    {
        var a = With(x => x with { ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>> { [AllocationClasses.Cash] = null! } });

        Assert.Empty(ProjectionRules.Check(a, Today));
        Assert.Null(ProjectionRules.WithoutPastYears(a, Today).ClassRatesByYear);
    }

    [Fact]
    public void WithoutPastYears_drops_emptied_tables()
    {
        var a = With(x => x with
        {
            InflationByYear = new Dictionary<int, decimal> { [2025] = 0.03m },
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>>
            {
                [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2025] = 0.04m },
                [AllocationClasses.MoneyMarket] = new Dictionary<int, decimal> { [2026] = 0.03m },
            },
        });

        var kept = ProjectionRules.WithoutPastYears(a, Today);

        Assert.Null(kept.InflationByYear);
        Assert.Equal([AllocationClasses.MoneyMarket], kept.ClassRatesByYear!.Keys);
        Assert.Null(ProjectionRules.WithoutPastYears(kept with
        {
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>> { [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2024] = 0.01m } },
        }, Today).ClassRatesByYear);
    }
}
