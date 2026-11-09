using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using static Nid.TestSupport.LoanApi;
using static Nid.TestSupport.PropertyApi;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>The explorer's property series: estimates, cost price and net value, with the property's item rights.</summary>
public class PropertySeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static string[] Keys(JsonElement catalogue) => catalogue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToArray();

    private static (string, decimal)[] Points(JsonElement series) => series.GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString()!, decimal.Parse(p.GetProperty("value").GetString()!, CultureInfo.InvariantCulture))).ToArray();

    /// <summary>Bought 100 000 on 2020-01-15, estimated 120 000 on 2024-06-30.</summary>
    private static async Task<Guid> NewAsync(NidApp app, string visibility, PersonId owner)
    {
        var body = Home(app, "Maison Alice", visibility, owner);
        body["purchaseDate"] = "2020-01-15";
        body["purchasePrice"] = "100000";
        body["notaryFees"] = "0";
        var id = await NewPropertyAsync(app, body, owner);
        await CreatedAsync(await app.RawAsync(owner, HttpMethod.Post, $"{Properties}/{id}/valuations", new { asOf = "2024-06-30", amount = "120000" }));
        return id;
    }

    [Fact]
    public async Task Catalogue_lists_a_shared_property_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, "shared", app.AdultA);

        var found = await GetAsync(app, app.AdultB, "/api/v1/series?q=alice");

        Assert.Equal([$"property:{id}:cost", $"property:{id}:net", $"property:{id}:value"], Keys(found).Order().ToArray());
        Assert.All(found.GetProperty("items").EnumerateArray(), i => Assert.Equal((id.ToString(), "currency:EUR"), (i.GetProperty("item").GetString(), i.GetProperty("unit").GetString())));
    }

    [Fact]
    public async Task Another_adults_private_property_is_absent_and_its_points_are_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, "private", app.AdultA);

        Assert.DoesNotContain(Keys(await GetAsync(app, app.AdultB, "/api/v1/series?scope=me")), k => k.Contains(id.ToString()));
        foreach (var key in new[] { $"property:{id}:value", $"property:{id}:cost", $"property:{id}:net" })
        {
            var failure = await GetAsync(app, app.AdultB, $"/api/v1/series/points?s={key}", HttpStatusCode.NotFound);
            Assert.Equal(("core.series_not_found", key), (failure.GetProperty("code").GetString(), failure.GetProperty("key").GetString()));
        }

        var denied = await AuditApi.EventsAsync(app, SecurityEventNames.AccessDenied);
        Assert.NotEmpty(denied); // the journal folds repeats
        Assert.All(denied, d =>
        {
            Assert.Equal(app.AdultB.Value, d.PersonId!.Value);
            Assert.Equal([id], d.ItemIds);
        });
    }

    [Fact]
    public async Task The_readers_own_private_property_answers_under_the_household_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, "private", app.AdultA);

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=property:{id}:value,property:{id}:cost,property:{id}:net&scope=household");
    }

    [Fact]
    public async Task A_loan_id_in_a_property_key_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await app.NewLoanAsync("Prêt Alice", Visibility.Shared, (app.AdultA, 10_000));

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=property:{loan.Value}:net", HttpStatusCode.NotFound);
        await GetAsync(app, app.AdultA, "/api/v1/series/points?s=property:nope:value", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Value_and_cost_are_the_valuation_overviews()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, "shared", app.AdultA);

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=property:{id}:value,property:{id}:cost")).GetProperty("series");

        Assert.Equal([("2024-06-30", 120_000m)], Points(series[0]));
        Assert.Equal([("2020-01-15", 100_000m)], Points(series[1]));
        var row = (await GetAsync(app, app.AdultA, $"{Properties}/overview/valuation")).GetProperty("properties")[0];
        Assert.Equal(Points(series[0]), row.GetProperty("estimates").EnumerateArray().Select(e => (S(e, "on")!, decimal.Parse(S(e, "value")!, CultureInfo.InvariantCulture))));
        Assert.Equal(Points(series[1]), row.GetProperty("costSteps").EnumerateArray().Select(e => (S(e, "on")!, decimal.Parse(S(e, "costPrice")!, CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task Net_value_is_the_overviews_curve_for_a_lone_property()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await NewAsync(app, "shared", app.AdultA);
        var loan = Golden(app);
        loan["propertyId"] = id;
        await LoanApi.NewAsync(app, loan);

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=property:{id}:net")).GetProperty("series")[0];

        var curve = (await GetAsync(app, app.AdultA, $"{Properties}/overview?span=all&scope=household")).GetProperty("points").EnumerateArray()
            .Where(p => p.GetProperty("netValue").ValueKind != JsonValueKind.Null)
            .Select(p => (S(p, "on")!, decimal.Parse(S(p.GetProperty("netValue"), "amount")!, CultureInfo.InvariantCulture))).ToArray();
        Assert.NotEmpty(curve);
        Assert.Equal(curve, Points(series));
    }
}
