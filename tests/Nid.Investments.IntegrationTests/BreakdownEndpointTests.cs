using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;

namespace Nid.Investments.IntegrationTests;

/// <summary>Holdings exposure spec C2 and C4, through the holding's routes.</summary>
public class BreakdownEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Isin = "FR0000120271";

    private static readonly object[] Regions = [new { code = "north_america", pct = 70 }, new { code = "europe_ex_france", pct = 30 }];

    private static async Task<(ItemId Envelope, Guid Holding)> HoldAsync(NidApp app, Visibility visibility, string? isin = Isin, string kind = "etf")
    {
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, visibility);
        var holding = Guid.NewGuid();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        await scope.ServiceProvider.GetRequiredService<IHoldingStore>().AddAsync(
            new EnvelopeHolding(holding, envelope, "Support", isin, kind, null, null, null, null, null, null, null, null), default);
        return (envelope, holding);
    }

    private static string Url(ItemId envelope, Guid holding, string tail = "") => $"/api/v1/investments/envelopes/{envelope.Value}/holdings/{holding}/breakdown{tail}";

    private static Task<HttpResponseMessage> PutAsync(NidApp app, PersonId by, ItemId envelope, Guid holding, object regions, string asOf = "2025-10-01")
        => app.RawAsync(by, HttpMethod.Put, Url(envelope, holding), new { asOf, regions });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    [Fact]
    public async Task A_breakdown_is_recorded_read_and_replaced_on_the_same_date()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared);

        var put = await PutAsync(app, app.AdultA, envelope, holding, Regions);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await JsonAsync(put)).TryGetProperty("id", out _));

        var read = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url(envelope, holding)));
        Assert.False(read.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("documentId").ValueKind);
        Assert.False(read.TryGetProperty("recordedBy", out _));
        Assert.Equal(2, read.GetProperty("regions").GetArrayLength());

        await PutAsync(app, app.AdultA, envelope, holding, new[] { new { code = "japan", pct = 100 } });
        var replaced = await JsonAsync(await app.RawAsync(app.AdultA, HttpMethod.Get, Url(envelope, holding)));
        Assert.Equal("japan", replaced.GetProperty("regions")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_same_isin_in_another_envelope_reads_the_same_breakdown()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared);
        var (other, otherHolding) = await HoldAsync(app, Visibility.Shared);
        await PutAsync(app, app.AdultA, envelope, holding, Regions);

        var read = await app.RawAsync(app.AdultA, HttpMethod.Get, Url(other, otherHolding));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(2, (await JsonAsync(read)).GetProperty("regions").GetArrayLength());
    }

    [Fact]
    public async Task A_breakdown_on_a_euro_fund_is_refused()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared, kind: "euro_fund");

        var put = await PutAsync(app, app.AdultA, envelope, holding, Regions);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal("investments.breakdown_not_market", (await JsonAsync(put)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_breakdown_that_is_not_100_is_refused_at_its_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared);

        var put = await PutAsync(app, app.AdultA, envelope, holding, new[] { new { code = "japan", pct = 99.9 } });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var error = (await JsonAsync(put)).GetProperty("errors")[0];
        Assert.Equal("regions", error.GetProperty("path").GetString());
        Assert.Equal("investments.breakdown_not_100", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Another_adult_s_private_envelope_answers_404_for_read_and_write()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Private);
        await PutAsync(app, app.AdultA, envelope, holding, Regions);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Get, Url(envelope, holding))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(app, app.AdultB, envelope, holding, Regions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultB, HttpMethod.Delete, Url(envelope, holding, "/2025-10-01"))).StatusCode);
    }

    [Fact]
    public async Task A_holding_without_isin_answers_404()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared, isin: null);

        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, Url(envelope, holding))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(app, app.AdultA, envelope, holding, Regions)).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_version()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared);
        await PutAsync(app, app.AdultA, envelope, holding, Regions);

        Assert.Equal(HttpStatusCode.NoContent, (await app.RawAsync(app.AdultA, HttpMethod.Delete, Url(envelope, holding, "/2025-10-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Get, Url(envelope, holding))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.RawAsync(app.AdultA, HttpMethod.Delete, Url(envelope, holding, "/2025-10-01"))).StatusCode);
    }
}
