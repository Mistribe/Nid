using System.Globalization;
using Nid.Core.Contracts;
using Nid.Subscriptions.Application;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain;
using NodaTime;
using static Nid.Subscriptions.Tests.TestData;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Tests;

public class MonthChargesTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static readonly YearMonth May = new(2026, 5);

    private static BuiltSubscription Built(
        IReadOnlyList<BillingPhase> phases, IReadOnlyList<Purchase>? purchases = null, ItemId? id = null, string name = "s")
    {
        var input = new SubscriptionInput(new SubscriptionHeader(id ?? ItemId.New(), true, null), phases, purchases ?? []);
        return new BuiltSubscription(input, SubscriptionTimeline.Build(input, Today, Today.PlusMonths(12)).Ok(), name, [], null);
    }

    private static SubscriptionCharges Charges(YearMonth month, bool beyond = false, params BuiltSubscription[] set)
        => MonthCharges.Build(set, month, Currency.Eur, RateTable.Empty(Today, 7), beyond);

    private static decimal? Amount(MoneyView? money)
        => money is null ? null : decimal.Parse(money.Amount, CultureInfo.InvariantCulture);

    [Fact]
    public void A_month_holds_monthly_and_yearly_charges()
    {
        var yearly = Duration.Create(1, DurationUnit.Year).Ok();
        var result = Charges(May, false,
            Built([Phase(new LocalDate(2025, 1, 28), null, 17.20m)], name: "monthly"),
            Built([Phase(new LocalDate(2025, 5, 22), null, 216.00m, every: yearly)], name: "yearly"));

        Assert.Equal("2026-05", result.Month);
        Assert.Equal([new LocalDate(2026, 5, 22), new LocalDate(2026, 5, 28)], result.Charges.Select(c => c.Date));
        Assert.Equal(233.20m, Amount(result.Total));
    }

    [Fact]
    public void A_shared_purchase_is_one_line()
    {
        ItemId a = ItemId.New(), b = ItemId.New();
        var purchase = Purchase(new LocalDate(2025, 11, 5), 120m, PurchaseKind.PrepaidTerm,
            [new Grant(a, Months(12), CoverageSource.Paid, null), new Grant(b, Months(12), CoverageSource.Paid, null)]);

        var result = Charges(new YearMonth(2025, 11), false, Built([], [purchase], a), Built([], [purchase], b));

        Assert.Single(result.Charges);
        Assert.Equal(120m, Amount(result.Total));
    }

    [Fact]
    public void An_empty_month_has_a_zero_total()
    {
        var result = Charges(May);

        Assert.Empty(result.Charges);
        Assert.Equal(0m, Amount(result.Total));
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Beyond_the_horizon_is_empty_with_a_finding()
    {
        var result = Charges(new YearMonth(2040, 1), true, Built([Phase(new LocalDate(2025, 1, 28), null, 17.20m)]));

        Assert.Empty(result.Charges);
        Assert.Equal(0m, Amount(result.Total));
        Assert.Equal("subscriptions.horizon_reached", Assert.Single(result.Findings).Code);
    }

    [Fact]
    public void A_missing_rate_nulls_the_total_and_keeps_the_lines()
    {
        var usd = new BillingPhase(BillingPhaseId.New(), new LocalDate(2025, 1, 1), null, Money.Create(10m, Currency.Usd).Ok(), Months(1),
            null, null, Household);

        var result = Charges(May, false, Built([usd]));

        Assert.Null(result.Total);
        var line = Assert.Single(result.Charges);
        Assert.Null(line.Converted);
        Assert.Contains(result.Findings, f => f.Code == FxCodes.RateMissing);
    }

    [Fact]
    public void A_charge_before_today_is_paid_and_after_is_projected()
    {
        var monthly = Built([Phase(new LocalDate(2025, 9, 1), null, 9.99m)]);

        Assert.False(Assert.Single(Charges(new YearMonth(2025, 10), false, monthly).Charges).IsProjected);
        Assert.True(Assert.Single(Charges(new YearMonth(2025, 11), false, monthly).Charges).IsProjected);
    }

    [Fact]
    public void The_first_month_is_the_earliest_charge_of_the_set_ended_ones_included()
    {
        var result = Charges(May, false,
            Built([Phase(new LocalDate(2025, 3, 5), new LocalDate(2025, 5, 31), 9m)]),
            Built([Phase(new LocalDate(2025, 9, 1), null, 9.99m)]));

        Assert.Equal("2025-03", result.FirstMonth);
    }

    [Fact]
    public void With_no_subscription_the_first_month_is_null()
        => Assert.Null(Charges(May).FirstMonth);

    [Fact]
    public void Beyond_the_horizon_keeps_the_first_month()
    {
        var result = Charges(new YearMonth(2040, 1), true, Built([Phase(new LocalDate(2025, 9, 1), null, 9.99m)]));

        Assert.Empty(result.Charges);
        Assert.Equal("2025-09", result.FirstMonth);
    }

    [Fact]
    public void A_past_month_shows_the_charges_of_an_ended_subscription()
    {
        var ended = Built([Phase(new LocalDate(2025, 1, 5), new LocalDate(2025, 6, 1), 9m)]);

        var result = Charges(new YearMonth(2025, 3), false, ended);

        Assert.Equal(new LocalDate(2025, 3, 5), Assert.Single(result.Charges).Date);
        Assert.Equal(9m, Amount(result.Total));
    }

    [Fact]
    public void A_truncated_month_says_the_forecast_stops_within_it_and_a_beyond_month_says_it_is_past()
    {
        var set = Built([Phase(new LocalDate(2025, 1, 5), null, 9m)]);

        var truncated = MonthCharges.Build([set], new YearMonth(2025, 10), Currency.Eur, RateTable.Empty(Today, 7), false, truncated: true);
        var beyond = MonthCharges.Build([set], May, Currency.Eur, RateTable.Empty(Today, 7), true);

        var stops = Assert.Single(truncated.Findings, f => f.Code == MonthCharges.HorizonReachedCode);
        var past = Assert.Single(beyond.Findings, f => f.Code == MonthCharges.HorizonReachedCode);
        Assert.Contains("stops within this month", stops.Message);
        Assert.Contains("past the furthest date", past.Message);
    }
}
