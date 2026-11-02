using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class ItemArchiveTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Archive_sets_and_unarchive_clears_the_date()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var persons = new PersonRegistry(db, PostgresFixture.Clock);
        var alice = (await persons.ProvisionAdultAsync("sub-a", "Alice", null, default, TestContext.Current.CancellationToken)).Ok();
        var items = new ItemRegistry(db, PostgresFixture.Clock);
        var item = (await items.CreateAsync(new NewItem("investments", "envelope", "PEA", Visibility.Shared, "EUR",
            [new OwnerShare(alice.Id, 10_000)]), TestContext.Current.CancellationToken)).Ok();
        var at = Instant.FromUtc(2025, 12, 31, 12, 0);

        Assert.Null((await items.FindAsync(item, TestContext.Current.CancellationToken))!.ArchivedAt);
        Assert.True((await items.ArchiveAsync(item, at, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Equal(at, (await items.FindAsync(item, TestContext.Current.CancellationToken))!.ArchivedAt);
        Assert.True((await items.UnarchiveAsync(item, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.Null((await items.FindAsync(item, TestContext.Current.CancellationToken))!.ArchivedAt);
    }

    [Fact]
    public async Task A_missing_item_is_not_found()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var items = new ItemRegistry(db, PostgresFixture.Clock);

        Assert.True((await items.ArchiveAsync(ItemId.New(), Instant.FromUtc(2025, 1, 1, 0, 0), TestContext.Current.CancellationToken)).TryGetFailure(out var failure));
        Assert.IsType<NotFoundFailure>(failure);
        Assert.True((await items.UnarchiveAsync(ItemId.New(), TestContext.Current.CancellationToken)).TryGetFailure(out failure));
        Assert.IsType<NotFoundFailure>(failure);
    }
}
