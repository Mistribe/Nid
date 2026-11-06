using Nid.Core.Contracts;

namespace Nid.Core.Tests;

/// <summary>Net worth projection spec §6: a scenario's overrides over the household's assumptions.</summary>
public class ProjectionOverridesTests
{
    private static readonly ProjectionAssumptions Household = ProjectionDefaults.Assumptions;

    [Fact]
    public void ProjectionOverrides_apply_merges_class_rates()
    {
        var overrides = new ProjectionOverrides(new Dictionary<string, decimal> { [AllocationClasses.Equity] = 0.08m }, null, null, null, null);

        var applied = overrides.Apply(Household);

        Assert.Equal(0.08m, applied.ClassRates[AllocationClasses.Equity]);
        Assert.Equal(Household.ClassRates[AllocationClasses.Cash], applied.ClassRates[AllocationClasses.Cash]);
        Assert.Equal(Household.ClassRates.Count, applied.ClassRates.Count);
        Assert.Equal(Household.Inflation, applied.Inflation);
        Assert.Equal(Household.SavingsClass, applied.SavingsClass);
        Assert.Equal(Household.FreedPaymentsToSavings, applied.FreedPaymentsToSavings);
    }

    [Fact]
    public void Every_field_is_replaced_only_when_set()
    {
        var overrides = new ProjectionOverrides(null, 0.03m, 400m, AllocationClasses.Equity, false);

        var applied = overrides.Apply(Household);

        Assert.Equal(0.03m, applied.Inflation);
        Assert.Equal(400m, applied.MonthlySavings);
        Assert.Equal(AllocationClasses.Equity, applied.SavingsClass);
        Assert.False(applied.FreedPaymentsToSavings);
        Assert.Equal(Household.ClassRates, applied.ClassRates);
    }

    [Fact]
    public void Overrides_merge_yearly_tables()
    {
        var household = Household with
        {
            InflationByYear = new Dictionary<int, decimal> { [2026] = 0.025m, [2027] = 0.022m },
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>>
            {
                [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2026] = 0.03m },
                [AllocationClasses.MoneyMarket] = new Dictionary<int, decimal> { [2026] = 0.035m },
            },
            IncomeGrowth = 0.005m,
        };

        // C18: an override of the long-term inflation only keeps the household's yearly tables.
        var longTerm = new ProjectionOverrides(null, 0.03m, null, null, null).Apply(household);
        Assert.Equal(0.03m, longTerm.Inflation);
        Assert.Same(household.InflationByYear, longTerm.InflationByYear);
        Assert.Equal(household.ClassRatesByYear, longTerm.ClassRatesByYear);
        Assert.Equal(0.005m, longTerm.IncomeGrowth);

        var yearly = new ProjectionOverrides(null, null, null, null, null,
            InflationByYear: new Dictionary<int, decimal> { [2028] = 0.01m },
            ClassRatesByYear: new Dictionary<string, IReadOnlyDictionary<int, decimal>>
            {
                [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2027] = 0.04m },
            },
            IncomeGrowth: 0.01m).Apply(household);
        Assert.Equal(new Dictionary<int, decimal> { [2028] = 0.01m }, yearly.InflationByYear);
        Assert.Equal(new Dictionary<int, decimal> { [2027] = 0.04m }, yearly.ClassRatesByYear![AllocationClasses.Cash]);
        Assert.Equal(new Dictionary<int, decimal> { [2026] = 0.035m }, yearly.ClassRatesByYear[AllocationClasses.MoneyMarket]);
        Assert.Equal(0.01m, yearly.IncomeGrowth);
    }

    [Fact]
    public void A_null_class_table_in_an_override_is_absent()
    {
        var household = Household with
        {
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>> { [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2026] = 0.03m } },
        };
        var overrides = new ProjectionOverrides(null, null, null, null, null,
            ClassRatesByYear: new Dictionary<string, IReadOnlyDictionary<int, decimal>> { [AllocationClasses.Cash] = null!, [AllocationClasses.MoneyMarket] = null! });

        var applied = overrides.Apply(household);

        Assert.Equal(new Dictionary<int, decimal> { [2026] = 0.03m }, applied.ClassRatesByYear![AllocationClasses.Cash]);
        Assert.False(applied.ClassRatesByYear.ContainsKey(AllocationClasses.MoneyMarket));
    }
}
