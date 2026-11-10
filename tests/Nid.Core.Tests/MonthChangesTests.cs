using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #415: « Depuis le mois dernier », its change and at most three causes, the largest first.</summary>
public class MonthChangesTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);
    private static readonly LocalDate Since = Today.PlusMonths(-1);
    private static readonly Currency Usd = Currency.Usd;

    private static Dictionary<Currency, decimal> Eur(decimal amount) => new() { [Currency.Eur] = amount };

    private static MonthFigureView? Figure(params MonthPart[] parts)
        => MonthChanges.Figure(parts, hadBefore: true, Currency.Eur, RateTable.Empty(Today, 7), Since, Today);

    [Fact]
    public void The_change_is_now_minus_before_and_keeps_the_three_largest_causes()
    {
        var figure = Figure(
            new MonthPart("subscriptions", Eur(100m), Eur(101m)),
            new MonthPart("loans", Eur(900m), Eur(900m)),
            new MonthPart("tax", Eur(200m), Eur(150m)),
            new MonthPart("childcare", Eur(300m), Eur(320m)),
            new MonthPart("property_costs", Eur(40m), Eur(42m)))!;

        Assert.Equal(("1540.00", "1513.00", "-27.00"), (figure.Before.Amount, figure.Now.Amount, figure.Change.Amount));
        Assert.Equal([("tax", "-50.00"), ("childcare", "20.00"), ("property_costs", "2.00")], figure.Causes.Select(c => (c.Key, c.Change.Amount)));
    }

    [Fact]
    public void A_cause_under_a_cent_is_left_out_and_parts_under_one_key_add_up()
    {
        var figure = Figure(
            new MonthPart("income", Eur(1000m), Eur(1000.004m)),
            new MonthPart("rents", Eur(500m), Eur(600m)),
            new MonthPart("rents", Eur(0m), Eur(-50m)))!;

        Assert.Equal([("rents", "50.00")], figure.Causes.Select(c => (c.Key, c.Change.Amount)));
    }

    [Fact]
    public void Nothing_a_month_ago_hides_the_figure()
        => Assert.Null(MonthChanges.Figure([new MonthPart("subscriptions", Eur(0m), Eur(10m))], hadBefore: false, Currency.Eur, RateTable.Empty(Today, 7), Since, Today));

    [Fact]
    public void A_missing_rate_hides_the_figure()
        => Assert.Null(Figure(new MonthPart("subscriptions", new Dictionary<Currency, decimal> { [Usd] = 10m }, Eur(10m))));

    [Fact]
    public void Each_date_converts_at_its_own_rate()
    {
        var rates = new RateTable(
            [new FxRate(Since, Currency.Eur, 0.8m, FxRateSource.Ecb), new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, maxGapDays: 7);
        var usd = new Dictionary<Currency, decimal> { [Usd] = 100m };

        var figure = MonthChanges.Figure([new MonthPart("investments.value", usd, usd)], true, Currency.Eur, rates, Since, Today)!;

        Assert.Equal(("80.00", "90.00", "10.00"), (figure.Before.Amount, figure.Now.Amount, figure.Change.Amount));
    }

    [Fact]
    public void A_net_worth_part_splits_into_what_was_paid_in_and_the_rest()
    {
        // A loan: 480 of capital repaid lowers the debt by 480; the property's value is unchanged.
        var before = new NetWorthPoint(Since, Eur(200_000m - 100_000m), Eur(20_000m), 2);
        var now = new NetWorthPoint(Today, Eur(200_000m - 99_520m), Eur(20_480m), 2);

        var figure = Figure([.. MonthChanges.NetWorthParts("real_estate", before, now)])!;

        Assert.Equal("480.00", figure.Change.Amount);
        Assert.Equal([("real_estate.paid_in", "480.00")], figure.Causes.Select(c => (c.Key, c.Change.Amount)));
    }

    [Fact]
    public void A_purchase_is_its_own_part()
    {
        // A home bought for 250 000 with a loan of 200 000: the net worth gains 50 000, moved, not a change of value.
        var before = new NetWorthPoint(Since, Eur(0m), Eur(0m), 0);
        var now = new NetWorthPoint(Today, Eur(250_000m - 200_000m), Eur(0m), 2, Eur(50_000m));

        var figure = Figure([.. MonthChanges.NetWorthParts("real_estate", before, now)])!;

        Assert.Equal("50000.00", figure.Change.Amount);
        Assert.Equal([("real_estate.moved", "50000.00")], figure.Causes.Select(c => (c.Key, c.Change.Amount)));
    }

    [Fact]
    public void The_digest_words_carry_directions_and_cause_codes_never_an_amount()
    {
        static DashboardMoneyView M(string amount) => new(amount, "EUR");
        var view = new MonthChangesView("EUR", Since,
            new MonthFigureView(M("1000.00"), M("2200.00"), M("1200.00"), [new("investments.value", M("1680.00")), new("real_estate.value", M("-480.00"))]),
            new MonthFigureView(M("100.00"), M("100.00"), M("0.00"), []),
            null);

        var words = CoreNotificationSource.MonthChangeParams(view);

        Assert.Equal(new Dictionary<string, string> { ["net_worth"] = "up|investments.value+|real_estate.value-" }, words);
    }
}
