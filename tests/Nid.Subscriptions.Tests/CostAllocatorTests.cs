using NodaTime;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;

namespace Nid.Subscriptions.Tests;

public class CostAllocatorTests
{
    private static CoveragePeriod Period(LocalDate start, LocalDate endBefore)
        => new(start, endBefore, CoverageSource.Paid,
               new CoverageOrigin.FromPhase(BillingPhaseId.New(), 0), IsProjected: false);

    [Fact]
    public void An_allocation_tiles_its_basis_exactly()
    {
        var coverage = new[] { Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1)) };

        var allocation = CostAllocator.Allocate(Eur(15.17m), coverage);

        Assert.Equal(31, allocation.Days);
        Assert.Equal(15.17m, allocation.PerDay * allocation.Days + allocation.HighDays * CostAllocator.Unit);
    }

    [Theory]
    [InlineData("15.17", 31)]
    [InlineData("179.86", 913)]
    [InlineData("9.99", 7)]
    [InlineData("0.01", 365)]
    [InlineData("15.1712", 31)]
    public void Every_basis_tiles_exactly(string basis, int days)
    {
        var amount = decimal.Parse(basis, System.Globalization.CultureInfo.InvariantCulture);
        var coverage = new[] { Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 1, 1).PlusDays(days)) };

        var allocation = CostAllocator.Allocate(Eur(amount), coverage);

        Assert.Equal(amount, allocation.PerDay * allocation.Days + allocation.HighDays * CostAllocator.Unit);
    }

    [Fact]
    public void A_free_grant_costs_nothing()
    {
        var coverage = new[] { Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 4, 1)) };

        var allocation = CostAllocator.Allocate(Eur(0m), coverage);

        Assert.Equal(0m, allocation.PerDay);
        Assert.Equal(0, allocation.HighDays);
    }

    [Fact]
    public void A_basis_with_no_coverage_allocates_nothing()
    {
        var allocation = CostAllocator.Allocate(Eur(20m), []);

        Assert.Equal(0, allocation.Days);
        Assert.Equal(0m, allocation.PerDay);
    }

    [Fact]
    public void Bonus_days_lower_the_daily_rate()
    {
        var paidOnly = CostAllocator.Allocate(
            Eur(180m), [Period(new LocalDate(2025, 1, 1), new LocalDate(2027, 1, 1))]);
        var paidAndBonus = CostAllocator.Allocate(
            Eur(180m),
            [Period(new LocalDate(2025, 1, 1), new LocalDate(2027, 1, 1)),
             Period(new LocalDate(2027, 1, 1), new LocalDate(2027, 7, 1))]);

        Assert.True(paidAndBonus.PerDay < paidOnly.PerDay);
    }
}
