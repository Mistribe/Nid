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
}
