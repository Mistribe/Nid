using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

public class ListFilterEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string January = "from=2025-01-01&to=2025-02-01";

    public sealed record MoneyResponse(string Amount, string Currency);
    public sealed record SummaryResponse(Guid Id, string DisplayName);
    public sealed record ListResponse(IReadOnlyList<SummaryResponse> Items, IReadOnlyList<MoneyResponse> HouseholdCashCost);

    private static async Task<(string First, string Second)> TwoMonthlyAsync(NidApp app)
    {
        foreach (var (name, amount) in new[] { ("Alpha", "10.00"), ("Beta", "20.00") })
        {
            var id = await app.NewSubscriptionAsync(name, app.AdultA, Visibility.Shared);
            (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
            {
                startsOn = "2025-01-01",
                price = new { amount, currency = "EUR" },
                every = new { count = 1, unit = "month" },
                payer = new { type = "household" },
            })).EnsureSuccessStatusCode();
        }
        return ("Alpha", "Beta");
    }

    [Fact]
    public async Task Filters_narrow_both_rows_and_household_totals()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (first, _) = await TwoMonthlyAsync(app);

        var list = await app.GetAsync<ListResponse>(app.AdultA, $"/api/v1/subscriptions?{January}&q={first}");

        Assert.Equal(first, Assert.Single(list.Items).DisplayName);
        Assert.Equal("10.00", list.HouseholdCashCost.Single(m => m.Currency == "EUR").Amount);
    }

    [Fact]
    public async Task Unknown_status_is_all()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await TwoMonthlyAsync(app);

        var list = await app.GetAsync<ListResponse>(app.AdultA, $"/api/v1/subscriptions?{January}&status=nope");

        Assert.Equal(2, list.Items.Count);
    }
}
