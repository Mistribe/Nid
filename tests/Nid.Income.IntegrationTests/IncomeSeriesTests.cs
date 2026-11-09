using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Income.IntegrationTests;

/// <summary>The explorer's income series: a source's months and the household's, with the item rights of a source.</summary>
public class IncomeSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement[] Items(JsonElement catalogue) => catalogue.GetProperty("items").EnumerateArray().ToArray();

    private static (string, decimal)[] Points(JsonElement series) => series.GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString()!, decimal.Parse(p.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();

    [Fact]
    public async Task Catalogue_lists_a_shared_source_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewIncomeSourceAsync("Salaire Alice", Visibility.Shared, (app.AdultA, 10_000));

        var found = Items(await GetAsync(app, app.AdultB, "/api/v1/series?q=alice")).Single(i => i.GetProperty("key").GetString() == $"income:{id.Value}");

        Assert.Equal((id.Value.ToString(), "currency:EUR", "income"),
            (found.GetProperty("item").GetString(), found.GetProperty("unit").GetString(), found.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Another_adults_private_source_is_absent_and_its_points_are_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewIncomeSourceAsync("Salaire Alice", Visibility.Private, (app.AdultA, 10_000));

        Assert.DoesNotContain(Items(await GetAsync(app, app.AdultB, "/api/v1/series?scope=me")), i => i.GetProperty("key").GetString() == $"income:{id.Value}");
        var failure = await GetAsync(app, app.AdultB, $"/api/v1/series/points?s=income:{id.Value}", HttpStatusCode.NotFound);
        Assert.Equal(("core.series_not_found", $"income:{id.Value}"), (failure.GetProperty("code").GetString(), failure.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task The_readers_own_private_source_answers_under_the_household_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewIncomeSourceAsync("Salaire Alice", Visibility.Private, (app.AdultA, 10_000));

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=income:{id.Value}&scope=household");
    }

    [Fact]
    public async Task A_source_series_is_its_overview_months_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewIncomeSourceAsync("Salaire Alice", Visibility.Shared, (app.AdultA, 10_000));

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=income:{id.Value}")).GetProperty("series")[0];

        var months = (await GetAsync(app, app.AdultA, $"/api/v1/income/sources/{id.Value}/overview?period=all")).GetProperty("months").EnumerateArray()
            .Select(m => ($"{m.GetProperty("year").GetInt32():0000}-{m.GetProperty("month").GetInt32():00}-01",
                decimal.Parse(m.GetProperty("total").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        Assert.Equal(months, Points(series));
        Assert.Equal(("2025-01-01", 1000m), months[0]);
    }

    [Fact]
    public async Task The_household_series_is_the_income_overview_months_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewIncomeSourceAsync("Salaire Alice", Visibility.Shared, (app.AdultA, 10_000));

        var series = (await GetAsync(app, app.AdultA, "/api/v1/series/points?s=income:household")).GetProperty("series")[0];

        var months = (await GetAsync(app, app.AdultA, "/api/v1/income/overview?period=3y")).GetProperty("months").EnumerateArray()
            .Where(m => m.GetProperty("total").ValueKind != JsonValueKind.Null)
            .Select(m => ($"{m.GetProperty("year").GetInt32():0000}-{m.GetProperty("month").GetInt32():00}-01",
                decimal.Parse(m.GetProperty("total").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        Assert.NotEmpty(months);
        Assert.Equal(months, Points(series));
        Assert.Equal(("currency:EUR", JsonValueKind.Null), (series.GetProperty("unit").GetString(), series.GetProperty("item").ValueKind));
    }
}
