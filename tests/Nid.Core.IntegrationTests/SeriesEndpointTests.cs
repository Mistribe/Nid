using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.RealEstate.Domain;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

/// <summary>The series explorer's routes: the catalogue and the points of a selection, as a non-admin adult.</summary>
public class SeriesEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "/api/v1/series";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Currency Code(string code) => Currency.TryCreate(code, out var c) ? c : throw new ArgumentException(code);

    private static async Task SeedAsync(NidApp app)
    {
        await app.SeedCpiAsync(new YearMonth(2025, 9), 2.4m);
        await using var scope = app.Services.CreateAsyncScope();
        var irl = scope.ServiceProvider.GetRequiredService<IIrlStore>();
        await irl.UpsertAsync(new IrlValue(new IrlQuarter(2025, 1), 140m, null, new LocalDate(2025, 10, 15), "manual"), Ct);
        await irl.UpsertAsync(new IrlValue(new IrlQuarter(2025, 2), 141.4m, null, new LocalDate(2025, 10, 15), "manual"), Ct);
        await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync([
            new FxRate(new LocalDate(2025, 10, 1), Currency.Eur, 0.85m, FxRateSource.Manual),
            new FxRate(new LocalDate(2025, 10, 2), Currency.Eur, 0.86m, FxRateSource.Manual),
            new FxRate(new LocalDate(2025, 10, 1), Code("GBP"), 0.75m, FxRateSource.Manual)], Ct);
    }

    private static async Task<JsonElement> GetAsync(NidApp app, string query)
    {
        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, Url + query);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static string[] Keys(JsonElement catalogue) => catalogue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToArray();

    private static (string?, string?, string?)[] Points(JsonElement series) => series.GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString(), p.GetProperty("value").GetString(), p.GetProperty("base100").GetString())).ToArray();

    private static async Task<(HttpStatusCode, string?, string?)> FailureAsync(NidApp app, string query)
    {
        var response = await app.RawAsync(app.AdultB, HttpMethod.Get, Url + "/points" + query);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        return (response.StatusCode, body.TryGetProperty("code", out var code) ? code.GetString() : null,
            body.TryGetProperty("key", out var key) ? key.GetString() : null);
    }

    [Fact]
    public async Task Catalogue_lists_the_reference_series_for_a_member()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        var catalogue = await GetAsync(app, "");

        Assert.Equal(
            ["cpi", "cpi:yoy", "income:household", "irl", "networth", "networth:projected", "projection:inflation", "projection:rate:bond", "projection:rate:capital_guaranteed", "projection:rate:cash",
                "projection:rate:equity", "projection:rate:mixed", "projection:rate:money_market", "projection:rate:other", "projection:rate:real_estate",
                "tax:yearly", "fx:EUR", "fx:GBP"],
            Keys(catalogue));
        Assert.Equal((18, 1), (catalogue.GetProperty("total").GetInt32(), catalogue.GetProperty("page").GetInt32()));
    }

    [Fact]
    public async Task Catalogue_search_and_paging_run_on_the_server()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        var found = await GetAsync(app, "?q=gb");
        Assert.Equal(["fx:GBP"], Keys(found));
        Assert.Equal(1, found.GetProperty("total").GetInt32());

        Assert.Equal(["irl"], Keys(await GetAsync(app, "?q=irl")));
        Assert.Equal(["cpi", "cpi:yoy"], Keys(await GetAsync(app, "?q=CPI")));
        Assert.Equal(["projection:inflation"], Keys(await GetAsync(app, "?q=inflation")));
        Assert.Equal(["fx:EUR"], Keys(await GetAsync(app, "?q=eur")));

        var second = await GetAsync(app, "?page=2");
        Assert.Empty(Keys(second));
        Assert.Equal((18, 2), (second.GetProperty("total").GetInt32(), second.GetProperty("page").GetInt32()));
    }

    [Fact]
    public async Task Catalogue_also_lists_the_kinds_asked_for()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        Assert.Equal(["irl"], Keys(await GetAsync(app, "?q=loyer&kinds=irl")));
        Assert.Equal((await GetAsync(app, "")).GetProperty("total").GetInt32(), (await GetAsync(app, "?kinds=cpi")).GetProperty("total").GetInt32());
        Assert.Empty(Keys(await GetAsync(app, "?q=zzz&kinds=nope")));
    }

    [Fact]
    public async Task Points_rebase_cpi_and_irl_on_their_common_first_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        var view = await GetAsync(app, "/points?s=cpi,irl&from=2024-01-01");

        Assert.Equal("2025-01-01", view.GetProperty("baseDate").GetString());
        var series = view.GetProperty("series");
        Assert.Equal(("2024-09-01", "100", "97.66"), Points(series[0])[0]);
        Assert.Equal([("2025-01-01", "140", "100"), ("2025-04-01", "141.4", "101")], Points(series[1]));
    }

    [Fact]
    public async Task Points_of_cpi_yoy_and_fx()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        var series = (await GetAsync(app, "/points?s=cpi:yoy,fx:EUR")).GetProperty("series");

        Assert.Equal("percent", series[0].GetProperty("unit").GetString());
        var yoy = series[0].GetProperty("points").EnumerateArray().Single();
        Assert.Equal(("2025-09-01", "2.4", JsonValueKind.Null), (yoy.GetProperty("date").GetString(), yoy.GetProperty("value").GetString(), yoy.GetProperty("base100").ValueKind));
        Assert.Equal(("rate:EUR", "fx", "EUR"),
            (series[1].GetProperty("unit").GetString(), series[1].GetProperty("kind").GetString(), series[1].GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Projection_rates_follow_the_assumptions_year_by_year()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);
        var put = await app.RawAsync(app.AdultA, HttpMethod.Put, "/api/v1/core/projection-assumptions", new
        {
            classRates = new Dictionary<string, string>
            {
                ["cash"] = "2", ["money_market"] = "2.5", ["bond"] = "3", ["equity"] = "6", ["mixed"] = "4",
                ["capital_guaranteed"] = "2.5", ["real_estate"] = "2", ["other"] = "0",
            },
            inflation = "2", monthlySavings = (string?)null, savingsClass = "cash", freedPaymentsToSavings = true,
            inflationByYear = new Dictionary<string, string> { ["2026"] = "3" },
            classRatesByYear = new Dictionary<string, Dictionary<string, string>> { ["cash"] = new() { ["2026"] = "2.5" } },
        });
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync(Ct));

        var series = (await GetAsync(app, "/points?s=projection:inflation,projection:rate:cash")).GetProperty("series");

        string[] Values(int i) => Points(series[i]).Select(p => p.Item2!).ToArray();
        var dates = Enumerable.Range(2025, 11).Select(y => $"{y}-01-01").ToArray();
        Assert.Equal(dates, Points(series[0]).Select(p => p.Item1!).ToArray());
        Assert.Equal(dates, Points(series[1]).Select(p => p.Item1!).ToArray());
        Assert.Equal(["2.4", "3", .. Enumerable.Repeat("2", 9)], Values(0));
        Assert.Equal(["2", "2.5", .. Enumerable.Repeat("2", 9)], Values(1));
    }

    [Fact]
    public async Task Points_of_a_known_series_with_no_data_answer_empty()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var view = await GetAsync(app, "/points?s=cpi,irl");

        Assert.All(view.GetProperty("series").EnumerateArray(), s => Assert.Empty(Points(s)));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("baseDate").ValueKind);
        var keys = Keys(await GetAsync(app, ""));
        Assert.DoesNotContain("cpi", keys);
        Assert.DoesNotContain("cpi:yoy", keys);
        Assert.DoesNotContain("irl", keys);
    }

    [Fact]
    public async Task A_stored_quote_without_rate_in_the_period_answers_empty()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        Assert.Empty(Points((await GetAsync(app, "/points?s=fx:GBP&from=2025-10-02")).GetProperty("series")[0]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?s=")]
    [InlineData("?s=CPI!")]
    [InlineData("?s=cpi,cpi")]
    [InlineData("?s=cpi,cpi:yoy,irl,fx:EUR,fx:GBP,projection:inflation,projection:rate:cash")]
    [InlineData("?s=cpi&from=2025-13-01")]
    public async Task Bad_selections_are_400(string query)
    {
        await using var app = await NidApp.StartAsync(fixture);

        var (status, code, _) = await FailureAsync(app, query);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        if (!query.Contains("from=")) Assert.Equal("core.series_invalid_key", code);
    }

    [Theory]
    [InlineData("?s=cpi,nope", "nope")]
    [InlineData("?s=fx:JPY", "fx:JPY")]
    [InlineData("?s=projection:rate:nope", "projection:rate:nope")]
    public async Task Unknown_series_are_404(string query, string key)
    {
        await using var app = await NidApp.StartAsync(fixture);
        await SeedAsync(app);

        Assert.Equal((HttpStatusCode.NotFound, "core.series_not_found", key), await FailureAsync(app, query));
    }
}
