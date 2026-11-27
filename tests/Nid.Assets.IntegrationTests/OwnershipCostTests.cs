using System.Net;
using System.Text.Json;
using Nid.Assets.Contracts;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Assets.IntegrationTests.AssetApi;

namespace Nid.Assets.IntegrationTests;

/// <summary>#650: a vehicle's cost of ownership. Today is <see cref="NidApp.GoldenCaseToday"/> (2025-10-15).</summary>
public class OwnershipCostTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static Task<HttpResponseMessage> CostOfAsync(NidApp app, ItemId id, string query = "")
        => app.RawAsync(app.AdultA, HttpMethod.Get, $"{Route}/{id.Value}/ownership-cost{query}");

    private static async Task AddCostAsync(NidApp app, ItemId id, string occurredOn, string amount, string category)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{id.Value}/costs",
            new { occurredOn, amount, category })).StatusCode);

    [Fact]
    public async Task A_vehicle_answers_its_cost_of_ownership()
    {
        await using var app = await NidApp.StartAsync(fixture);
        // Bought 2025-01-10 for 1 000: the default period (the 12 months to 2025-10-15) starts at the purchase.
        var id = await CreateAsync(app, Body(app, "Citadine", "vehicle"));
        await AddCostAsync(app, id, "2025-03-01", "120", "service");
        await AddCostAsync(app, id, "2025-06-01", "60", "fuel");
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Route}/{id.Value}/valuations",
            new { asOf = "2025-06-01", amount = "800" })).StatusCode);

        var response = await CostOfAsync(app, id);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var view = await JsonAsync(response);
        Assert.Equal(("2025-01-10", "2025-10-15", "380.00", "41.44", true), (view.GetProperty("from").GetString(), view.GetProperty("to").GetString(),
            view.GetProperty("total").GetProperty("amount").GetString(), view.GetProperty("monthly").GetProperty("amount").GetString(),
            view.GetProperty("incomplete").GetBoolean()));
        var parts = view.GetProperty("parts").EnumerateArray()
            .Select(p => (p.GetProperty("code").GetString(), p.GetProperty("amount").ValueKind == JsonValueKind.Null ? null : p.GetProperty("amount").GetProperty("amount").GetString()))
            .ToList();
        Assert.Equal([("running:service", "120.00"), ("running:fuel", "60.00"), ("subscription", null), ("depreciation", "200.00")], parts);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("perKm").ValueKind);
        Assert.Contains(2025, view.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
    }

    [Fact]
    public async Task A_year_is_its_calendar_year_up_to_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app, "Citadine", "vehicle", purchasedOn: "2024-01-10"));
        await AddCostAsync(app, id, "2024-03-01", "100", "fuel");
        await AddCostAsync(app, id, "2025-03-01", "50", "fuel");

        var view = await JsonAsync(await CostOfAsync(app, id, "?year=2024"));

        Assert.Equal(("2024-01-10", "2024-12-31"), (view.GetProperty("from").GetString(), view.GetProperty("to").GetString()));
        Assert.Equal("100.00", view.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("code").GetString() == "running:fuel")
            .GetProperty("amount").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Another_asset_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var art = await CreateAsync(app, Body(app));

        await AssertRefusedAsync(await CostOfAsync(app, art), HttpStatusCode.UnprocessableEntity, AssetsCodes.CostsVehicleOnly);
    }

    [Fact]
    public async Task A_year_out_of_bounds_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await CreateAsync(app, Body(app, "Citadine", "vehicle"));

        var response = await CostOfAsync(app, id, "?year=1899");

        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("year", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
