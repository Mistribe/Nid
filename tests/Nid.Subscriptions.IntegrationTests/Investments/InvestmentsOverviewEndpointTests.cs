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
