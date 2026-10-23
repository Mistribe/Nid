using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Investments;

/// <summary>Holding prices spec §6: « Accepter ce cours », checked by the right to write the holding's envelope.</summary>
public class PriceAcceptTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Isin = "FR0000120271";
    private static readonly LocalDate Day = new(2026, 10, 23);

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<(ItemId Envelope, Guid Holding)> HoldAsync(NidApp app, Visibility visibility, string? isin = Isin)
    {
        var envelope = await app.NewEnvelopeAsync("PEA", app.AdultA, visibility);
        var holding = Guid.NewGuid();
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<IHoldingStore>().AddAsync(
            new EnvelopeHolding(holding, envelope, "Support", isin, "fund", null, null, null, null, null, null, null, null), default);
        return (envelope, holding);
    }

    private static async Task RejectAsync(NidApp app)
    {
        await using var scope = Scope(app);
        await scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>().WriteAsync(
            new SecurityPrice(Isin, Day, 50m, Currency.Eur, PriceSources.Boursorama, PriceStatus.Rejected, 100m, Instant.FromUtc(2026, 10, 23, 20, 0), null), default);
    }

    private static Task<HttpResponseMessage> AcceptAsync(NidApp app, PersonId by, ItemId envelope, Guid holding)
        => app.RawAsync(by, HttpMethod.Post, $"/api/v1/investments/envelopes/{envelope.Value}/holdings/{holding}/price/accept", new { onDate = "2026-10-23" });

    private static async Task<(string Status, Guid? By)> RowAsync(NidApp app)
    {
        await using var db = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        var row = await db.SecurityPrices.AsNoTracking().SingleAsync(p => p.Isin == Isin);
        return (row.Status, row.AcceptedBy);
    }

    [Fact]
    public async Task A_writer_accepts_a_rejected_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared);
        await RejectAsync(app);

        var response = await AcceptAsync(app, app.AdultA, envelope, holding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetBoolean());
        Assert.Equal(("accepted", app.AdultA.Value), await RowAsync(app));
    }

    [Fact]
    public async Task Accepting_needs_write_access()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Private);
        await RejectAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(app, app.AdultB, envelope, holding)).StatusCode);
        Assert.Equal(("rejected", null), await RowAsync(app));
    }

    [Fact]
    public async Task A_holding_of_another_envelope_or_without_isin_is_not_found()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, _) = await HoldAsync(app, Visibility.Shared);
        var (_, other) = await HoldAsync(app, Visibility.Shared);
        var (noIsinEnvelope, noIsin) = await HoldAsync(app, Visibility.Shared, isin: null);
        await RejectAsync(app);

        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(app, app.AdultA, envelope, other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AcceptAsync(app, app.AdultA, noIsinEnvelope, noIsin)).StatusCode);
        Assert.Equal(("rejected", null), await RowAsync(app));
    }

    [Fact]
    public async Task Without_a_rejected_price_that_day_it_is_a_conflict()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (envelope, holding) = await HoldAsync(app, Visibility.Shared);

        var response = await AcceptAsync(app, app.AdultA, envelope, holding);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("investments.price_not_rejected", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }
}
