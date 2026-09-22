using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Tests;

public class ContractsTests
{
    [Fact]
    public void A_phase_without_covers_falls_back_to_its_billing_period()
    {
        var every = Duration.Create(1, DurationUnit.Month).Ok();
        var phase = new BillingPhase(
            new BillingPhaseId(Guid.NewGuid()),
            new LocalDate(2025, 1, 31),
            null,
            Money.Create(9.99m, Currency.Eur).Ok(),
            every,
            Covers: null,
            MaxCycles: null,
            new Payer(PayerType.Household, null));

        Assert.Equal(every, phase.CoverageEvery);
    }

    [Fact]
    public void A_phase_covers_what_it_says_when_it_says_it()
    {
        var covers = Duration.Create(3, DurationUnit.Month).Ok();
        var phase = new BillingPhase(
            new BillingPhaseId(Guid.NewGuid()),
            new LocalDate(2025, 1, 1),
            null,
            Money.Create(30m, Currency.Eur).Ok(),
            Duration.Create(1, DurationUnit.Month).Ok(),
            covers,
            MaxCycles: null,
            new Payer(PayerType.Household, null));

        Assert.Equal(covers, phase.CoverageEvery);
    }

    [Fact]
    public void A_coverage_period_counts_its_days_half_open()
    {
        var period = new CoveragePeriod(
            new LocalDate(2025, 1, 1),
            new LocalDate(2025, 2, 1),
            CoverageSource.Paid,
            new CoverageOrigin.FromPhase(new BillingPhaseId(Guid.NewGuid()), 0),
            IsProjected: false);

        Assert.Equal(31, period.Days);
        Assert.True(period.Contains(new LocalDate(2025, 1, 31)));
        Assert.False(period.Contains(new LocalDate(2025, 2, 1)));
    }

    [Fact]
    public void A_cost_breakdown_returns_nothing_for_a_currency_it_does_not_hold()
    {
        var breakdown = new CostBreakdown([Money.Create(10m, Currency.Eur).Ok()]);

        Assert.Equal(10m, breakdown.For(Currency.Eur)!.Value.Amount);
        Assert.Null(breakdown.For(Currency.Usd));
    }
}
