using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

/// <summary>Batched reads spec §2 and §4: the batched registry and policy answer, id by id, as the single ones.</summary>
[Collection("postgres")]
public class BatchedAccessTests(PostgresFixture postgres)
{
    private static readonly Instant At = Instant.FromUtc(2025, 12, 31, 12, 0);

    private sealed record World(PersonId Alice, PersonId Bob, PersonId Kid, ItemRegistry Items, AccessPolicy Policy, CoreDbContext Db)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<World> NewWorldAsync()
    {
        var db = await postgres.NewDatabaseAsync();
        var persons = new PersonRegistry(db, PostgresFixture.Clock);
        var items = new ItemRegistry(db, PostgresFixture.Clock);
        Assert.True((await persons.ProvisionAdultAsync("sub-a", "Alice", null, default)).TryGetValue(out var alice));
        Assert.True((await persons.PreRegisterAdultAsync("Bob", "bob@example.test", default)).IsSuccess);
        Assert.True((await persons.ProvisionAdultAsync("sub-b", "Bob", "bob@example.test", default)).TryGetValue(out var bob));
        Assert.True((await persons.CreateDependentAsync("Kid", default)).TryGetValue(out var kid));
        return new World(alice.Id, bob.Id, kid.Id, items, new AccessPolicy(items, persons), db);
    }

    private static async Task<ItemId> CreateAsync(World w, Visibility visibility, params OwnerShare[] owners)
        => (await w.Items.CreateAsync(new NewItem("test", "thing", "Thing", visibility, "EUR", owners), default)).Ok();

    [Fact]
    public async Task FindMany_builds_each_entry_as_Find_does_and_leaves_an_unknown_id_out()
    {
        await using var w = await NewWorldAsync();
        var mine = await CreateAsync(w, Visibility.Private, new OwnerShare(w.Alice, 10_000));
        var joint = await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Alice, 5_000), new OwnerShare(w.Bob, 5_000));
        var kids = await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Kid, 10_000));
        var closed = await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Bob, 10_000));
        Assert.True((await w.Items.ArchiveAsync(closed, At, TestContext.Current.CancellationToken)).IsSuccess);
        var unknown = ItemId.New();

        var found = await w.Items.FindManyAsync([mine, joint, kids, closed, unknown], TestContext.Current.CancellationToken);

        Assert.Equal(4, found.Count);
        Assert.False(found.ContainsKey(unknown));
        foreach (var id in new[] { mine, joint, kids, closed })
            Assert.Equivalent(await w.Items.FindAsync(id, TestContext.Current.CancellationToken), found[id], strict: true);
        Assert.Equal(At, found[closed].ArchivedAt);
        Assert.Equal("EUR", found[mine].Currency);
    }

    [Fact]
    public async Task CheckMany_keeps_exactly_the_ids_Check_accepts_for_each_adult_and_kind()
    {
        await using var w = await NewWorldAsync();
        var bobsPrivate = await CreateAsync(w, Visibility.Private, new OwnerShare(w.Bob, 10_000));
        var closed = await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Bob, 10_000));
        Assert.True((await w.Items.ArchiveAsync(closed, At, TestContext.Current.CancellationToken)).IsSuccess);
        List<ItemId> ids =
        [
            await CreateAsync(w, Visibility.Private, new OwnerShare(w.Alice, 10_000)),
            bobsPrivate,
            await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Alice, 10_000)),
            await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Alice, 5_000), new OwnerShare(w.Bob, 5_000)),
            await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Kid, 10_000)),
            closed,
            ItemId.New(),
        ];

        foreach (var actor in new[] { w.Alice, w.Bob })
            foreach (var kind in new[] { AccessKind.Read, AccessKind.Write })
            {
                var expected = new List<ItemId>();
                foreach (var id in ids)
                    if ((await w.Policy.CheckAsync(actor, id, kind, TestContext.Current.CancellationToken)).IsSuccess)
                        expected.Add(id);

                var actual = await w.Policy.CheckManyAsync(actor, ids, kind, TestContext.Current.CancellationToken);

                Assert.Equal(expected.OrderBy(i => i.Value), actual.OrderBy(i => i.Value));
            }

        var aliceReads = await w.Policy.CheckManyAsync(w.Alice, ids, AccessKind.Read, TestContext.Current.CancellationToken);
        Assert.Contains(closed, aliceReads); // archived: still read, as CheckAsync says
        Assert.DoesNotContain(bobsPrivate, aliceReads);
    }

    [Fact]
    public async Task No_id_reads_nothing_and_a_repeated_id_reads_once()
    {
        await using var w = await NewWorldAsync();
        var shared = await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Alice, 10_000));

        Assert.Empty(await w.Items.FindManyAsync([], TestContext.Current.CancellationToken));
        Assert.Empty(await w.Policy.CheckManyAsync(w.Alice, [], AccessKind.Read, TestContext.Current.CancellationToken));
        Assert.Equal([shared], (await w.Items.FindManyAsync([shared, shared], TestContext.Current.CancellationToken)).Keys);
        Assert.Equal([shared], await w.Policy.CheckManyAsync(w.Bob, [shared, shared], AccessKind.Read, TestContext.Current.CancellationToken));
    }
}
