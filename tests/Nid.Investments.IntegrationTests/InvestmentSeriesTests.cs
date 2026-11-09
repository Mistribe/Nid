using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Investments.IntegrationTests;

/// <summary>The explorer's investment series: an envelope's value and net contributions, a holding's unit price, with item rights.</summary>
public class InvestmentSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<Guid> AddHoldingAsync(NidApp app, ItemId id, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>An envelope of A with a euro fund and a fund « Fonds monde », valued 1 500 and snapshotted on 2025-06-30 (the fund at 10 a unit).</summary>
    private static async Task<(ItemId Id, Guid Euro, Guid Fund)> EnvelopeAsync(NidApp app, Visibility visibility)
    {
        var id = await app.NewEnvelopeAsync("Assurance-vie", app.AdultA, visibility);
        var euro = await AddHoldingAsync(app, id, new { name = "Fonds euros", kind = "euro_fund", assetClass = "capital_guaranteed" });
        var fund = await AddHoldingAsync(app, id, new { name = "Fonds monde", kind = "fund" });
        Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/valuations", new { asOf = "2025-06-30", amount = Eur("1500") })).StatusCode);
        var snapshot = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots", new
        {
            asOf = "2025-06-30", sourceLabel = "Relevé", recordValuation = false,
            lines = new object[]
            {
                new { holdingId = euro, value = Eur("500") },
                new { holdingId = fund, units = "100", unitPrice = "10", value = Eur("1000") },
            },
        });
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        return (id, euro, fund);
    }

    private static string[] Keys(JsonElement catalogue) => catalogue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()!).ToArray();

    private static (string, string)[] Points(JsonElement series, string field = "value") => series.GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString()!, p.GetProperty(field).GetString()!)).ToArray();

    [Fact]
    public async Task Catalogue_lists_a_shared_envelope_and_its_priced_holdings_for_the_other_adult()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await EnvelopeAsync(app, Visibility.Shared);

        var keys = Keys(await GetAsync(app, app.AdultB, "/api/v1/series?scope=household"));

        Assert.Contains($"envelope:{id.Value}:value", keys);
        Assert.Contains($"envelope:{id.Value}:contributions", keys);
        Assert.Contains($"holding:{id.Value}:{fund}:price", keys);
        Assert.DoesNotContain($"holding:{id.Value}:{euro}:price", keys);
        var found = await GetAsync(app, app.AdultB, "/api/v1/series?q=monde");
        Assert.Equal([$"holding:{id.Value}:{fund}:price"], Keys(found));
        Assert.Equal(("currency:EUR", id.Value.ToString()), (found.GetProperty("items")[0].GetProperty("unit").GetString(), found.GetProperty("items")[0].GetProperty("item").GetString()));
    }

    [Fact]
    public async Task Another_adults_private_envelope_is_absent_and_its_points_are_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await EnvelopeAsync(app, Visibility.Private);

        var keys = Keys(await GetAsync(app, app.AdultB, "/api/v1/series?scope=me"));
        Assert.DoesNotContain(keys, k => k.Contains(id.Value.ToString()));
        foreach (var key in new[] { $"envelope:{id.Value}:value", $"envelope:{id.Value}:contributions", $"holding:{id.Value}:{fund}:price" })
        {
            var failure = await GetAsync(app, app.AdultB, $"/api/v1/series/points?s={key}", HttpStatusCode.NotFound);
            Assert.Equal(("core.series_not_found", key), (failure.GetProperty("code").GetString(), failure.GetProperty("key").GetString()));
        }
    }

    [Fact]
    public async Task A_refused_holding_price_read_is_recorded_as_access_denied()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await EnvelopeAsync(app, Visibility.Private);

        await GetAsync(app, app.AdultB, $"/api/v1/series/points?s=holding:{id.Value}:{fund}:price", HttpStatusCode.NotFound);

        var denied = Assert.Single(await AuditApi.EventsAsync(app, SecurityEventNames.AccessDenied));
        Assert.Equal(app.AdultB.Value, denied.PersonId!.Value);
        Assert.Equal([id.Value], denied.ItemIds);
    }

    [Fact]
    public async Task The_readers_own_private_envelope_answers_under_the_household_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, fund) = await EnvelopeAsync(app, Visibility.Private);

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=envelope:{id.Value}:value,holding:{id.Value}:{fund}:price&scope=household");
    }

    [Fact]
    public async Task Envelope_series_are_its_value_series()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _, _) = await EnvelopeAsync(app, Visibility.Shared);

        var series = (await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=envelope:{id.Value}:value,envelope:{id.Value}:contributions")).GetProperty("series");

        var chart = (await GetAsync(app, app.AdultA, $"{Url(id)}/series")).GetProperty("points").EnumerateArray().ToArray();
        Assert.NotEmpty(chart);
        static string Num(JsonElement amount) => decimal.Parse(amount.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture).ToString("0.##########", CultureInfo.InvariantCulture);
        Assert.Equal(chart.Select(p => (p.GetProperty("asOf").GetString()!, Num(p.GetProperty("value")))), Points(series[0]));
        Assert.Equal(chart.Select(p => (p.GetProperty("asOf").GetString()!, Num(p.GetProperty("netContributions")))), Points(series[1]));
        Assert.Equal(("irregular", "envelope_value"), (series[0].GetProperty("frequency").GetString(), series[0].GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task A_holding_price_is_its_snapshot_unit_prices()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, euro, fund) = await EnvelopeAsync(app, Visibility.Shared);

        var series = (await GetAsync(app, app.AdultB, $"/api/v1/series/points?s=holding:{id.Value}:{fund}:price")).GetProperty("series")[0];

        Assert.Equal([("2025-06-30", "10")], Points(series));
        Assert.Equal(("Fonds monde", id.Value.ToString()), (series.GetProperty("name").GetString(), series.GetProperty("item").GetString()));
        await GetAsync(app, app.AdultB, $"/api/v1/series/points?s=holding:{id.Value}:{euro}:price", HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("envelope:nope:value")]
    [InlineData("holding:nope:nope:price")]
    [InlineData("holding:00000000-0000-0000-0000-000000000001:00000000-0000-0000-0000-000000000002:price")]
    [InlineData("loan")]
    public async Task Malformed_or_foreign_ids_are_404(string key)
    {
        await using var app = await NidApp.StartAsync(fixture);
        var loan = await app.NewLoanAsync("Prêt", Visibility.Shared, (app.AdultA, 10_000));
        key = key == "loan" ? $"envelope:{loan.Value}:value" : key;

        var failure = await GetAsync(app, app.AdultA, $"/api/v1/series/points?s={key}", HttpStatusCode.NotFound);

        Assert.Equal("core.series_not_found", failure.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_holding_of_another_envelope_is_404_under_a_visible_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (_, _, fund) = await EnvelopeAsync(app, Visibility.Shared);
        var other = await app.NewEnvelopeAsync("Assurance vie", app.AdultA, Visibility.Shared);

        await GetAsync(app, app.AdultA, $"/api/v1/series/points?s=holding:{other.Value}:{fund}:price", HttpStatusCode.NotFound);
    }
}
