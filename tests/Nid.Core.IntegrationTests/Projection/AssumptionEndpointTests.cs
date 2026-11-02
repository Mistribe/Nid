using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Projection;

/// <summary>Net worth projection spec §2.1: the household's assumptions, read and replaced by any adult.</summary>
public class AssumptionEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/core/projection-assumptions";

    private static Dictionary<string, string> Rates(string equity) => new()
    {
        ["cash"] = "2", ["money_market"] = "2.5", ["bond"] = "3", ["equity"] = equity, ["mixed"] = "4",
        ["capital_guaranteed"] = "2.5", ["real_estate"] = "2", ["other"] = "0",
    };

    private static object Body(string equity) => new
    {
        classRates = Rates(equity), inflation = "2", monthlySavings = (string?)null, savingsClass = "cash", freedPaymentsToSavings = true,
    };

    [Fact]
    public async Task Get_returns_defaults_as_percent_strings()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);

        Assert.Equal("6", view.GetProperty("classRates").GetProperty("equity").GetString());
        Assert.Equal("2", view.GetProperty("inflation").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("monthlySavings").ValueKind);
        Assert.Equal("cash", view.GetProperty("savingsClass").GetString());
        Assert.True(view.GetProperty("freedPaymentsToSavings").GetBoolean());
        Assert.Equal("EUR", view.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Any_adult_replaces_them()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var put = await app.RawAsync(app.AdultB, HttpMethod.Put, Url, new
        {
            classRates = Rates("7"), inflation = "2.5", monthlySavings = "500", savingsClass = "equity", freedPaymentsToSavings = false,
        });

        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var view = await app.GetAsync<JsonElement>(app.AdultA, Url);
        Assert.Equal("7", view.GetProperty("classRates").GetProperty("equity").GetString());
        Assert.Equal("2.5", view.GetProperty("inflation").GetString());
        Assert.Equal("500.00", view.GetProperty("monthlySavings").GetString());
        Assert.Equal("equity", view.GetProperty("savingsClass").GetString());
        Assert.False(view.GetProperty("freedPaymentsToSavings").GetBoolean());
        Assert.Equal("EUR", view.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Invalid_rate_names_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Url, Body("51"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("projection.invalid_field", problem.GetProperty("code").GetString());
        Assert.Equal("classRates.equity", problem.GetProperty("errors")[0].GetProperty("path").GetString());
        Assert.Equal("6", (await app.GetAsync<JsonElement>(app.AdultA, Url)).GetProperty("classRates").GetProperty("equity").GetString());
    }

    [Fact]
    public async Task Scenario_defaults_follow_the_household_rates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        (await app.RawAsync(app.AdultA, HttpMethod.Put, Url, Body("7"))).EnsureSuccessStatusCode();

        var defaults = await app.GetAsync<JsonElement>(app.AdultA, "/api/v1/scenarios/defaults");

        Assert.Equal("7", defaults.GetProperty("classRates").GetProperty("equity").GetString());
    }
}
