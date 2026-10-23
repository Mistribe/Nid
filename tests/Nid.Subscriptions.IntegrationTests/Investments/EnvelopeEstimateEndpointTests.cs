using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Investments;

/// <summary>Holding prices spec §5.3: the envelope's estimate and each holding's latest price in the holdings view.</summary>
public class EnvelopeEstimateEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    internal const string EtfIsin = "IE00B4L5Y983";
    private const string UsIsin = "US0378331005";
    private const string OtherIsin = "FR0010315770";
    private const string UnknownIsin = "LU0274208692";

    // NidApp's today is 2025-10-15; the statement is two weeks older.
    internal static readonly LocalDate Day = new(2025, 10, 14);

    private static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<Guid> AddAsync(NidApp app, ItemId id, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Statement of 30/09 (or <paramref name="statement"/>): the ETF 10 u @ 100 = 1 000, the euro fund 500, and its valuation of 1 500; prices on.</summary>
    internal static async Task<(ItemId Id, Guid Etf)> EnvelopeAsync(
        NidApp app, bool enable = true, Visibility visibility = Visibility.Shared, string statement = "2025-09-30", params (string Isin, string Value)[] others)
    {
        var id = await app.NewEnvelopeAsync("PEA", app.AdultA, visibility);
        var euro = await AddAsync(app, id, new { name = "Fonds euros", kind = "euro_fund" });
        var etf = await AddAsync(app, id, new { name = "ETF Monde", kind = "etf", isin = EtfIsin });
        var lines = new List<object>
        {
            new { holdingId = euro, value = Eur("500.00") },
            new { holdingId = etf, units = "10", unitPrice = "100", value = Eur("1000.00") },
        };
        foreach (var (isin, value) in others)
            lines.Add(new { holdingId = await AddAsync(app, id, new { name = isin, kind = "fund", isin }), value = Eur(value) });
        var snapshot = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holding-snapshots",
            new { asOf = statement, recordValuation = true, lines });
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        if (enable)
            (await app.RawAsync(app.AdultA, HttpMethod.Patch, "/api/v1/admin/household", new { pricesEnabled = true })).EnsureSuccessStatusCode();
        return (id, etf);
    }

    internal static async Task PriceAsync(
        NidApp app, string isin, decimal price, LocalDate on, Currency? currency = null, PriceStatus status = PriceStatus.Accepted, decimal? previous = null)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().WriteAsync(
            new SecurityPrice(isin, on, price, currency ?? Currency.Eur, PriceSources.Boursorama, status, previous, Instant.FromUtc(2025, 10, 14, 20, 0), null), default);
    }

    private static async Task<JsonElement> HoldingsAsync(NidApp app, ItemId id, string query = "")
        => await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Url(id)}/holdings{query}"));

    private static decimal Amount(JsonElement estimate) => decimal.Parse(estimate.GetProperty("value").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);

    private static Dictionary<Guid, string> Stale(JsonElement estimate)
        => estimate.GetProperty("stale").EnumerateArray().ToDictionary(s => s.GetProperty("holdingId").GetGuid(), s => s.GetProperty("reason").GetString()!);

    [Fact]
    public async Task Without_prices_there_is_no_estimate()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeAsync(app, enable: false);
        await PriceAsync(app, EtfIsin, 110m, Day);

        var read = await HoldingsAsync(app, id);
        var estimate = read.GetProperty("estimate");

        Assert.Equal(JsonValueKind.Null, estimate.GetProperty("value").ValueKind);
        Assert.Equal("disabled", estimate.GetProperty("missingReason").GetString());
        // D9: off, nothing changes on screen, not even the prices already stored.
        Assert.All(read.GetProperty("holdings").EnumerateArray(), h => Assert.Equal(JsonValueKind.Null, h.GetProperty("price").ValueKind));
    }

    [Fact]
    public async Task A_priced_etf_is_units_times_its_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeAsync(app);
        await PriceAsync(app, EtfIsin, 110m, Day);

        var estimate = (await HoldingsAsync(app, id)).GetProperty("estimate");

        Assert.Equal(1_600m, Amount(estimate));
        Assert.Equal("EUR", estimate.GetProperty("value").GetProperty("currency").GetString());
        Assert.Equal(("2025-10-14", "2025-09-30", false), (estimate.GetProperty("asOf").GetString(), estimate.GetProperty("basedOn").GetString(),
            estimate.GetProperty("converted").GetBoolean()));
        Assert.Empty(Stale(estimate));
        Assert.Equal(JsonValueKind.Null, estimate.GetProperty("missingReason").ValueKind);
    }

    [Fact]
    public async Task A_missing_rate_keeps_the_statement_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeAsync(app, others: [(UsIsin, "300.00")]);
        await PriceAsync(app, EtfIsin, 110m, Day);
        await PriceAsync(app, UsIsin, 40m, Day, Currency.Usd);

        var read = await HoldingsAsync(app, id);
        var estimate = read.GetProperty("estimate");

        Assert.Equal(1_900m, Amount(estimate));
        var us = read.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("isin").GetString() == UsIsin).GetProperty("id").GetGuid();
        Assert.Equal("rate_missing", Assert.Single(Stale(estimate), s => s.Key == us).Value);
        Assert.False(estimate.GetProperty("converted").GetBoolean());
    }

    [Fact]
    public async Task A_stale_foreign_price_without_a_rate_is_stale()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeAsync(app, others: [(UsIsin, "300.00")]);
        await PriceAsync(app, EtfIsin, 110m, Day);
        await PriceAsync(app, UsIsin, 40m, new LocalDate(2025, 10, 1), Currency.Usd);

        var read = await HoldingsAsync(app, id);

        var us = read.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("isin").GetString() == UsIsin).GetProperty("id").GetGuid();
        Assert.Equal("stale_price", Stale(read.GetProperty("estimate"))[us]);
    }

    [Fact]
    public async Task A_newer_rejected_price_over_a_stale_accepted_one_is_rejected()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, etf) = await EnvelopeAsync(app);
        await PriceAsync(app, EtfIsin, 105m, new LocalDate(2025, 10, 1));
        await PriceAsync(app, EtfIsin, 200m, Day, status: PriceStatus.Rejected, previous: 105m);

        var estimate = (await HoldingsAsync(app, id)).GetProperty("estimate");

        Assert.Equal("no_newer_price", estimate.GetProperty("missingReason").GetString());
        Assert.Equal("rejected_price", Stale(estimate)[etf]);
    }

    [Fact]
    public async Task A_foreign_price_converts_at_the_rate_of_its_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeAsync(app);
        await PriceAsync(app, EtfIsin, 110m, Day, Currency.Usd);
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<IFxRateStore>().WriteAsync([new FxRate(Day, Currency.Eur, 0.9m, FxRateSource.Manual)], default);

        var estimate = (await HoldingsAsync(app, id)).GetProperty("estimate");

        Assert.Equal(10m * 99m + 500m, Amount(estimate));
        Assert.True(estimate.GetProperty("converted").GetBoolean());
    }

    [Fact]
    public async Task A_rejected_or_unknown_price_keeps_the_statement_value_with_its_reason()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, _) = await EnvelopeAsync(app, others: [(OtherIsin, "200.00"), (UnknownIsin, "100.00")]);
        await PriceAsync(app, EtfIsin, 110m, Day);
        await PriceAsync(app, OtherIsin, 50m, Day, status: PriceStatus.Rejected, previous: 20m);
        await using (var scope = Scope(app))
            await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().SaveListingAsync(
                new SecurityListing(UnknownIsin, null, null, Instant.FromUtc(2025, 10, 14, 20, 0), Instant.FromUtc(2025, 10, 14, 20, 0)), default);

        var read = await HoldingsAsync(app, id);
        var estimate = read.GetProperty("estimate");

        Assert.Equal(1_600m + 300m, Amount(estimate));
        var byIsin = read.GetProperty("holdings").EnumerateArray().Where(h => h.GetProperty("isin").ValueKind == JsonValueKind.String)
            .ToDictionary(h => h.GetProperty("isin").GetString()!, h => h.GetProperty("id").GetGuid());
        var stale = Stale(estimate);
        Assert.Equal(("rejected_price", "not_found"), (stale[byIsin[OtherIsin]], stale[byIsin[UnknownIsin]]));
    }

    [Fact]
    public async Task A_holding_shows_a_newer_rejected_price_with_the_previous_one()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (id, etf) = await EnvelopeAsync(app);
        await PriceAsync(app, EtfIsin, 110m, Day.PlusDays(-4));
        await PriceAsync(app, EtfIsin, 200m, Day, status: PriceStatus.Rejected, previous: 110m);

        var read = await HoldingsAsync(app, id);
        var price = read.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("id").GetGuid() == etf).GetProperty("price");

        Assert.Equal(("200.000000", "EUR", "2025-10-14", "boursorama", "rejected", "110.000000"), (price.GetProperty("value").GetString(),
            price.GetProperty("currency").GetString(), price.GetProperty("onDate").GetString(), price.GetProperty("source").GetString(),
            price.GetProperty("status").GetString(), price.GetProperty("previous").GetString()));
        // A newer refused price does not block an accepted one still fresh.
        Assert.Equal(1_600m, Amount(read.GetProperty("estimate")));

        // The estimate is a figure of today; the price is shown whatever the date.
        var past = await HoldingsAsync(app, id, "?on=2025-10-01");
        Assert.Equal(JsonValueKind.Null, past.GetProperty("estimate").ValueKind);
        Assert.Equal("rejected", past.GetProperty("holdings").EnumerateArray().Single(h => h.GetProperty("id").GetGuid() == etf)
            .GetProperty("price").GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_envelope_that_cannot_be_read_is_unreadable_without_a_failure()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (shared, _) = await EnvelopeAsync(app);
        var (hidden, _) = await EnvelopeAsync(app, visibility: Visibility.Private);
        await PriceAsync(app, EtfIsin, 110m, Day);
        var unknown = ItemId.New();

        async Task<IReadOnlyDictionary<ItemId, EnvelopeEstimate>> EstimateAsync(Actor actor, params ItemId[] ids)
        {
            await using var scope = app.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = actor;
            return await scope.ServiceProvider.GetRequiredService<EnvelopeEstimator>().EstimateAsync(ids, default);
        }
        static void Unreadable(EnvelopeEstimate e) => Assert.Equal((null, "unreadable"), (e.Value, e.MissingReason));

        // No person actor: every envelope is a key, none has a value.
        var system = await EstimateAsync(Actor.System, shared, hidden);
        Assert.Equal(2, system.Count);
        Unreadable(system[shared]);
        Unreadable(system[hidden]);
        // B cannot read A's private envelope; an id nobody knows has no currency.
        Unreadable((await EstimateAsync(Actor.Browser(app.AdultB), hidden))[hidden]);
        var mixed = await EstimateAsync(Actor.Browser(app.AdultB), shared, unknown);
        Assert.Equal(1_600m, mixed[shared].Value?.Amount);
        Unreadable(mixed[unknown]);
    }
}
