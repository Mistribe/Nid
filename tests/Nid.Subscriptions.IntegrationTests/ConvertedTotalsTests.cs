using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests;

[Collection("postgres")]
public class ConvertedTotalsTests(PostgresFixture fixture)
{
    public sealed record Money(string Amount, string Currency);
    public sealed record FindingResponse(string Code, string? Reference);
    public sealed record Detail(List<Money> CashCost, Money? CashCostConverted, Money? AmortizedCostConverted, bool ConvertedIsEstimate, List<FindingResponse> Findings);
    public sealed record Summary(Guid Id, Money? CashCostConverted);
    public sealed record ListResponse(List<Summary> Items, Money? HouseholdCashCostConverted, bool HouseholdConvertedIsEstimate, List<FindingResponse> Findings);

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
