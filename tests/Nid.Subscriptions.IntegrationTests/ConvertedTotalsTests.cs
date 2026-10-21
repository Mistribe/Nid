using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

public class ConvertedTotalsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Money(string Amount, string Currency);
    public sealed record FindingResponse(string Code, string? Reference);
    public sealed record Detail(List<Money> CashCost, List<Money> AmortizedCost, Money? CashCostConverted, Money? AmortizedCostConverted, bool ConvertedIsEstimate, List<FindingResponse> Findings);
    public sealed record Summary(Guid Id, Money? CashCostConverted);
    public sealed record ListResponse(
        List<Summary> Items, List<Money> HouseholdCashCost, List<Money> HouseholdAmortizedCost,
        Money? HouseholdCashCostConverted, Money? HouseholdAmortizedCostConverted, bool HouseholdConvertedIsEstimate,
        List<FindingResponse> Findings);

    /// <summary>
    /// Compares a converted amount by value, not by string: the conversion computation may keep a scale
    /// (<c>"17.0000"</c>) the source amount did not have (<c>"17"</c>) — both are the same amount
    /// (brief note on <c>MoneyView</c>'s format). The currency and the value still must match exactly.
    /// </summary>
    private static void AssertMoney(Money expected, Money? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Currency, actual.Currency);
        Assert.Equal(
            decimal.Parse(expected.Amount, CultureInfo.InvariantCulture),
            decimal.Parse(actual.Amount, CultureInfo.InvariantCulture));
    }

    private static async Task SeedAsync(NidApp app, params (LocalDate On, decimal EurPerUsd)[] rates)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IFxRateStore>()
            .WriteAsync(rates.Select(r => new FxRate(r.On, Currency.Eur, r.EurPerUsd, FxRateSource.Ecb)).ToList(), default);
    }

    /// <summary>A USD monthly phase from 2025-09-01: charges on 09-01 and 10-01 before today (2025-10-15), then projected.</summary>
    private static async Task<ItemId> UsdMonthlyAsync(NidApp app)
    {
        var id = await app.NewSubscriptionAsync("usd-monthly", app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-09-01",
            price = new { amount = "10", currency = "USD" },
            every = new { count = 1, unit = "month" },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>One USD purchase on <paramref name="date"/>, granting <paramref name="duration"/> to each of <paramref name="grantTo"/>.</summary>
    private static async Task UsdPurchaseAsync(
        NidApp app, string date, decimal amount, (int Count, string Unit) duration, params ItemId[] grantTo)
        => (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{grantTo[0].Value}/purchases", new
        {
            date, label = "usd purchase", kind = "prepaid_term",
            price = new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "USD" },
            payer = new { type = "household" },
            grants = grantTo.Select(id => new
            {
                subscriptionId = id.Value, duration = new { count = duration.Count, unit = duration.Unit }, source = "paid",
            }).ToArray(),
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Amortized_days_convert_at_the_rate_of_the_purchase_that_paid_them()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("usd-yearly", app.AdultA, Visibility.Shared);
        await UsdPurchaseAsync(app, "2025-06-01", 365m, (1, "year"), id); // 1 USD a day, 2025-06-01 to 2026-06-01
        await SeedAsync(app, (new LocalDate(2025, 6, 1), 0.9m), (new LocalDate(2025, 9, 1), 0.5m), (new LocalDate(2025, 9, 15), 0.7m));

        var detail = await app.GetAsync<Detail>(app.AdultA, $"/api/v1/subscriptions/{id.Value}?from=2025-09-01&to=2025-10-01");

        AssertMoney(new Money("30", "USD"), Assert.Single(detail.AmortizedCost));
        AssertMoney(new Money("27", "EUR"), detail.AmortizedCostConverted); // 30 × 0.9, the rate of 2025-06-01
        Assert.False(detail.ConvertedIsEstimate);
    }

    /// <summary>
    /// A 60 USD purchase on 2025-09-01 granting a month to two subscriptions, and a 10 USD monthly phase on a third
    /// (charges on 09-01, 10-01, then 11-01 projected), over September to November.
    /// </summary>
    private static async Task SharedUsdHouseholdAsync(NidApp app)
    {
        var first = await app.NewSubscriptionAsync("usd-shared-first", app.AdultA, Visibility.Shared);
        var second = await app.NewSubscriptionAsync("usd-shared-second", app.AdultA, Visibility.Shared);
        await UsdPurchaseAsync(app, "2025-09-01", 60m, (1, "month"), first, second);
        await UsdMonthlyAsync(app);
    }

    private const string HouseholdWindow = "/api/v1/subscriptions?scope=me&from=2025-09-01&to=2025-12-01";

    [Fact]
    public async Task The_household_converts_a_shared_purchase_once_in_cash_and_on_each_subscription_amortized()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SharedUsdHouseholdAsync(app);
        await SeedAsync(app, (new LocalDate(2025, 9, 1), 0.9m), (new LocalDate(2025, 10, 1), 0.8m), (new LocalDate(2025, 10, 14), 0.5m));

        var list = await app.GetAsync<ListResponse>(app.AdultA, HouseholdWindow);

        AssertMoney(new Money("90", "USD"), Assert.Single(list.HouseholdCashCost));       // 60 once + 3 × 10
        AssertMoney(new Money("150", "USD"), Assert.Single(list.HouseholdAmortizedCost)); // 60 on each + 3 × 10
        AssertMoney(new Money("76", "EUR"), list.HouseholdCashCostConverted);      // 60 × 0.9 + 10 × 0.9 + 10 × 0.8 + 10 × 0.5
        AssertMoney(new Money("130", "EUR"), list.HouseholdAmortizedCostConverted); // 2 × 60 × 0.9 + 10 × 0.9 + 10 × 0.8 + 10 × 0.5
        Assert.True(list.HouseholdConvertedIsEstimate); // the 11-01 charge is projected, at the latest rate
        Assert.DoesNotContain(list.Findings, f => f.Code == FxCodes.RateMissing);
    }

    [Fact]
    public async Task A_missing_rate_leaves_no_household_converted_total_and_says_which()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SharedUsdHouseholdAsync(app);
        await SeedAsync(app, (new LocalDate(2025, 10, 1), 0.8m), (new LocalDate(2025, 10, 14), 0.5m));

        var list = await app.GetAsync<ListResponse>(app.AdultA, HouseholdWindow);

        Assert.Null(list.HouseholdCashCostConverted);
        Assert.Null(list.HouseholdAmortizedCostConverted);
        Assert.Single(list.Findings, f => f.Code == FxCodes.RateMissing && f.Reference == "EUR:2025-09-01");
    }

    [Fact]
    public async Task Each_charge_converts_at_its_own_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UsdMonthlyAsync(app);
        await SeedAsync(app, (new LocalDate(2025, 9, 1), 0.9m), (new LocalDate(2025, 10, 1), 0.8m));

        var detail = await app.GetAsync<Detail>(app.AdultA, $"/api/v1/subscriptions/{id.Value}?from=2025-09-01&to=2025-10-15");

        Assert.Equal(new Money("20", "USD"), Assert.Single(detail.CashCost));
        AssertMoney(new Money("17", "EUR"), detail.CashCostConverted); // 10 × 0.9 + 10 × 0.8
        Assert.False(detail.ConvertedIsEstimate);
    }

    [Fact]
    public async Task A_missing_rate_leaves_no_converted_total_and_says_which()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UsdMonthlyAsync(app);
        await SeedAsync(app, (new LocalDate(2025, 10, 1), 0.8m));

        var detail = await app.GetAsync<Detail>(app.AdultA, $"/api/v1/subscriptions/{id.Value}?from=2025-09-01&to=2025-10-15");

        Assert.Null(detail.CashCostConverted);
        Assert.Contains(detail.Findings, f => f.Code == FxCodes.RateMissing && f.Reference == "EUR:2025-09-01");
    }

    [Fact]
    public async Task A_projected_foreign_charge_is_converted_at_the_latest_rate_as_an_estimate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await UsdMonthlyAsync(app);
        await SeedAsync(app, (new LocalDate(2025, 10, 14), 0.8m));

        var detail = await app.GetAsync<Detail>(app.AdultA, $"/api/v1/subscriptions/{id.Value}?from=2025-11-01&to=2025-12-01");

        AssertMoney(new Money("8", "EUR"), detail.CashCostConverted);
        Assert.True(detail.ConvertedIsEstimate);
    }

    [Fact]
    public async Task Everything_in_the_reference_currency_converts_without_any_rate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 9.99m);

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me&from=2025-10-01&to=2025-11-01");

        AssertMoney(new Money("9.99", "EUR"), list.HouseholdCashCostConverted);
        AssertMoney(new Money("9.99", "EUR"), Assert.Single(list.Items).CashCostConverted);
        Assert.DoesNotContain(list.Findings, f => f.Code == FxCodes.RateMissing);
    }

    [Fact]
    public async Task The_household_total_mixes_currencies()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 5m);
        await UsdMonthlyAsync(app);
        await SeedAsync(app, (new LocalDate(2025, 10, 1), 0.8m));

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me&from=2025-10-01&to=2025-10-15");

        AssertMoney(new Money("13", "EUR"), list.HouseholdCashCostConverted); // 5 + 10 × 0.8
    }
}
