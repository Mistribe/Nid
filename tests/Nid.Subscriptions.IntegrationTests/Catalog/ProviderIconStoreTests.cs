using Microsoft.EntityFrameworkCore;
using Nid.Subscriptions.Contracts;
using Nid.Subscriptions.Domain.Catalog;
using Nid.Subscriptions.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.Catalog;

/// <summary>1C2c design §5: the merge stores the declared URL; only the icon pass links an asset, and a sync keeps it.</summary>
[Collection("postgres")]
public class ProviderIconStoreTests(PostgresFixture fixture)
{
    private const string First = "https://1000logos.net/netflix.png";
    private const string Second = "https://upload.wikimedia.org/netflix.png";
    private static readonly string HashA = new('a', 64);
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 25, 7, 0);

    private static CatalogReading Reading(params (string Key, string Name, string? Icon)[] entries)
        => CatalogReader.Read(CatalogFixtures.StreamingLabels(), CatalogFixtures.Providers(entries)).Ok();

    /// <summary>What <c>CatalogSync</c> does in its transaction (1C2b Task 4): lock, read, plan, write.</summary>
    private static async Task<CatalogReport> SyncAsync(SubscriptionsDbContext db, CatalogReading reading)
    {
        var store = new ProviderStore(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var plan = CatalogMerge.Plan(await store.LockForSyncAsync(default), reading, CatalogFixtures.PinnedCommit);
        await store.WriteCatalogAsync(plan, Now, default);
        await transaction.CommitAsync();
        return plan.Report;
    }

    private static async Task<ProviderRow> RowAsync(string connectionString, string key)
    {
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        return await db.Providers.AsNoTracking().SingleAsync(p => p.Key == key);
    }

    [Fact]
    public async Task The_declared_url_is_stored_and_a_linked_icon_survives_a_sync_that_keeps_it()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        await SyncAsync(db, Reading(("netflix", "Netflix", First)));
        Assert.Equal((First, (string?)null, (string?)null), await IconOf(connectionString));

        Assert.Equal(1, await new ProviderStore(db).SetIconAsync("netflix", First, HashA, default)); // review follow-up, Minor 1
        Assert.Equal((First, HashA, First), await IconOf(connectionString));

        var renamed = await SyncAsync(db, Reading(("netflix", "Netflix HD", First)));

        Assert.Equal(["netflix"], renamed.ProvidersUpdated);
        Assert.Equal((First, HashA, First), await IconOf(connectionString)); // the rewrite did not drop the icon
    }

    [Fact]
    public async Task A_new_url_keeps_the_old_icon_until_the_pass_replaces_it()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        await SyncAsync(db, Reading(("netflix", "Netflix", First)));
        await new ProviderStore(db).SetIconAsync("netflix", First, HashA, default);

        await SyncAsync(db, Reading(("netflix", "Netflix", Second)));

        Assert.Equal((Second, HashA, First), await IconOf(connectionString)); // source ≠ url: the pass will fetch it
    }

    [Fact]
    public async Task A_provider_the_catalog_no_longer_gives_an_icon_loses_it()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        await SyncAsync(db, Reading(("netflix", "Netflix", First)));
        await new ProviderStore(db).SetIconAsync("netflix", First, HashA, default);

        await SyncAsync(db, Reading(("netflix", "Netflix", null)));

        Assert.Equal(((string?)null, (string?)null, (string?)null), await IconOf(connectionString));
    }

    [Fact]
    public async Task Linking_an_icon_fetched_from_a_url_the_catalog_left_does_nothing()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        await SyncAsync(db, Reading(("netflix", "Netflix", Second)));

        // review follow-up, Minor 1: the URL the pass fetched from is no longer the one the catalog declares —
        // nothing is linked, and CatalogIconPass reads that from the row count, not a coded refusal.
        Assert.Equal(0, await new ProviderStore(db).SetIconAsync("netflix", First, HashA, default));

        Assert.Equal((Second, (string?)null, (string?)null), await IconOf(connectionString));
    }

    [Fact]
    public async Task The_icons_to_look_at_are_the_live_catalog_providers_with_an_icon_in_key_order()
    {
        var connectionString = await fixture.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewSubscriptionsContext(connectionString);
        await SyncAsync(db, Reading(("b-one", "B", First), ("a-two", "A", Second), ("a2", "A2", First), ("c-none", "C", null), ("d-gone", "D", First)));
        await SyncAsync(db, Reading(("b-one", "B", First), ("a-two", "A", Second), ("a2", "A2", First), ("c-none", "C", null))); // d-gone archived

        var keys = (await new ProviderStore(db).CatalogIconsAsync(default)).Select(p => p.Key).ToList();

        Assert.Equal(["a-two", "a2", "b-one"], keys); // ordinal: '-' sorts before '2', whatever the database collation says
    }

    private static async Task<(string? Url, string? Asset, string? Source)> IconOf(string connectionString)
    {
        var row = await RowAsync(connectionString, "netflix");
        return (row.IconUrl, row.IconAsset, row.IconSource);
    }
}
