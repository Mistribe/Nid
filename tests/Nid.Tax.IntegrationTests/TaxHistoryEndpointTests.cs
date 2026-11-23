using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Tax.IntegrationTests;

/// <summary>Issue #307: GET /api/v1/tax/history, a notice's year read from the notice, the others estimated (today: 2026-11-05).</summary>
public class TaxHistoryEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class FixedClock(Instant now) : IClock
    {
        public Instant GetCurrentInstant() => now;
    }

    private static Task<NidApp> StartAsync(PostgresFixture fixture)
        => NidApp.StartAsync(fixture, services => services.AddSingleton<IClock>(new FixedClock(Instant.FromUtc(2026, 11, 5, 12, 0))));

    private static async Task<JsonElement> GetAsync(NidApp app, string url)
        => JsonDocument.Parse(await app.GetRawAsync(app.AdultA, url)).RootElement;

    private static Task<JsonElement> HistoryAsync(NidApp app, string scope = "household") => GetAsync(app, $"/api/v1/tax/history?scope={scope}");

    private static async Task NoticeAsync(NidApp app, int year, string netTax, bool joint = true)
    {
        var declarants = joint
            ? new object[] { new { personId = app.AdultA.Value, rank = 1 }, new { personId = app.AdultB.Value, rank = 2 } }
            : [new { personId = app.AdultA.Value, rank = 1 }];
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, "/api/v1/tax/assessments",
            new { year, declarants, netTax = new { amount = netTax, currency = "EUR" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static JsonElement Row(JsonElement history, int year) => history.GetProperty("years").EnumerateArray().Single(y => y.GetProperty("year").GetInt32() == year);

    /// <summary>The year's income tax plus flat tax, as GET /tax?year= gives them.</summary>
    private static async Task<decimal> EstimatedAsync(NidApp app, int year)
    {
        var result = (await GetAsync(app, $"/api/v1/tax?year={year}&scope=household")).GetProperty("result");
        return Dec(result.GetProperty("incomeTax").GetString()!) + Dec(result.GetProperty("capitalFlatTax").GetString()!);
    }

    private static decimal Dec(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public async Task History_uses_the_notice_when_the_year_has_one()
    {
        await using var app = await StartAsync(fixture);
        await NoticeAsync(app, 2025, "1200.00");

        var row = Row(await HistoryAsync(app), 2025);

        Assert.Equal("assessment", row.GetProperty("source").GetString());
        Assert.Equal("1200.00", row.GetProperty("tax").GetString());
    }

    [Fact]
    public async Task History_estimates_years_without_notice()
    {
        await using var app = await StartAsync(fixture);
        foreach (var year in new[] { 2025, 2026 })
            Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, $"/api/v1/tax/profile/{year}?scope=me", new { dependents = 1, pinel = Array.Empty<object>() })).StatusCode);

        var history = await HistoryAsync(app);

        Assert.Equal([2025, 2026], history.GetProperty("years").EnumerateArray().Select(y => y.GetProperty("year").GetInt32()));
        foreach (var year in new[] { 2025, 2026 })
        {
            Assert.Equal("estimate", Row(history, year).GetProperty("source").GetString());
            Assert.Equal(await EstimatedAsync(app, year), Dec(Row(history, year).GetProperty("tax").GetString()!));
        }
    }

    [Fact]
    public async Task History_skips_a_year_with_nothing()
    {
        await using var app = await StartAsync(fixture);
        await NoticeAsync(app, 2023, "900.00");
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2025?scope=me", new { dependents = 1, pinel = Array.Empty<object>() })).StatusCode);

        var history = await HistoryAsync(app);

        Assert.Equal([2023, 2025, 2026], history.GetProperty("years").EnumerateArray().Select(y => y.GetProperty("year").GetInt32()));
    }

    [Fact]
    public async Task Estimate_carries_previous_and_change()
    {
        await using var app = await StartAsync(fixture);
        await NoticeAsync(app, 2025, "1200.00");

        var estimate = await GetAsync(app, "/api/v1/tax?year=2026&scope=household");

        var previous = estimate.GetProperty("previous");
        Assert.Equal(2025, previous.GetProperty("year").GetInt32());
        Assert.Equal("assessment", previous.GetProperty("source").GetString());
        Assert.Equal("1200.00", previous.GetProperty("tax").GetString());
        var change = await EstimatedAsync(app, 2026) - 1200m;
        Assert.Equal(change, Dec(previous.GetProperty("change").GetString()!));
        Assert.Equal(
            (await HistoryAsync(app)).GetProperty("years").GetRawText(),
            estimate.GetProperty("history").GetRawText());
    }

    [Fact]
    public async Task Estimate_without_earlier_year_has_no_previous()
    {
        await using var app = await StartAsync(fixture);

        var estimate = await GetAsync(app, "/api/v1/tax?year=2025&scope=household");

        Assert.Equal(JsonValueKind.Null, estimate.GetProperty("previous").ValueKind);
    }

    [Fact]
    public async Task History_skips_an_earlier_year_without_data_but_keeps_the_year_shown()
    {
        await using var app = await StartAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/tax/profile/2026?scope=me", new { dependents = 1, pinel = Array.Empty<object>() })).StatusCode);

        var estimate = await GetAsync(app, "/api/v1/tax?year=2026&scope=household");

        Assert.Equal([2026], estimate.GetProperty("history").EnumerateArray().Select(y => y.GetProperty("year").GetInt32()));
        Assert.Equal(JsonValueKind.Null, estimate.GetProperty("previous").ValueKind);

        await using var empty = await StartAsync(fixture);
        Assert.Equal([2026], (await HistoryAsync(empty)).GetProperty("years").EnumerateArray().Select(y => y.GetProperty("year").GetInt32()));
    }

    [Fact]
    public async Task History_respects_the_scope()
    {
        await using var app = await StartAsync(fixture);
        await NoticeAsync(app, 2025, "1200.00", joint: false);

        Assert.Equal("assessment", Row(await HistoryAsync(app, "me"), 2025).GetProperty("source").GetString());
        // In Foyer a notice only one adult declares is not the household's: its year has no data left.
        Assert.DoesNotContain((await HistoryAsync(app, "household")).GetProperty("years").EnumerateArray(), y => y.GetProperty("year").GetInt32() == 2025);
    }

    [Fact]
    public async Task History_leaves_out_a_notice_the_reader_does_not_declare()
    {
        await using var app = await StartAsync(fixture);
        var response = await app.RawAsync(app.AdultB, HttpMethod.Post, "/api/v1/tax/assessments", new
        {
            year = 2025, declarants = new object[] { new { personId = app.AdultB.Value, rank = 1 } }, netTax = new { amount = "800.00", currency = "EUR" },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        foreach (var scope in new[] { "me", "household" })
            Assert.DoesNotContain((await HistoryAsync(app, scope)).GetProperty("years").EnumerateArray(), y => y.GetProperty("source").GetString() == "assessment");

        // Positive control: a notice AdultA declares does appear.
        await NoticeAsync(app, 2024, "700.00", joint: false);
        Assert.Equal("assessment", Row(await HistoryAsync(app, "me"), 2024).GetProperty("source").GetString());
    }
}
