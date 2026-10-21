using System.Globalization;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>The annual cost: amortized over the 12 months from the 1st of the current month (the app clock is fixed at 2025-10-15).</summary>
public class AnnualCostTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    public sealed record Money(string Amount, string Currency);
    public sealed record FindingResponse(string Code, string? Reference);
    public sealed record Summary(Guid Id, Money? AmortizedCostConverted, Money? AnnualCostConverted, List<FindingResponse> Findings);
    public sealed record ListResponse(List<Summary> Items, Money? HouseholdAnnualCostConverted, List<FindingResponse> Findings);

    private static decimal Value(Money? m) => decimal.Parse(Assert.IsType<Money>(m).Amount, CultureInfo.InvariantCulture);

    private static async Task<ItemId> PhaseAsync(NidApp app, string name, string currency, string amount, string unit)
    {
        var id = await app.NewSubscriptionAsync(name, app.AdultA, Visibility.Shared);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-01",
            price = new { amount, currency },
            every = new { count = 1, unit },
            payer = new { type = "household" },
        })).EnsureSuccessStatusCode();
        return id;
    }

    [Fact]
    public async Task A_yearly_subscription_costs_its_price_over_the_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PhaseAsync(app, "yearly", "EUR", "216.00", "year");

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me");

        Assert.Equal(216m, Value(Assert.Single(list.Items).AnnualCostConverted));
    }

    [Fact]
    public async Task A_monthly_subscription_costs_twelve_charges()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 17.20m);

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me");

        Assert.Equal(206.40m, Value(Assert.Single(list.Items).AnnualCostConverted));
    }

    [Fact]
    public async Task The_household_annual_cost_is_the_sum_of_its_rows()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PhaseAsync(app, "yearly", "EUR", "216.00", "year");
        await app.NewMonthlyAsync(app.AdultA, 17.20m);

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me");

        Assert.Equal(422.40m, Value(list.HouseholdAnnualCostConverted));
    }

    /// <summary>EUR until 2026-01-01, then USD: a September window converts without a rate, the year (projected USD charges at the 2025-10-15 rate) does not.</summary>
    [Fact]
    public async Task A_missing_rate_leaves_the_annual_cost_null()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("eur-then-usd", app.AdultA, Visibility.Shared);
        foreach (var (starts, ends, currency) in new[] { ("2025-01-01", "2026-01-01", "EUR"), ("2026-01-01", null, "USD") })
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
            {
                startsOn = starts, endsOn = ends,
                price = new { amount = "10", currency },
                every = new { count = 1, unit = "month" },
                payer = new { type = "household" },
            })).EnsureSuccessStatusCode();

        var list = await app.GetAsync<ListResponse>(app.AdultA, "/api/v1/subscriptions?scope=me&from=2025-09-01&to=2025-10-01");

        var row = Assert.Single(list.Items);
        Assert.NotNull(row.AmortizedCostConverted);
        Assert.Null(row.AnnualCostConverted);
        Assert.Null(list.HouseholdAnnualCostConverted);
        Assert.Contains(row.Findings, f => f.Code == FxCodes.RateMissing && f.Reference == "EUR:2025-10-15");
    }
}
