using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using static Nid.Subscriptions.Tests.TestData;

namespace Nid.Subscriptions.Tests;

public class TimelineQueriesTests
{
    private static Timeline Monthly(LocalDate today, LocalDate horizon)
        => SubscriptionTimeline.Build(
            Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], []), today, horizon).Ok();

    private static DateRange Range(LocalDate start, LocalDate? endBefore)
        => DateRange.Create(start, endBefore).Ok();

    [Fact]
    public void Coverage_answers_for_a_date_inside_it()
    {
        var timeline = Monthly(new LocalDate(2025, 3, 1), new LocalDate(2025, 6, 1));

        Assert.True(timeline.IsCoveredOn(new LocalDate(2025, 2, 14)));
        Assert.False(timeline.IsCoveredOn(new LocalDate(2024, 12, 31)));
    }

    [Fact]
    public void Expiry_is_the_end_of_the_last_period()
    {
        var timeline = Monthly(new LocalDate(2025, 1, 1), new LocalDate(2025, 4, 1));

        // The charge that would fall on the horizon is not emitted, so coverage ends there.
        Assert.Equal(new LocalDate(2025, 4, 1), timeline.ExpiresOn());
    }

    [Fact]
    public void The_next_charge_is_the_first_one_after_today()
    {
        var timeline = Monthly(new LocalDate(2025, 3, 15), new LocalDate(2025, 7, 1));

        Assert.Equal(new LocalDate(2025, 4, 1), timeline.NextCharge()!.Date);
    }

    [Fact]
    public void A_cancelled_subscription_has_no_next_charge()
    {
        var timeline = SubscriptionTimeline.Build(
            Input([Phase(new LocalDate(2025, 1, 1), null, 10m)], [], autoRenew: false),
            new LocalDate(2025, 3, 1), new LocalDate(2025, 7, 1)).Ok();

        Assert.Null(timeline.NextCharge());
    }

    [Fact]
    public void Cash_cost_counts_the_charges_inside_the_window()
    {
        var timeline = Monthly(new LocalDate(2025, 6, 1), new LocalDate(2025, 6, 1));

        var cost = timeline.CashCost(Range(new LocalDate(2025, 1, 1), new LocalDate(2025, 4, 1)));

        Assert.Equal(30m, cost.For(Currency.Eur)!.Value.Amount);
    }

    [Fact]
    public void Cash_cost_can_exclude_projections()
    {
        var timeline = Monthly(new LocalDate(2025, 2, 15), new LocalDate(2025, 6, 1));

        var all = timeline.CashCost(Range(new LocalDate(2025, 1, 1), new LocalDate(2025, 5, 1)));
        var real = timeline.CashCost(Range(new LocalDate(2025, 1, 1), new LocalDate(2025, 5, 1)),
                                     includeProjected: false);

        Assert.Equal(40m, all.For(Currency.Eur)!.Value.Amount);
        Assert.Equal(20m, real.For(Currency.Eur)!.Value.Amount);
    }

    [Fact]
    public void Amortized_cost_is_additive_across_a_split()
    {
        var timeline = Monthly(new LocalDate(2026, 1, 1), new LocalDate(2025, 7, 1));
        var a = new LocalDate(2025, 1, 1);
        var split = new LocalDate(2025, 3, 12);
        var b = new LocalDate(2025, 6, 1);

        var whole = timeline.AmortizedCost(Range(a, b)).For(Currency.Eur)!.Value.Amount;
        var first = timeline.AmortizedCost(Range(a, split)).For(Currency.Eur)!.Value.Amount;
        var second = timeline.AmortizedCost(Range(split, b)).For(Currency.Eur)!.Value.Amount;

        Assert.Equal(whole, first + second);
    }

    [Theory]
    [InlineData(2025, 2, 3)]
    [InlineData(2025, 3, 20)]
    [InlineData(2025, 5, 1)]
    public void Amortized_cost_is_additive_across_any_split(int year, int month, int day)
    {
        var timeline = Monthly(new LocalDate(2026, 1, 1), new LocalDate(2025, 7, 1));
        var a = new LocalDate(2025, 1, 1);
        var b = new LocalDate(2025, 6, 1);
        var split = new LocalDate(year, month, day);

        var whole = timeline.AmortizedCost(Range(a, b)).For(Currency.Eur)!.Value.Amount;
        var parts = timeline.AmortizedCost(Range(a, split)).For(Currency.Eur)!.Value.Amount
                    + timeline.AmortizedCost(Range(split, b)).For(Currency.Eur)!.Value.Amount;

        Assert.Equal(whole, parts);
    }

    [Fact]
    public void A_charge_amortizes_to_exactly_its_basis_over_all_of_its_coverage()
    {
        var timeline = SubscriptionTimeline.Build(
            Input([], [Purchase(new LocalDate(2025, 6, 22), 179.86m, PurchaseKind.PrepaidTerm,
                                [Grant(Months(24), CoverageSource.Paid, new LocalDate(2025, 6, 22)),
                                 Grant(Months(6), CoverageSource.Bonus)])]),
            new LocalDate(2030, 1, 1), new LocalDate(2030, 1, 1)).Ok();

        var cost = timeline.AmortizedCost(Range(new LocalDate(2025, 6, 22), new LocalDate(2027, 12, 22)));

        Assert.Equal(179.86m, cost.For(Currency.Eur)!.Value.Amount);
    }

    [Fact]
    public void Two_identically_dated_coverage_periods_still_reconstruct_the_basis()
    {
        // An explicit grant overlapping existing coverage is a warning, not an error, so one purchase can
        // produce two periods with the same dates. They must not both be credited with the same extra units.
        var timeline = SubscriptionTimeline.Build(
            Input([], [Purchase(new LocalDate(2025, 1, 1), 10m, PurchaseKind.PrepaidTerm,
                                [Grant(Months(1), CoverageSource.Paid, new LocalDate(2025, 1, 1)),
                                 Grant(Months(1), CoverageSource.Bonus, new LocalDate(2025, 1, 1))])]),
            new LocalDate(2025, 6, 1), new LocalDate(2025, 6, 1)).Ok();

        var cost = timeline.AmortizedCost(Range(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1)));

        Assert.Equal(10m, cost.For(Currency.Eur)!.Value.Amount);
    }

    [Fact]
    public void Free_days_count_everything_that_is_not_paid()
    {
        var timeline = SubscriptionTimeline.Build(
            Input([], [Purchase(new LocalDate(2025, 1, 1), 0m, PurchaseKind.Gift,
                                [Grant(Days(10), CoverageSource.Gift, new LocalDate(2025, 1, 1))])]),
            new LocalDate(2025, 6, 1), new LocalDate(2025, 6, 1)).Ok();

        Assert.Equal(10, timeline.FreeDays(Range(new LocalDate(2025, 1, 1), new LocalDate(2025, 2, 1))));
    }

    [Fact]
    public void A_mixed_currency_subscription_reports_one_total_per_currency()
    {
        var usd = new Purchase(PurchaseId.New(), new LocalDate(2025, 1, 1), "usd",
                               Money.Create(20m, Currency.Usd).Ok(), Household, PurchaseKind.PrepaidTerm,
                               [Grant(Months(1), CoverageSource.Paid, new LocalDate(2025, 1, 1))], null);
        var timeline = SubscriptionTimeline.Build(
            Input([Phase(new LocalDate(2025, 3, 1), null, 10m, maxCycles: 1)], [usd]),
            new LocalDate(2025, 6, 1), new LocalDate(2025, 6, 1)).Ok();

        var cost = timeline.CashCost(Range(new LocalDate(2025, 1, 1), new LocalDate(2025, 6, 1)));

        Assert.Equal(20m, cost.For(Currency.Usd)!.Value.Amount);
        Assert.Equal(10m, cost.For(Currency.Eur)!.Value.Amount);
    }
}
