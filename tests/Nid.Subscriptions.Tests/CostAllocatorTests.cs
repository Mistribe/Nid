using NodaTime;
using Nid.Core.Contracts;
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

    private static Charge ChargeFor(Money basis, IReadOnlyList<CoveragePeriod> coverage)
        => new(coverage[0].Start, basis, Household, new ChargeOrigin.FromPurchase(PurchaseId.New()),
               coverage, IsProjected: false, Allocation: CostAllocator.Allocate(basis, coverage));

    [Fact]
    public void AmountIn_reconstructs_the_basis_over_every_period_of_a_multi_period_charge()
    {
        // 59 + 31 = 90 days. 100.007 leaves a remainder of 80 units, more than the 59 days of the first
        // period, so the extra units spill into the second one and its offset is actually exercised.
        var paid = Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 3, 1));
        var bonus = Period(new LocalDate(2025, 3, 1), new LocalDate(2025, 4, 1));
        var coverage = new[] { paid, bonus };
        var basis = Eur(100.007m);
        var charge = ChargeFor(basis, coverage);

        Assert.Equal(80, charge.Allocation.HighDays);
        Assert.True(charge.Allocation.HighDays > paid.Days);

        var total = CostAllocator.AmountIn(charge, 0, paid.Start, null)
                  + CostAllocator.AmountIn(charge, 1, bonus.Start, null);

        Assert.Equal(basis.Amount, total);
    }

    [Fact]
    public void AmountIn_tells_two_identically_dated_periods_apart()
    {
        // An explicit grant is allowed to overlap another, so one charge can produce two periods with the
        // same dates. Matching a period by value would credit the second with the first one's extra units.
        var first = Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1));
        var second = Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1));
        var coverage = new[] { first, second };
        var basis = Eur(10m);
        var charge = ChargeFor(basis, coverage);

        var total = CostAllocator.AmountIn(charge, 0, first.Start, null)
                  + CostAllocator.AmountIn(charge, 1, second.Start, null);

        Assert.Equal(basis.Amount, total);
    }

    [Fact]
    public void AmountIn_is_additive_across_a_window_split_inside_a_period()
    {
        var period = Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1));
        var coverage = new[] { period };
        var basis = Eur(15.17m);
        var charge = ChargeFor(basis, coverage);
        var splitAt = new LocalDate(2025, 1, 15);

        var whole = CostAllocator.AmountIn(charge, 0, period.Start, null);
        var firstHalf = CostAllocator.AmountIn(charge, 0, period.Start, splitAt);
        var secondHalf = CostAllocator.AmountIn(charge, 0, splitAt, null);

        Assert.Equal(whole, firstHalf + secondHalf);
        Assert.Equal(basis.Amount, whole);
    }

    [Fact]
    public void A_negative_basis_round_trips_exactly()
    {
        // -15.17 over 31 days does not divide evenly, so the remainder-normalisation in Allocate is exercised.
        var period = Period(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1));
        var coverage = new[] { period };
        var basis = Eur(-15.17m);
        var charge = ChargeFor(basis, coverage);

        var total = CostAllocator.AmountIn(charge, 0, period.Start, null);

        Assert.Equal(basis.Amount, total);
    }
}
