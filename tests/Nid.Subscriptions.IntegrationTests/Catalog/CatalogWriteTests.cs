using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Npgsql;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

[Collection("postgres")]
public class CatalogWriteTests(PostgresFixture fixture)
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 24, 7, 0);

    private static CatalogReading RealReading() => CatalogReader.Read(CatalogFixtures.RealLabels(), CatalogFixtures.RealProviders()).Ok();

    /// <summary>What <c>CatalogSync</c> does in its transaction (Task 4): lock, read, plan, write.</summary>
    private static async Task<CatalogReport> SyncAsync(SubscriptionsDbContext db, CatalogReading reading)
    {
        var store = new ProviderStore(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var plan = CatalogMerge.Plan(await store.LockForSyncAsync(default), reading, CatalogFixtures.PinnedCommit);
        await store.WriteCatalogAsync(plan, Now, default);
        await transaction.CommitAsync();
        return plan.Report;
    }

    [Fact]
    public async Task The_real_catalog_is_written_then_found_unchanged()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);

        var first = await SyncAsync(db, RealReading());

        Assert.Equal(17, first.ProvidersAdded.Count);
        Assert.Equal(19, first.LabelsAdded.Count);
        Assert.Equal(["home"], first.LabelsCreated);
        await using var check = PostgresFixture.NewSubscriptionsContext(connectionString);
        var netflix = await check.Providers.AsNoTracking().SingleAsync(p => p.Key == "netflix");
        Assert.Equal(("catalog", "Netflix", CatalogFixtures.PinnedCommit), (netflix.Origin, netflix.Name, netflix.CatalogCommit));
        Assert.Equal(["streaming", "vod"], netflix.LabelKeys);
        var home = await check.Labels.AsNoTracking().SingleAsync(l => l.Key == "home");
        Assert.Equal(("Home", CatalogLimits.NeutralColor, "catalog"), (home.Name, home.Color, home.Origin));

        var second = await SyncAsync(db, RealReading());

        Assert.Equal(17, second.ProvidersUnchanged.Count);
        Assert.Empty(second.ProvidersAdded);
        Assert.Empty(second.ProvidersUpdated);
        Assert.Empty(second.LabelsAdded);
        Assert.Empty(second.LabelsUpdated);
        await using var after = PostgresFixture.NewSubscriptionsContext(connectionString);
        Assert.Equal(netflix.Version, (await after.Providers.AsNoTracking().SingleAsync(p => p.Key == "netflix")).Version); // not even rewritten
    }

    [Fact]
    public async Task A_provider_leaving_the_catalog_is_archived_keeps_its_subscription_and_comes_back()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        await SyncAsync(db, RealReading());
        var item = new ItemId(Guid.CreateVersion7());
        (await new SubscriptionStore(db).CreateAsync(item, new NewSubscription("netflix", true, [], null), default)).Ok();

        var without = RealReading() with { Providers = RealReading().Providers.Where(p => p.Key != "netflix").ToList() };
        var archived = await SyncAsync(db, without);

        Assert.Equal(["netflix"], archived.ProvidersArchived);
        await using (var check = PostgresFixture.NewSubscriptionsContext(connectionString))
        {
            Assert.Equal(Now, (await check.Providers.AsNoTracking().SingleAsync(p => p.Key == "netflix")).ArchivedAt);
            Assert.Equal("netflix", (await check.Subscriptions.AsNoTracking().SingleAsync(s => s.ItemId == item.Value)).ProviderKey);
        }

        var restored = await SyncAsync(db, RealReading());

        Assert.Equal(["netflix"], restored.ProvidersUpdated);
        await using var after = PostgresFixture.NewSubscriptionsContext(connectionString);
        Assert.Null((await after.Providers.AsNoTracking().SingleAsync(p => p.Key == "netflix")).ArchivedAt);
    }

    [Fact]
    public async Task Locking_outside_a_transaction_is_refused()
    {
        await using var db = PostgresFixture.NewSubscriptionsContext(await fixture.NewConnectionStringAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProviderStore(db).LockForSyncAsync(default));
    }

    /// <summary>
    /// Review Focus 5. An import holds an uncommitted custom provider whose key the new catalog version also names
    /// (practically impossible with generated keys — which is why nothing else would catch it). Read without the lock,
    /// the snapshot would miss it and the insert would collide; with it, the sync waits for the import, then sees the
    /// provider, leaves it alone and reports the conflict.
    /// </summary>
    [Fact]
    public async Task A_sync_waits_for_a_provider_being_committed_and_leaves_it_alone()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var import = new NpgsqlConnection(connectionString);
        await import.OpenAsync();
        await using var importTransaction = await import.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(
            "insert into subscriptions.provider (key, origin, name, label_keys, created_by) values ('tinder-k3f9qa', 'custom', 'Tinder', array['other'], gen_random_uuid())",
            import, importTransaction))
            await insert.ExecuteNonQueryAsync();

        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        var reading = RealReading() with
        {
            Providers = [.. RealReading().Providers, new CatalogProvider("tinder-k3f9qa", "Tinder (catalog)", null, null, ["streaming"])],
        };
        var sync = SyncAsync(db, reading);

        try
        {
            await WaitUntilTheProviderLockIsAwaitedAsync(connectionString);
            Assert.False(sync.IsCompleted);
        }
        finally
        {
            await importTransaction.CommitAsync(); // on failure too: releases the sync, so nothing is left waiting
        }

        var report = await sync.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["tinder-k3f9qa"], report.Conflicts);
        Assert.DoesNotContain("tinder-k3f9qa", report.ProvidersAdded);
        await using var check = PostgresFixture.NewSubscriptionsContext(connectionString);
        var tinder = await check.Providers.AsNoTracking().SingleAsync(p => p.Key == "tinder-k3f9qa");
        Assert.Equal(("custom", "Tinder", (Instant?)null), (tinder.Origin, tinder.Name, tinder.ArchivedAt));
    }

    /// <summary>
    /// Review Focus 5, the other half (final review H5): while a sync holds the provider table in SHARE ROW EXCLUSIVE,
    /// a subscription write linking an existing provider proceeds — its foreign-key check takes ROW SHARE, which the
    /// sync's lock admits — and completes before the sync ends.
    /// </summary>
    [Fact]
    public async Task A_subscription_links_a_provider_while_a_sync_holds_the_lock()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using (var first = PostgresFixture.NewSubscriptionsContext(connectionString))
            await SyncAsync(first, RealReading());

        await using var sync = PostgresFixture.NewSubscriptionsContext(connectionString);
        await using var syncTransaction = await sync.Database.BeginTransactionAsync();
        await new ProviderStore(sync).LockForSyncAsync(default);

        try
        {
            await using var write = PostgresFixture.NewSubscriptionsContext(connectionString);
            var item = new ItemId(Guid.CreateVersion7());
            var created = await new SubscriptionStore(write).CreateAsync(item, new NewSubscription("netflix", true, [], null), default)
                .WaitAsync(TimeSpan.FromSeconds(10)); // bounded: a write blocked by the sync's lock times out here
            created.Ok();

            await using var check = PostgresFixture.NewSubscriptionsContext(connectionString);
            Assert.Equal("netflix", (await check.Subscriptions.AsNoTracking().SingleAsync(s => s.ItemId == item.Value)).ProviderKey);
        }
        finally
        {
            await syncTransaction.RollbackAsync(); // on failure too: releases the lock, so nothing is left waiting
        }
    }

    /// <summary>
    /// Bounded: throws after 10 seconds. <c>pg_locks</c> spans the whole server, so only a wait on this database's
    /// <c>provider</c> table counts (a sync without the table lock would instead wait on the import's row, then collide).
    /// </summary>
    private static async Task WaitUntilTheProviderLockIsAwaitedAsync(string connectionString)
    {
        await using var probe = new NpgsqlConnection(connectionString);
        await probe.OpenAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var count = new NpgsqlCommand(
                """
                select count(*) from pg_locks
                where not granted and locktype = 'relation' and relation = 'subscriptions.provider'::regclass
                  and database = (select oid from pg_database where datname = current_database())
                """, probe);
            if ((long)(await count.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("The sync never waited on the import's lock.");
    }

    /// <summary>What a household synced by 1C2b sees at its first sync of the new pin: five labels change, nothing else.</summary>
    [Fact]
    public async Task Moving_from_the_1C2b_commit_to_the_pinned_one_updates_five_labels_and_only_moves_the_providers_commit()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        await SyncAtAsync(db, RealReading(), CatalogFixtures.LegacyCommit);

        var moved = await SyncAtAsync(db, CatalogReader.Read(CatalogFixtures.PinnedLabels(), CatalogFixtures.PinnedProviders()).Ok(), CatalogFixtures.PinnedCommit);

        Assert.Equal(17, moved.ProvidersUnchanged.Count);
        Assert.Empty(moved.ProvidersAdded);
        Assert.Empty(moved.ProvidersUpdated);
        Assert.Empty(moved.LabelsAdded);
        Assert.Equal(["cloud", "home", "hosting", "kid", "work"], moved.LabelsUpdated); // case of #A855B4, and home's own colour
        Assert.Empty(moved.LabelsCreated);
        await using var check = PostgresFixture.NewSubscriptionsContext(connectionString);
        Assert.All(await check.Providers.AsNoTracking().ToListAsync(), p => Assert.Equal(CatalogFixtures.PinnedCommit, p.CatalogCommit));
        var home = await check.Labels.AsNoTracking().SingleAsync(l => l.Key == "home");
        Assert.Equal(("Home", "#A1887F", "catalog"), (home.Name, home.Color, home.Origin));
    }

    private static async Task<CatalogReport> SyncAtAsync(SubscriptionsDbContext db, CatalogReading reading, string commit)
    {
        var store = new ProviderStore(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var plan = CatalogMerge.Plan(await store.LockForSyncAsync(default), reading, commit);
        await store.WriteCatalogAsync(plan, Now, default);
        await transaction.CommitAsync();
        return plan.Report;
    }
}
