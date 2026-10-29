using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.TestSupport;

/// <summary>Holding prices spec §5.3: an envelope with a statement, and the prices its estimate reads; shared with the dashboard tests.</summary>
public static class EnvelopeEstimateApi
{
    public const string EtfIsin = "IE00B4L5Y983";

    // NidApp's today is 2025-10-15; the statement is two weeks older.
    public static readonly LocalDate Day = new(2025, 10, 14);

    public static string Url(ItemId id) => $"/api/v1/investments/envelopes/{id.Value}";

    public static object Eur(string amount) => new { amount, currency = "EUR" };

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    public static async Task<Guid> AddAsync(NidApp app, ItemId id, object body)
    {
        var response = await app.RawAsync(app.AdultA, HttpMethod.Post, $"{Url(id)}/holdings", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Statement of 30/09 (or <paramref name="statement"/>): the ETF 10 u @ 100 = 1 000, the euro fund 500, and its valuation of 1 500; prices on.</summary>
    public static async Task<(ItemId Id, Guid Etf)> EnvelopeAsync(
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

    public static async Task PriceAsync(
        NidApp app, string isin, decimal price, LocalDate on, Currency? currency = null, PriceStatus status = PriceStatus.Accepted, decimal? previous = null)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().WriteAsync(
            new SecurityPrice(isin, on, price, currency ?? Currency.Eur, PriceSources.Boursorama, status, previous, Instant.FromUtc(2025, 10, 14, 20, 0), null), default);
    }
}
