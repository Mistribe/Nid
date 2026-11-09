using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Childcare.IntegrationTests;

/// <summary>The explorer's childcare series: an arrangement's validated months, dated the first, with the item rights of an arrangement.</summary>
public class ChildcareSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static string IdOf(string arrangement) => arrangement[(arrangement.LastIndexOf('/') + 1)..];

    private static JsonElement[] Items(JsonElement catalogue) => catalogue.GetProperty("items").EnumerateArray().ToArray();

    private static bool Has(JsonElement catalogue, string id) => Items(catalogue).Any(i => i.GetProperty("key").GetString() == $"childcare:{id}:monthly");

    [Fact]
    public async Task Catalogue_lists_a_shared_arrangement_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = IdOf(await ChildcareApi.CreateAsync(app));

        var found = Items(await GetAsync(app, app.AdultB, "/api/v1/series?q=garde")).Single(i => i.GetProperty("key").GetString() == $"childcare:{id}:monthly");

        Assert.Equal((id, "currency:EUR", "childcare_monthly"),
            (found.GetProperty("item").GetString(), found.GetProperty("unit").GetString(), found.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Another_adults_private_arrangement_is_absent_and_its_points_are_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = IdOf(await ChildcareApi.CreateAsync(app, "private"));

        Assert.False(Has(await GetAsync(app, app.AdultB, "/api/v1/series?scope=me"), id));
        var failure = await GetAsync(app, app.AdultB, $"/api/v1/series/points?s=childcare:{id}:monthly", HttpStatusCode.NotFound);
        Assert.Equal(("core.series_not_found", $"childcare:{id}:monthly"), (failure.GetProperty("code").GetString(), failure.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task The_readers_own_private_arrangement_answers_under_the_household_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = IdOf(await ChildcareApi.CreateAsync(app, "private"));

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=childcare:{id}:monthly&scope=household");
    }

    [Fact]
    public async Task The_series_is_the_validated_months_cost()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var arrangement = await ChildcareApi.CreateAsync(app);
        await ChildcareApi.ValidateAsync(app, arrangement, "2025-09");
        await ChildcareApi.GetAsync(app, $"{arrangement}/statements/2025-10"); // read, not validated: no cost

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=childcare:{IdOf(arrangement)}:monthly")).GetProperty("series")[0];

        var points = series.GetProperty("points").EnumerateArray()
            .Select(p => (p.GetProperty("date").GetString()!, decimal.Parse(p.GetProperty("value").GetString()!, CultureInfo.InvariantCulture))).ToArray();
        var rows = (await GetAsync(app, app.AdultA, $"{arrangement}/statements")).GetProperty("months").EnumerateArray()
            .Where(m => m.GetProperty("cost").ValueKind != JsonValueKind.Null)
            .Select(m => ($"{m.GetProperty("month").GetString()}-01", decimal.Parse(m.GetProperty("cost").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture))).ToArray();
        Assert.Single(points);
        Assert.Equal("2025-09-01", points[0].Item1);
        Assert.Equal(rows, points);
        Assert.Equal("currency:EUR", series.GetProperty("unit").GetString());
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("nope")]
    public async Task A_goal_id_in_a_childcare_key_is_404(string which)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = which == "goal" ? (await app.NewGoalAsync("Vacances", app.AdultA)).Value.ToString() : "nope";

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=childcare:{id}:monthly", HttpStatusCode.NotFound);
    }
}
