using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests;

/// <summary>The explorer's subscription series: the overview's yearly spend, dated 1 January (the app clock is fixed at 2025-10-15).</summary>
public class SubscriptionSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static (string, decimal)[] Points(JsonElement view) => view.GetProperty("series")[0].GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString()!, decimal.Parse(p.GetProperty("value").GetString()!, CultureInfo.InvariantCulture))).ToArray();

    private static async Task<(string, decimal)[]> OverviewAsync(NidApp app, PersonId reader, string scope)
        => (await GetAsync(app, reader, $"/api/v1/subscriptions/overview?scope={scope}")).GetProperty("years").EnumerateArray()
            .Where(y => y.GetProperty("total").ValueKind != JsonValueKind.Null)
            .Select(y => ($"{y.GetProperty("year").GetInt32()}-01-01", decimal.Parse(y.GetProperty("total").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture))).ToArray();

    [Fact]
    public async Task Catalogue_lists_the_yearly_spend_for_a_member()
    {
        await using var app = await NidApp.StartAsync(fixture);

        foreach (var url in new[] { "/api/v1/series?q=subscriptions", "/api/v1/series?q=zzz&kinds=subscriptions_yearly" })
        {
            var items = (await GetAsync(app, app.AdultB, url)).GetProperty("items").EnumerateArray().ToArray();
            var item = Assert.Single(items);
            Assert.Equal(("subscriptions:yearly", "currency:EUR", JsonValueKind.Null),
                (item.GetProperty("key").GetString(), item.GetProperty("unit").GetString(), item.GetProperty("item").ValueKind));
        }
    }

    [Fact]
    public async Task The_yearly_series_is_the_overview_years_total()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await app.NewMonthlyAsync(app.AdultA, 10m);

        var points = Points(await GetAsync(app, app.AdultA, "/api/v1/series/points?s=subscriptions:yearly"));

        Assert.Equal([("2025-01-01", 120m)], points); // the current year, paid and still to come
        Assert.Equal(await OverviewAsync(app, app.AdultA, "household"), points);
    }

    [Fact]
    public async Task The_yearly_series_follows_the_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await app.NewSubscriptionAsync("Private", app.AdultA, Visibility.Private);
        (await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/subscriptions/{id.Value}/phases", new
        {
            startsOn = "2025-01-01", price = new { amount = "10", currency = "EUR" }, every = new { count = 1, unit = "month" }, payer = new { type = "household" },
        })).EnsureSuccessStatusCode();

        // The household selector counts shared items only (the brief expected 120 for A there; the overview gives none, and the series equals the overview).
        foreach (var (reader, scope, expected) in new[] { (app.AdultB, "household", 0m), (app.AdultA, "household", 0m), (app.AdultA, "me", 120m), (app.AdultB, "me", 0m) })
        {
            var points = Points(await GetAsync(app, reader, $"/api/v1/series/points?s=subscriptions:yearly&scope={scope}"));
            Assert.Equal(await OverviewAsync(app, reader, scope), points);
            Assert.Equal(expected == 0m ? [] : [("2025-01-01", expected)], points);
        }
    }
}
