using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Scenarios;

/// <summary>Property purchase scenario spec §6, through the real API. Today is 2025-10-15.</summary>
public class ScenarioEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/scenarios";

    private static object Document(string price = "215000", string rate = "3.5", int horizon = 10) => new
    {
        version = 1, use = "primary", purchaseOn = "2025-12-01", price, notaryFees = "15000", currentRent = "800", monthlyCharges = "100",
        funding = Array.Empty<object>(),
        loans = new[]
        {
            new
            {
                name = "Banque", amount = "200000", rate, months = 240, insurance = new { kind = "rate_on_initial", value = "0.3" },
                fileFees = "1000", guaranteeFees = "0",
            },
        },
        assumptions = new
        {
            horizonYears = horizon, classRates = new Dictionary<string, string> { ["cash"] = "2", ["equity"] = "6" }, monthlySavings = "500",
            savingsClass = "cash",
        },
    };

    private static object Body(NidApp app, object document, string name = "Appartement") => new
    {
        name, visibility = "shared", scope = "household", owners = new[] { new { personId = app.AdultA.Value, shareBp = 10_000 } }, document,
    };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task A_scenario_is_created_read_replaced_and_deleted()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var created = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, Document()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonOf(created)).GetProperty("id").GetGuid();

        var read = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}"));
        Assert.True(read.GetProperty("result").GetProperty("capacity").TryGetProperty("newPayment", out _));
        Assert.Equal("215000.00", read.GetProperty("document").GetProperty("price").GetString());

        var replaced = await app.RawAsync(app.AdultA, HttpMethod.Put, $"{Url}/{id}", Body(app, Document("230000")));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        read = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/{id}"));
        Assert.Equal("230000.00", read.GetProperty("document").GetProperty("price").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, $"{Url}/{id}")).StatusCode);
        Assert.DoesNotContain(id.ToString(), await app.GetRawAsync(app.AdultA, Url));
    }

    [Theory]
    [InlineData("price", "-1")]
    [InlineData("loans[0].rate", "abc")]
    [InlineData("assumptions.horizonYears", "31")]
    [InlineData("loans[0].insurance", "missing")]
    [InlineData("assumptions.classRates", "missing")]
    [InlineData("purchaseOn", "missing")]
    public async Task Each_refusal_names_its_field(string field, string value)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var json = JsonSerializer.Serialize(Document(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        switch (field)
        {
            case "price": doc["price"] = value; break;
            case "loans[0].rate": doc["loans"]![0]!["rate"] = value; break;
            case "assumptions.horizonYears": doc["assumptions"]!["horizonYears"] = int.Parse(value); break;
            case "loans[0].insurance": doc["loans"]![0]!["insurance"] = null; break;
            case "purchaseOn": ((System.Text.Json.Nodes.JsonObject)doc).Remove("purchaseOn"); break;
            default: doc["assumptions"]!["classRates"] = null; break;
        }

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, Url, Body(app, doc));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var paths = (await JsonOf(response)).GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains(field, paths);
    }

    [Fact]
    public async Task Simulate_answers_without_storing()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url}/simulate", new { scope = "household", document = Document() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonOf(response)).GetProperty("capacity").TryGetProperty("newPayment", out _));
        Assert.Empty((await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, Url))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Defaults_give_the_specs_rates_as_percents()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var defaults = await JsonOf(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url}/defaults"));

        Assert.Equal("6", defaults.GetProperty("classRates").GetProperty("equity").GetString());
    }
}
