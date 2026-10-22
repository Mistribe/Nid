using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Subscriptions.IntegrationTests.Investments;

/// <summary>Investments dashboard spec §3.4: <c>GET /investments/overview</c>, its span, its scope, and another adult's private envelope.</summary>
public class InvestmentsOverviewEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Overview = "/api/v1/investments/overview";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string? S(JsonElement element, string name) => element.GetProperty(name).GetString();

    /// <summary>investments-data-model.md §12.1: 20 000 opened, 2 000 paid in, worth 25 000 on 30 September.</summary>
    private static async Task<ItemId> PeaAsync(NidApp app, string name, PersonId owner, Visibility visibility)
    {
        var id = await app.NewEnvelopeAsync(name, owner, visibility);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2024-12-31", kind = "opening", amount = Eur("20000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-06-15", kind = "contribution", amount = Eur("2000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2024-12-31", amount = Eur("21000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("25000.00") });
        return id;
    }

    [Fact]
    public async Task The_overview_adds_up_the_open_envelopes_at_the_hosts_today()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var empty = await app.NewEnvelopeAsync("Livret vide", app.AdultA, Visibility.Shared);

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=household");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(("EUR", 2, 0, 1), (S(body, "currency"), body.GetProperty("openCount").GetInt32(), body.GetProperty("closedCount").GetInt32(),
            body.GetProperty("withoutValueCount").GetInt32()));
        Assert.Equal(("25000.00", "22000.00", "22000.00", "3000.00"), (S(body.GetProperty("value"), "amount"),
            S(body.GetProperty("netContributions"), "amount"), S(body.GetProperty("paidIn"), "amount"), S(body.GetProperty("gain"), "amount")));
        Assert.Equal(13.6m, body.GetProperty("gainPct").GetDecimal());
        // 25 000 − 21 000 at the start of the year, less the 2 000 paid in June.
        Assert.Equal("2000.00", S(body.GetProperty("sinceJanuary"), "amount"));
        Assert.Equal([pea.Value, empty.Value], body.GetProperty("envelopes").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        Assert.Equal(("savings", "100"), (S(body.GetProperty("byKind")[0], "kind"), body.GetProperty("byKind")[0].GetProperty("sharePct").GetDecimal().ToString("0")));
        Assert.Equal(app.AdultA.Value, Assert.Single(body.GetProperty("byPerson").EnumerateArray()).GetProperty("personId").GetGuid());

        // 12 month ends, then today; the last point is the value.
        var points = body.GetProperty("points");
        Assert.Equal(13, points.GetArrayLength());
        Assert.Equal(("2025-10-15", "25000.00"), (S(points[12], "on"), S(points[12].GetProperty("value"), "amount")));

        var watch = Assert.Single(body.GetProperty("watch").EnumerateArray());
        Assert.Equal(("no_value", empty.Value), (S(watch, "code"), watch.GetProperty("envelopeId").GetGuid()));
        Assert.Empty(body.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_span_is_a_bad_request_and_all_starts_at_the_first_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);

        var refused = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?span=5y");
        var all = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?span=all"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("2024-12-31", S(all.GetProperty("points")[0], "on"));
    }

    [Fact]
    public async Task Another_adults_private_envelope_is_in_no_figure_and_the_scope_applies()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var shared = await PeaAsync(app, "PEA de B", app.AdultB, Visibility.Shared);
        var hidden = await PeaAsync(app, "Secret de B", app.AdultB, Visibility.Private);

        var household = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=household"));
        var mine = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Overview}?scope=me"));
        var theirs = await JsonAsync(await app.RawAsync(app.AdultB, HttpMethod.Get, $"{Overview}?scope=me"));

        Assert.Equal([shared.Value], household.GetProperty("envelopes").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        Assert.Equal("25000.00", S(household.GetProperty("value"), "amount"));
        Assert.DoesNotContain(hidden.Value.ToString(), household.ToString());
        // Moi: the shared envelopes and the reader's own private ones.
        Assert.Equal([shared.Value], mine.GetProperty("envelopes").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        Assert.Equal(2, theirs.GetProperty("openCount").GetInt32());
    }
}

/// <summary>Investments dashboard spec §4.4: <c>GET /investments/overview/performance</c>, its refusals, its scope.</summary>
public class InvestmentsPerformanceEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Performance = "/api/v1/investments/overview/performance";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string? S(JsonElement element, string name) => element.GetProperty(name).GetString();

    /// <summary>investments-data-model.md §12.1: 21 000 on 31 December, 2 000 paid in June, 25 000 on 30 September.</summary>
    private static async Task<ItemId> PeaAsync(NidApp app, string name, PersonId owner, Visibility visibility)
    {
        var id = await app.NewEnvelopeAsync(name, owner, visibility);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2024-12-31", kind = "opening", amount = Eur("20000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2025-06-15", kind = "contribution", amount = Eur("2000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2024-12-31", amount = Eur("21000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("25000.00") });
        return id;
    }

    [Fact]
    public async Task The_period_decomposes_the_change_in_value_month_by_month()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var pea = await PeaAsync(app, "PEA", app.AdultA, Visibility.Shared);
        var late = await app.NewEnvelopeAsync("Récent", app.AdultA, Visibility.Shared);
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{late.Value}/valuations", new { asOf = "2025-03-01", amount = Eur("100.00") });

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?scope=household&from=2024-12-31&to=2025-10-15");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(("21000.00", "25000.00", "2000.00", "0.00", "0.00", "2000.00"),
            (S(body.GetProperty("startValue"), "amount"), S(body.GetProperty("endValue"), "amount"), S(body.GetProperty("contributions"), "amount"),
                S(body.GetProperty("withdrawals"), "amount"), S(body.GetProperty("transfers"), "amount"), S(body.GetProperty("performance"), "amount")));
        Assert.Equal(8.7m, body.GetProperty("performancePct").GetDecimal());
        var months = body.GetProperty("months");
        Assert.Equal(("2025-01", "2025-10"), (S(months[0], "month"), S(months[months.GetArrayLength() - 1], "month")));
        Assert.Equal(("2000.00", "2025-09"), (S(months[8].GetProperty("performance"), "amount"), S(body, "bestMonth")));
        Assert.Equal(pea.Value, Assert.Single(body.GetProperty("envelopes").EnumerateArray()).GetProperty("id").GetGuid());
        var excluded = Assert.Single(body.GetProperty("excluded").EnumerateArray());
        Assert.Equal((late.Value, "no_start_value"), (excluded.GetProperty("envelopeId").GetGuid(), S(excluded, "code")));
    }

    [Fact]
    public async Task A_reversed_or_future_period_is_refused_and_another_adults_private_envelope_is_absent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await PeaAsync(app, "PEA de B", app.AdultB, Visibility.Shared);
        var hidden = await PeaAsync(app, "Secret de B", app.AdultB, Visibility.Private);

        var reversed = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?from=2025-09-30&to=2024-12-31");
        var future = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?from=2024-12-31&to=2025-12-31");
        var household = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Performance}?from=2024-12-31&to=2025-10-15"));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (reversed.StatusCode, future.StatusCode));
        Assert.Contains("investments.invalid_period", await reversed.Content.ReadAsStringAsync());
        Assert.Single(household.GetProperty("envelopes").EnumerateArray());
        Assert.DoesNotContain(hidden.Value.ToString(), household.ToString());
    }
}

/// <summary>Investments dashboard spec §5.4: <c>GET /investments/overview/holdings</c> and its scope.</summary>
public class InvestmentsHoldingsEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Holdings = "/api/v1/investments/overview/holdings";

    private static object Eur(string amount) => new { amount, currency = "EUR" };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string? S(JsonElement element, string name) => element.GetProperty(name).GetString();

    /// <summary>An envelope worth 25 000 on 30 September whose snapshot of that day details 24 000 in two holdings.</summary>
    private static async Task<ItemId> DetailedAsync(NidApp app, string name, PersonId owner, Visibility visibility)
    {
        var id = await app.NewEnvelopeAsync(name, owner, visibility);
        var url = $"/api/v1/investments/envelopes/{id.Value}";
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/flows", new { occurredOn = "2024-12-31", kind = "opening", amount = Eur("20000.00") });
        await app.RawAsync(owner, HttpMethod.Post, $"{url}/valuations", new { asOf = "2025-09-30", amount = Eur("25000.00") });
        var euro = (await JsonAsync(await app.RawAsync(owner, HttpMethod.Post, $"{url}/holdings", new { name = "Fonds euros", kind = "euro_fund" })))
            .GetProperty("id").GetGuid();
        var etf = (await JsonAsync(await app.RawAsync(owner, HttpMethod.Post, $"{url}/holdings", new { name = "ETF Monde", kind = "etf" })))
            .GetProperty("id").GetGuid();
        var snapshot = await app.RawAsync(owner, HttpMethod.Post, $"{url}/holding-snapshots", new
        {
            asOf = "2025-09-30", complete = true,
            lines = new object[] { new { holdingId = euro, value = Eur("15000.00") }, new { holdingId = etf, value = Eur("9000.00") } },
        });
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        return id;
    }

    [Fact]
    public async Task What_is_detailed_unallocated_and_undetailed_adds_up_to_the_value()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var detailed = await DetailedAsync(app, "Assurance-vie", app.AdultA, Visibility.Shared);
        var savings = await app.NewEnvelopeAsync("Livret", app.AdultA, Visibility.Shared);
        await app.RawAsync(app.AdultA, HttpMethod.Post, $"/api/v1/investments/envelopes/{savings.Value}/valuations", new { asOf = "2025-09-30", amount = Eur("500.00") });

        var response = await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Holdings}?scope=household");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(("25500.00", "24000.00", "1000.00", "500.00"), (S(body.GetProperty("value"), "amount"), S(body.GetProperty("detailed"), "amount"),
            S(body.GetProperty("unallocated"), "amount"), S(body.GetProperty("undetailed"), "amount")));
        Assert.Equal((1, 2), (body.GetProperty("detailedCount").GetInt32(), body.GetProperty("envelopeCount").GetInt32()));
        Assert.Equal(["Fonds euros", "ETF Monde"], body.GetProperty("holdings").EnumerateArray().Select(h => S(h, "name")));
        Assert.All(body.GetProperty("holdings").EnumerateArray(), h => Assert.Equal(detailed.Value, h.GetProperty("envelopeId").GetGuid()));
        Assert.Equal(savings.Value, Assert.Single(body.GetProperty("undetailedEnvelopes").EnumerateArray()).GetProperty("envelopeId").GetGuid());
    }

    [Fact]
    public async Task The_fees_by_envelope_and_their_projection_follow_the_assumptions_sent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var id = await DetailedAsync(app, "Assurance-vie", app.AdultA, Visibility.Shared);
        var etf = (await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Holdings))).GetProperty("holdings").EnumerateArray()
            .Single(h => S(h, "name") == "ETF Monde").GetProperty("holdingId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.RawAsync(app.AdultA, HttpMethod.Put,
            $"/api/v1/investments/envelopes/{id.Value}/holdings/{etf}/cost-reports/2024", new { grossReturnPct = 8m, totalFeesPct = 0.5m, netReturnPct = 7.5m })).StatusCode);

        // The reference's fees equal the holding's: no difference whatever the horizon.
        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Holdings}?assumedReturn=5&referenceFees=0.5"));

        var fees = Assert.Single(body.GetProperty("envelopeFees").EnumerateArray());
        Assert.Equal((0.5m, "45.00", 37.5m), (fees.GetProperty("totalFeesPct").GetDecimal(), S(fees.GetProperty("yearlyCost"), "amount"), fees.GetProperty("coveredPct").GetDecimal()));
        var projection = body.GetProperty("projection");
        Assert.Equal(("9000.00", 5m, 0.5m), (S(projection.GetProperty("base"), "amount"), projection.GetProperty("assumedReturnPct").GetDecimal(),
            projection.GetProperty("referenceFeesPct").GetDecimal()));
        Assert.All(projection.GetProperty("horizons").EnumerateArray(), h => Assert.Equal("0.00", S(h.GetProperty("difference"), "amount")));

        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Holdings}?assumedReturn=30")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.RawAsync(app.AdultA, HttpMethod.Get, $"{Holdings}?referenceFees=-1")).StatusCode);
    }

    [Fact]
    public async Task Another_adults_private_envelope_is_absent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await DetailedAsync(app, "AV de B", app.AdultB, Visibility.Shared);
        var hidden = await DetailedAsync(app, "Secret de B", app.AdultB, Visibility.Private);

        var body = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Holdings));

        Assert.Equal(2, body.GetProperty("holdings").GetArrayLength());
        Assert.DoesNotContain(hidden.Value.ToString(), body.ToString());
    }
}
