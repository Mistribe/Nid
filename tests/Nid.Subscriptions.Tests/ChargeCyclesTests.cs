using Nid.Core.Contracts;
using Duration = Nid.Core.Contracts.Duration;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using static Nid.Subscriptions.Tests.TestData;

namespace Nid.Subscriptions.Tests;

public class ChargeCyclesTests
{
    [Theory]
    [InlineData(1, DurationUnit.Month, false)]
    [InlineData(2, DurationUnit.Month, true)]
    [InlineData(1, DurationUnit.Year, true)]
    [InlineData(5, DurationUnit.Week, true)]
    [InlineData(4, DurationUnit.Week, false)]
    [InlineData(32, DurationUnit.Day, true)]
    [InlineData(31, DurationUnit.Day, false)]
    public void Longer_than_a_month(int count, DurationUnit unit, bool expected)
        => Assert.Equal(expected, ChargeCycles.LongerThanAMonth(Duration.Create(count, unit).Ok()));

    private static readonly LocalDate Today = new(2026, 1, 1);

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static List<(LocalDate, string, decimal)> Changes(IReadOnlyList<BillingPhase> phases, IReadOnlyList<Purchase>? purchases = null)
    {
        var input = Input(phases, purchases ?? []);
        var timeline = SubscriptionTimeline.Build(input, Today, Today.PlusYears(3)).Ok();
        return ChargeCycles.PriceChanges(input, timeline, Today, Today.PlusYears(2))
            .Select(x => (x.Charge.Date, x.Kind, x.Charge.Price.Amount)).ToList();
    }

    [Fact]
    public void A_free_month_phase_then_a_monthly_price_ends_a_trial_once()
        => Assert.Equal([(D(2026, 2, 1), UpcomingKinds.SubscriptionTrialEnd, 10m)],
            Changes([Phase(D(2026, 1, 1), D(2026, 2, 1), 0m), Phase(D(2026, 2, 1), null, 10m)]));

    [Theory]
    [InlineData(CoverageSource.Trial)]
    [InlineData(CoverageSource.Gift)]
    [InlineData(CoverageSource.Bonus)]
    public void A_free_coverage_then_a_monthly_price_ends_a_trial(CoverageSource source)
        => Assert.Equal([(D(2026, 2, 1), UpcomingKinds.SubscriptionTrialEnd, 10m)],
            Changes([Phase(D(2026, 1, 1), null, 10m)], [Purchase(D(2026, 1, 1), 0m, PurchaseKind.Gift, [Grant(Months(1), source)])]));

    [Fact]
    public void An_included_coverage_is_not_a_trial()
        => Assert.Empty(Changes([Phase(D(2026, 1, 1), null, 10m)],
            [Purchase(D(2026, 1, 1), 0m, PurchaseKind.Bundle, [Grant(Months(1), CoverageSource.Included)])]));

    [Fact]
    public void A_launch_price_ending_is_a_price_increase()
        => Assert.Equal([(D(2026, 7, 1), UpcomingKinds.SubscriptionPriceIncrease, 12m)],
            Changes([Phase(D(2026, 1, 1), D(2026, 7, 1), 5m), Phase(D(2026, 7, 1), null, 12m)]));

    [Fact]
    public void A_price_decrease_raises_nothing()
        => Assert.Empty(Changes([Phase(D(2026, 1, 1), D(2026, 7, 1), 12m), Phase(D(2026, 7, 1), null, 5m)]));

    [Fact]
    public void Yearly_billing_at_the_same_monthly_cost_raises_nothing()
        => Assert.Empty(Changes([Phase(D(2026, 1, 1), D(2026, 7, 1), 10m), Phase(D(2026, 7, 1), null, 120m, every: Duration.Create(1, DurationUnit.Year).Ok())]));

    [Fact]
    public void Yearly_billing_dearer_per_month_is_a_price_increase()
        => Assert.Equal([(D(2026, 7, 1), UpcomingKinds.SubscriptionPriceIncrease, 130m)],
            Changes([Phase(D(2026, 1, 1), D(2026, 7, 1), 10m), Phase(D(2026, 7, 1), null, 130m, every: Duration.Create(1, DurationUnit.Year).Ok())]));
}
