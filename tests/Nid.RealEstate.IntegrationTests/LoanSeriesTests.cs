using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.RealEstate.IntegrationTests;

/// <summary>The explorer's loan series: the remaining balance and the payment of each schedule row, with the loan's item rights.</summary>
public class LoanSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
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

    [Fact]
    public async Task Catalogue_lists_a_shared_loan_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewLoanAsync("Prêt Alice", Visibility.Shared, (app.AdultA, 10_000));

        var found = await GetAsync(app, app.AdultB, "/api/v1/series?q=alice");

        Assert.Equal([$"loan:{id.Value}:payment", $"loan:{id.Value}:remaining"], Keys(found).Order().ToArray());
        Assert.All(found.GetProperty("items").EnumerateArray(), i => Assert.Equal((id.Value.ToString(), "currency:EUR"), (i.GetProperty("item").GetString(), i.GetProperty("unit").GetString())));
    }

    [Fact]
    public async Task Another_adults_private_loan_is_absent_and_its_points_are_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewLoanAsync("Prêt Alice", Visibility.Private, (app.AdultA, 10_000));

        Assert.DoesNotContain(Keys(await GetAsync(app, app.AdultB, "/api/v1/series?scope=me")), k => k.Contains(id.Value.ToString()));
        foreach (var key in new[] { $"loan:{id.Value}:remaining", $"loan:{id.Value}:payment" })
        {
            var failure = await GetAsync(app, app.AdultB, $"/api/v1/series/points?s={key}", HttpStatusCode.NotFound);
            Assert.Equal(("core.series_not_found", key), (failure.GetProperty("code").GetString(), failure.GetProperty("key").GetString()));
        }
    }

    [Fact]
    public async Task The_readers_own_private_loan_answers_under_the_household_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewLoanAsync("Prêt Alice", Visibility.Private, (app.AdultA, 10_000));

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=loan:{id.Value}:remaining,loan:{id.Value}:payment&scope=household");
    }

    [Fact]
    public async Task Loan_series_are_its_schedule()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewLoanAsync("Prêt Alice", Visibility.Shared, (app.AdultA, 10_000));

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=loan:{id.Value}:remaining,loan:{id.Value}:payment")).GetProperty("series");

        var rows = (await GetAsync(app, app.AdultA, $"/api/v1/realestate/loans/{id.Value}/schedule")).GetProperty("rows").EnumerateArray().ToArray();
        (string, decimal) Row(JsonElement r, string field) => (r.GetProperty("dueDate").GetString()!, decimal.Parse(r.GetProperty(field).GetString()!, CultureInfo.InvariantCulture));
        Assert.Equal(rows.Select(r => Row(r, "balance")), Points(series[0]));
        Assert.Equal(rows.Select(r => Row(r, "payment")), Points(series[1]));
        Assert.Equal(24, Points(series[0]).Length);
        Assert.Equal(("2025-01-15", ("2026-12-15", 0m)), (Points(series[0])[0].Item1, Points(series[0])[^1]));
    }
}
