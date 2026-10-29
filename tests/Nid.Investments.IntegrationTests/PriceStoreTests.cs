using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Core.Contracts;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Investments.IntegrationTests;

public class PriceStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string X = "FR0000000001", Y = "FR0000000002", Z = "FR0000000003", F = "FR0000000004", M = "FR0000000005";

    private static AsyncServiceScope Scope(NidApp app)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return scope;
    }

    private static async Task<ItemId> EnvelopeAsync(IServiceProvider sp, LocalDate? closedOn = null)
    {
        var item = ItemId.New();
        await sp.GetRequiredService<IEnvelopeStore>().CreateAsync(
            new Envelope(item, EnvelopeKind.Pea, "Banque", null, null, ValuationCadence.Monthly, null, null, closedOn), default);
        return item;
    }

    private static EnvelopeHolding Holding(ItemId envelope, string? isin, string kind = "etf", LocalDate? closedOn = null)
        => new(Guid.NewGuid(), envelope, "Support " + isin, isin, kind, null, null, null, null, null, null, closedOn, null);

    private static SecurityPrice Price(string isin, LocalDate day, decimal price, PriceStatus status = PriceStatus.Accepted, PersonId? by = null)
        => new(isin, day, price, Currency.Eur, PriceSources.Boursorama, status, null, Instant.FromUtc(2026, 10, 23, 20, 0), by);

    [Fact]
    public async Task Priced_isins_are_distinct_and_skip_closed()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var holdings = scope.ServiceProvider.GetRequiredService<IHoldingStore>();
        var open1 = await EnvelopeAsync(scope.ServiceProvider);
        var open2 = await EnvelopeAsync(scope.ServiceProvider);
        var closed = await EnvelopeAsync(scope.ServiceProvider, new LocalDate(2026, 1, 1));
        await holdings.AddAsync(Holding(open1, X), default);
        await holdings.AddAsync(Holding(open2, X), default);
        await holdings.AddAsync(Holding(closed, Y), default);
        await holdings.AddAsync(Holding(open1, Z, closedOn: new LocalDate(2026, 2, 1)), default);
        await holdings.AddAsync(Holding(open1, F, "euro_fund"), default);
        await holdings.AddAsync(Holding(open1, null), default);
        var manual = Holding(open1, M);
        await holdings.AddAsync(manual, default);
        await using (var db = PostgresFixture.NewInvestmentsContext(app.ConnectionString))
            await db.Database.ExecuteSqlAsync($"update investments.holding set auto_price = false where id = {manual.Id}");

        Assert.Equal([X], await holdings.PricedIsinsAsync(default));
    }

    [Fact]
    public async Task A_manually_accepted_price_is_not_overwritten()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>();
        var day = new LocalDate(2026, 10, 23);

        var person = new PersonId(Guid.NewGuid());
        Assert.True(await store.WriteAsync(Price(X, day, 100m, PriceStatus.Accepted, person), default));
        Assert.False(await store.WriteAsync(Price(X, day, 50m), default));
        Assert.Equal(100m, (await store.LatestAcceptedAsync([X], default))[X].Price);

        Assert.True(await store.WriteAsync(Price(Y, day, 10m), default));
        Assert.True(await store.WriteAsync(Price(Y, day, 11m), default));
        Assert.Equal(11m, (await store.LatestAcceptedAsync([Y], default))[Y].Price);
    }

    [Fact]
    public async Task Latest_accepted_ignores_rejected_and_the_purge_drops_old_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>();
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 22), 100m), default);
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 23), 1m, PriceStatus.Rejected), default);

        Assert.Equal(new LocalDate(2026, 10, 22), (await store.LatestAcceptedAsync([X, Y], default)).Single().Value.OnDate);

        Assert.Equal(1, await store.PurgeBeforeAsync(new LocalDate(2026, 10, 23), default));
        Assert.Empty(await store.LatestAcceptedAsync([X], default));
    }

    [Fact]
    public async Task Latest_gives_the_newest_accepted_and_a_rejected_only_when_more_recent()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>();
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 21), 100m), default);
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 22), 101m), default);
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 23), 1m, PriceStatus.Rejected), default);
        await store.WriteAsync(Price(Y, new LocalDate(2026, 10, 20), 1m, PriceStatus.Rejected), default);
        await store.WriteAsync(Price(Y, new LocalDate(2026, 10, 22), 10m), default);

        var latest = await store.LatestAsync([X, Y, Z], default);

        Assert.Equal([X, Y], latest.Keys.Order());
        Assert.Equal((101m, 1m), (latest[X].Accepted!.Price, latest[X].Rejected!.Price));
        Assert.Equal(10m, latest[Y].Accepted!.Price);
        Assert.Null(latest[Y].Rejected);
    }

    [Fact]
    public async Task Accepted_in_a_window_skips_rejected_and_other_days()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>();
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 19), 99m), default);
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 21), 100m), default);
        await store.WriteAsync(Price(X, new LocalDate(2026, 10, 22), 1m, PriceStatus.Rejected), default);
        await store.WriteAsync(Price(Y, new LocalDate(2026, 10, 21), 10m), default);

        var found = await store.AcceptedOnOrBeforeAsync([X], new LocalDate(2026, 10, 20), new LocalDate(2026, 10, 23), default);

        Assert.Equal([100m], found.Select(p => p.Price));
    }

    [Fact]
    public async Task Accepting_flips_a_rejected_row_and_records_who_and_when()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>();
        var day = new LocalDate(2026, 10, 23);
        var person = new PersonId(Guid.NewGuid());
        var at = Instant.FromUtc(2026, 10, 24, 8, 0);
        await store.WriteAsync(Price(X, day, 1m, PriceStatus.Rejected), default);
        await store.WriteAsync(Price(Y, day, 1m), default);

        Assert.True(await store.AcceptAsync(X, day, person, at, default));
        Assert.False(await store.AcceptAsync(X, day, person, at, default));
        Assert.False(await store.AcceptAsync(Y, day, person, at, default));
        Assert.False(await store.AcceptAsync(Z, day, person, at, default));

        await using var db = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        var row = await db.SecurityPrices.SingleAsync(p => p.Isin == X);
        Assert.Equal(("accepted", person.Value, at), (row.Status, row.AcceptedBy, row.AcceptedAt));
        Assert.False(await store.WriteAsync(Price(X, day, 5m), default));
    }

    [Fact]
    public async Task A_listing_round_trips()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var store = scope.ServiceProvider.GetRequiredService<ISecurityPriceStore>();
        var listing = new SecurityListing(X, "/bourse/opcvm/cours/MP-800128/", null, Instant.FromUtc(2026, 10, 23, 20, 0), null);

        await store.SaveListingAsync(listing, default);
        await store.SaveListingAsync(listing with { YahooSymbol = "CW8.PA" }, default);

        Assert.Equal(listing with { YahooSymbol = "CW8.PA" }, (await store.ListingsAsync([X, Y], default)).Single().Value);
    }

    [Fact]
    public async Task Only_one_run_at_a_time()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var log = scope.ServiceProvider.GetRequiredService<IPriceRunLog>();

        var first = await log.TryStartAsync(default);
        Assert.NotNull(first);
        Assert.Null(await log.TryStartAsync(default));
        Assert.True(await log.IsRunningAsync(default));

        await log.FinishAsync(first with { Status = PriceRunStatus.Succeeded, FinishedAt = first.StartedAt, Queried = 3, Accepted = 2, NotFound = 1 }, default);
        Assert.False(await log.IsRunningAsync(default));
        var second = await log.TryStartAsync(default);
        Assert.NotNull(second);

        var recent = await log.RecentAsync(5, default);
        Assert.Equal([second.Id, first.Id], recent.Select(r => r.Id));
        Assert.Equal((PriceRunStatus.Succeeded, 3, 2, 1), (recent[1].Status, recent[1].Queried, recent[1].Accepted, recent[1].NotFound));
    }

    [Fact]
    public async Task A_holding_without_auto_price_keeps_it_off()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var envelope = await EnvelopeAsync(scope.ServiceProvider);
        var id = Guid.NewGuid();
        await using (var db = PostgresFixture.NewInvestmentsContext(app.ConnectionString))
        {
            db.Holdings.Add(new HoldingRow { Id = id, EnvelopeItemId = envelope.Value, Name = "Manuel", Kind = "fund", AutoPrice = false });
            await db.SaveChangesAsync();
        }

        await using var read = PostgresFixture.NewInvestmentsContext(app.ConnectionString);
        Assert.False((await read.Holdings.SingleAsync(h => h.Id == id)).AutoPrice);
    }

    [Fact]
    public async Task Snapshot_unit_prices_take_the_latest_line_with_a_price()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = Scope(app);
        var holdings = scope.ServiceProvider.GetRequiredService<IHoldingStore>();
        var envelope = await EnvelopeAsync(scope.ServiceProvider);
        var holding = Holding(envelope, X);
        await holdings.AddAsync(holding, default);
        Task Save(LocalDate day, decimal? unitPrice) => holdings.SaveSnapshotAsync(new HoldingSnapshot(Guid.NewGuid(), envelope, day, null, true,
            [new SnapshotLine(holding.Id, 2m, unitPrice, 10m, null)]), default);
        await Save(new LocalDate(2026, 3, 31), 5m);
        await Save(new LocalDate(2026, 6, 30), 7m);
        await Save(new LocalDate(2026, 9, 30), null);

        var price = Assert.Single(await holdings.SnapshotUnitPricesAsync([X, Y], default));
        Assert.Equal(new SnapshotPrice(X, 7m, envelope, new LocalDate(2026, 6, 30)), price);
    }
}
