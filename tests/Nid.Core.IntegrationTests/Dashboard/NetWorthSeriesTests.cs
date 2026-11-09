using System.Globalization;
using System.Net;
using System.Text.Json;
using Nid.Core.Contracts;
using Nid.TestSupport;

namespace Nid.Core.IntegrationTests.Dashboard;

/// <summary>The explorer's net worth series: the dashboard curve and the projection's nominal curve, in the selector's scope.</summary>
public class NetWorthSeriesTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(NidApp app, PersonId reader, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.RawAsync(reader, HttpMethod.Get, url);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task ValueAsync(NidApp app, PersonId writer, ItemId envelope, string asOf, string amount)
        => Assert.Equal(HttpStatusCode.Created, (await app.RawAsync(writer, HttpMethod.Post,
            $"/api/v1/investments/envelopes/{envelope.Value}/valuations",
            new { asOf, amount = new { amount, currency = "EUR" }, note = (string?)null })).StatusCode);

    private static decimal Parse(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    private static (string, decimal)[] Points(JsonElement view) => view.GetProperty("series")[0].GetProperty("points").EnumerateArray()
        .Select(p => (p.GetProperty("date").GetString()!, Parse(p.GetProperty("value").GetString()!))).ToArray();

    private static async Task<(string, decimal)[]> CurveAsync(NidApp app, PersonId reader, string scope)
        => (await GetAsync(app, reader, $"/api/v1/dashboard/net-worth?period=all&scope={scope}")).GetProperty("points").EnumerateArray()
            .Where(p => p.GetProperty("value").ValueKind != JsonValueKind.Null)
            .Select(p => (p.GetProperty("on").GetString()!, Parse(p.GetProperty("value").GetProperty("amount").GetString()!))).ToArray();

    [Fact]
    public async Task Catalogue_lists_net_worth_and_its_projection_for_a_member()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var items = (await GetAsync(app, app.AdultB, "/api/v1/series?q=networth")).GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(["networth", "networth:projected"], items.Select(i => i.GetProperty("key").GetString()));
        Assert.Equal(["networth", "networth_projected"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.All(items, i => Assert.Equal(("currency:EUR", JsonValueKind.Null), (i.GetProperty("unit").GetString(), i.GetProperty("item").ValueKind)));
    }

    [Fact]
    public async Task Net_worth_is_the_dashboard_curve()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, envelope, "2025-06-30", "1000.00");

        var points = Points(await GetAsync(app, app.AdultA, "/api/v1/series/points?s=networth"));

        Assert.NotEmpty(points);
        Assert.Equal(await CurveAsync(app, app.AdultA, "household"), points);
    }

    [Fact]
    public async Task Net_worth_follows_the_selector()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var secret = await app.NewEnvelopeAsync("Secret", app.AdultA, Visibility.Private);
        await ValueAsync(app, app.AdultA, secret, "2025-06-30", "1000.00");
        var shared = await app.NewEnvelopeAsync("Partagé", app.AdultB, Visibility.Shared);
        await ValueAsync(app, app.AdultB, shared, "2025-06-30", "500.00");

        var asB = Points(await GetAsync(app, app.AdultB, "/api/v1/series/points?s=networth&scope=household"));
        var asA = Points(await GetAsync(app, app.AdultA, "/api/v1/series/points?s=networth&scope=me"));

        Assert.Equal(await CurveAsync(app, app.AdultB, "household"), asB);
        Assert.Equal(500m, asB[^1].Item2);
        Assert.Equal(await CurveAsync(app, app.AdultA, "me"), asA);
        Assert.NotEqual(asB[^1].Item2, asA[^1].Item2); // A sees the private envelope, B does not
    }

    [Fact]
    public async Task Projected_net_worth_is_the_projection_nominal_curve()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, Visibility.Shared);
        await ValueAsync(app, app.AdultA, envelope, "2025-06-30", "1000.00");

        var points = Points(await GetAsync(app, app.AdultA, "/api/v1/series/points?s=networth:projected"));

        var projection = await GetAsync(app, app.AdultA, "/api/v1/projection");
        Assert.Equal(projection.GetProperty("points").EnumerateArray().Select(p => (p.GetProperty("on").GetString()!, Parse(p.GetProperty("nominal").GetString()!))), points);
        Assert.NotEmpty(points);
        Assert.Equal(projection.GetProperty("horizonOn").GetString(), points[^1].Item1);
    }

    [Fact]
    public async Task An_unknown_net_worth_key_is_404()
    {
        await using var app = await NidApp.StartAsync(fixture);

        var failure = await GetAsync(app, app.AdultA, "/api/v1/series/points?s=networth:nope", HttpStatusCode.NotFound);

        Assert.Equal("core.series_not_found", failure.GetProperty("code").GetString());
    }
}
