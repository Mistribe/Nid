using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;
using static Nid.Subscriptions.IntegrationTests.RealEstate.LoanApi;
using static Nid.Subscriptions.IntegrationTests.RealEstate.PropertyApi;

namespace Nid.Subscriptions.IntegrationTests.Dashboard;

/// <summary>Issue #104: the dashboard's allocation by asset class and liquidity, and the household's target.</summary>
public class AllocationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Target = "/api/v1/dashboard/allocation-target";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> AllocationAsync(NidApp app, PersonId reader)
        => JsonDocument.Parse(await app.GetRawAsync(reader, "/api/v1/dashboard?scope=household")).RootElement.GetProperty("allocation");

    private static string? Amount(JsonElement money) => money.ValueKind == JsonValueKind.Null ? null : money.GetProperty("amount").GetString();

    private static Dictionary<string, JsonElement> Lines(JsonElement allocation, string name)
        => allocation.GetProperty(name).EnumerateArray().ToDictionary(l => l.GetProperty("key").GetString()!);

    /// <summary>A savings account worth 25 000 whose snapshot details 15 000 of euro fund and 9 000 of ETF; a home estimated 300 000; a loan with 2 000 owed.</summary>
    private static async Task SeedAsync(NidApp app)
    {
        var id = await app.NewEnvelopeAsync("Assurance", app.AdultA, Visibility.Shared);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("25000.00") });
        var euro = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings", new { name = "Fonds euros", kind = "euro_fund" }))).GetProperty("id").GetGuid();
        var etf = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holdings", new { name = "ETF Monde", kind = "etf" }))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{url}/holding-snapshots", new
        {
            asOf = "2025-09-30", complete = true,
            lines = new object[] { new { holdingId = euro, value = Eur("15000.00") }, new { holdingId = etf, value = Eur("9000.00") } },
        })).StatusCode);

        var home = await NewPropertyAsync(app, Home(app, "Maison"));
        await CreatedAsync(await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Properties}/{home}/valuations", new { asOf = "2025-09-30", amount = "300000" }));
        await NewAsync(app, Short(app, "Prêt travaux"));
    }

    [Fact]
    public async Task The_assets_split_by_class_and_liquidity_the_debts_apart_summing_to_the_net_worth()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        var view = JsonDocument.Parse(await app.GetRawAsync(app.AdultA, "/api/v1/dashboard?scope=household")).RootElement;
        var allocation = view.GetProperty("allocation");

        Assert.Equal("323000.00", view.GetProperty("netWorth").GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("325000.00", Amount(allocation.GetProperty("assets")));
        Assert.Equal("-2000.00", Amount(allocation.GetProperty("debts")));
        Assert.Equal("9375.00", Amount(allocation.GetProperty("approximate"))); // the ETF's class is guessed from its kind

        var classes = Lines(allocation, "classes");
        Assert.Equal(["equity", "capital_guaranteed", "real_estate"], classes.Keys);
        Assert.Equal("9375.00", Amount(classes["equity"].GetProperty("amount")));
        Assert.Equal("15625.00", Amount(classes["capital_guaranteed"].GetProperty("amount")));
        Assert.Equal("300000.00", Amount(classes["real_estate"].GetProperty("amount")));
        Assert.Equal(92.3m, classes["real_estate"].GetProperty("pct").GetDecimal());
        Assert.Equal(JsonValueKind.Null, classes["equity"].GetProperty("targetPct").ValueKind);

        var liquidity = Lines(allocation, "liquidity");
        Assert.Equal(["available", "locked"], liquidity.Keys); // a savings account now, a property locked
        Assert.Equal("25000.00", Amount(liquidity["available"].GetProperty("amount")));
    }

    [Fact]
    public async Task A_target_set_by_either_adult_adds_its_gaps_and_an_empty_one_removes_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        var saved = await app.RawAsync(app.AdultB, HttpMethod.Put, Target, new { target = new Dictionary<string, decimal> { ["equity"] = 60m, ["bond"] = 40m, ["cash"] = 0m } });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (await JsonAsync(saved)).GetProperty("target").EnumerateObject().Count()); // a zero share is no target

        var classes = Lines(await AllocationAsync(app, app.AdultA), "classes");
        Assert.Equal(["bond", "equity", "capital_guaranteed", "real_estate"], classes.Keys); // a class aimed at shows even at 0
        Assert.Equal("0.00", Amount(classes["bond"].GetProperty("amount")));
        Assert.Equal(-40.0m, classes["bond"].GetProperty("gapPct").GetDecimal());
        Assert.Equal(60m, classes["equity"].GetProperty("targetPct").GetDecimal());
        Assert.Equal(-57.1m, classes["equity"].GetProperty("gapPct").GetDecimal());
        Assert.Equal("-185625.00", Amount(classes["equity"].GetProperty("gap"))); // 9 375 − 60 % of 325 000

        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, Target, new { target = new Dictionary<string, decimal>() })).StatusCode);
        Assert.Empty((await AllocationAsync(app, app.AdultA)).GetProperty("target").EnumerateObject());
    }

    [Theory]
    [InlineData("equity", 60, "bond", 30)] // 90 %
    [InlineData("gold", 50, "bond", 50)] // no such class
    [InlineData("equity", 50.05, "bond", 49.95)] // beyond 0.1 %
    public async Task A_target_not_adding_up_to_100_or_naming_an_unknown_class_is_refused(string a, decimal pa, string b, decimal pb)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Put, Target, new { target = new Dictionary<string, decimal> { [a] = pa, [b] = pb } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("core.invalid_allocation_target", (await JsonAsync(response)).GetProperty("code").GetString());
    }
}
