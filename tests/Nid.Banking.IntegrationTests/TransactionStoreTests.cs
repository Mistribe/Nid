using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;
using Duration = NodaTime.Duration;

namespace Nid.Banking.IntegrationTests;

/// <summary>Bank sync spec §2.5: booked transactions added once, found again by provider ref or fingerprint.</summary>
public class TransactionStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly LocalDate Day = new(2026, 10, 12);

    private static NewBankTransaction Line(string? providerRef, LocalDate on, decimal amount, string label = "PRLV SEPA NETFLIX")
        => new(providerRef, on, null, amount, Currency.Eur, label, null, null, "api");

    private static async Task<(AsyncServiceScope Scope, Guid Account)> SetUpAsync(NidApp app, string iban = "FR7630006000011234567890189")
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var now = NidApp.GoldenCaseToday.AtMidnight().InUtc().ToInstant();
        var id = await connections.CreatePendingAsync(app.AdultA, "Boursorama", "FR", [1, 2, 3], now + Duration.FromMinutes(15), default);
        await connections.ActivateAsync(id, "session", now + Duration.FromDays(180), default);
        var accounts = await scope.ServiceProvider.GetRequiredService<IAccountStore>()
            .UpsertFromSessionAsync(id, [new SessionAccount("uid", "hash", iban, "Compte", "CACC", "EUR")], default);
        return (scope, accounts[0].Id);
    }

    [Fact]
    public async Task Adding_the_same_lines_twice_adds_them_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        NewBankTransaction[] lines = [Line("a", Day, -13.49m), Line(null, Day, -2m, "CB  Café"), Line(null, Day, -2m, "CB  Café")];

        Assert.Equal(new TransactionWrite(3, 0), await store.AddAsync(account, lines, TestContext.Current.CancellationToken));
        Assert.Equal(new TransactionWrite(0, 3), await store.AddAsync(account, lines, TestContext.Current.CancellationToken));

        var stored = await store.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken);
        Assert.Equal(3, stored.Count);
        Assert.Equal(["CAFE", "CAFE", "NETFLIX"], stored.Select(t => t.NormalizedLabel).Order());
        Assert.All(stored.Where(t => t.ProviderRef != "a"), t => Assert.Equal(t.Fingerprint, t.ProviderRef)); // no provider ref: the fingerprint
        Assert.All(stored, t => Assert.Equal(TransactionLinkState.Unlinked, t.LinkState));
    }

    [Fact]
    public async Task A_known_provider_ref_with_another_fingerprint_is_known_and_left_unchanged()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [Line("a", Day, -10m, "LOYER")], TestContext.Current.CancellationToken);

        Assert.Equal(new TransactionWrite(0, 1), await store.AddAsync(account, [Line("a", Day.PlusDays(1), -11m, "LOYER BIS")], TestContext.Current.CancellationToken));
        var only = Assert.Single(await store.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken));
        Assert.Equal((Day, -10m, "LOYER"), (only.BookedOn, only.Amount, only.Label));
    }

    [Fact]
    public async Task Latest_booked_date_and_fetch_origin()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        Assert.Null(await store.LatestBookedOnAsync(account, null, TestContext.Current.CancellationToken));
        Assert.False(await store.HasFetchedViaAsync(account, "api", TestContext.Current.CancellationToken));

        await store.AddAsync(account, [Line("a", Day, -1m), Line("b", Day.PlusDays(3), -2m)], TestContext.Current.CancellationToken);

        Assert.Equal(Day.PlusDays(3), await store.LatestBookedOnAsync(account, null, TestContext.Current.CancellationToken));
        Assert.True(await store.HasFetchedViaAsync(account, "api", TestContext.Current.CancellationToken));
        Assert.False(await store.HasFetchedViaAsync(account, "file", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Near_finds_the_same_amount_within_one_day_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account,
        [
            Line("same", Day, -5m), Line("before", Day.PlusDays(-1), -5m), Line("after", Day.PlusDays(1), -5m),
            Line("far", Day.PlusDays(2), -5m), Line("other", Day, -6m),
        ], TestContext.Current.CancellationToken);

        var near = await store.NearAsync(account, [(Day, -5.00m)], TestContext.Current.CancellationToken);

        Assert.Equal(["after", "before", "same"], near.Select(t => t.ProviderRef).Order());
    }

    [Fact]
    public async Task Purge_removes_unlinked_and_ignored_lines_booked_before_and_keeps_linked_ones()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var db = scope.ServiceProvider.GetRequiredService<BankingDbContext>();
        var old = Day.PlusYears(-3);
        await store.AddAsync(account, [Line("u", old, -1m), Line("i", old, -2m), Line("l", old, -3m), Line("n", Day, -4m)], TestContext.Current.CancellationToken);
        foreach (var t in await db.Transactions.ToListAsync(cancellationToken: TestContext.Current.CancellationToken))
            t.LinkState = t.ProviderRef switch { "i" => "ignored", "l" => "linked", _ => t.LinkState };
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await store.PurgeBookedBeforeAsync(Day.PlusYears(-1), TestContext.Current.CancellationToken));

        Assert.Equal(["l", "n"], (await store.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken)).Select(t => t.ProviderRef).Order());
    }

    [Fact]
    public async Task A_new_line_sharing_a_fingerprint_with_a_line_of_another_ref_is_stored_at_the_next_rank()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        var a = Line("a", Day, -2.10m, "RATP");
        var b = Line("b", Day, -2.10m, "RATP");
        await store.AddAsync(account, [a], TestContext.Current.CancellationToken);

        // Newest first: b takes rank 1 in this read, where a had it.
        Assert.Equal(new TransactionWrite(1, 1), await store.AddAsync(account, [b, a], TestContext.Current.CancellationToken));
        Assert.Equal(new TransactionWrite(0, 2), await store.AddAsync(account, [b, a], TestContext.Current.CancellationToken));
        Assert.Equal(["a", "b"], (await store.ListAsync([account], null, null, 0, 10, TestContext.Current.CancellationToken)).Select(t => t.ProviderRef).Order());
    }

    [Fact]
    public async Task A_fingerprint_match_across_sources_is_the_same_line()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        var store = scope.ServiceProvider.GetRequiredService<ITransactionStore>();
        await store.AddAsync(account, [Line("a", Day, -2.10m, "RATP")], TestContext.Current.CancellationToken);

        Assert.Equal(new TransactionWrite(0, 1), await store.AddAsync(account, [Line("fitid-9", Day, -2.10m, "RATP") with { FetchedVia = "file" }], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Two_concurrent_adds_of_the_same_lines_store_them_once()
    {
        await using var app = await NidApp.StartAsync(fixture);
        var (scope, account) = await SetUpAsync(app);
        await using var _ = scope;
        await using var other = app.Services.CreateAsyncScope();
        other.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
        NewBankTransaction[] lines = [.. Enumerable.Range(0, 50).Select(i => Line($"r{i}", Day, -i - 1m, $"LABEL {i}"))];

        var results = await Task.WhenAll(
            scope.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(account, lines, TestContext.Current.CancellationToken),
            other.ServiceProvider.GetRequiredService<ITransactionStore>().AddAsync(account, lines, TestContext.Current.CancellationToken));

        Assert.Equal(50, results.Sum(r => r.Added));
        Assert.Equal(50, (await scope.ServiceProvider.GetRequiredService<ITransactionStore>().ListAsync([account], null, null, 0, 100, TestContext.Current.CancellationToken)).Count);
    }
}
