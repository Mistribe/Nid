using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using UnambitiousFx.Functional.Failures;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class AccessPolicyTests(PostgresFixture postgres)
{
    private sealed record World(PersonId Alice, PersonId Bob, PersonId Kid, ItemRegistry Items, AccessPolicy Policy, CoreDbContext Db);

    private async Task<World> NewWorldAsync()
    {
        var db = await postgres.NewDatabaseAsync();
        var persons = new PersonRegistry(db, PostgresFixture.Clock);
        var items = new ItemRegistry(db, PostgresFixture.Clock);
        Assert.True((await persons.ProvisionAdultAsync("sub-a", "Alice", null, default)).TryGetValue(out var alice));
        Assert.True((await persons.ProvisionAdultAsync("sub-b", "Bob", null, default)).TryGetValue(out var bob));
        Assert.True((await persons.CreateDependentAsync("Kid", default)).TryGetValue(out var kid));
        return new World(alice.Id, bob.Id, kid.Id, items, new AccessPolicy(items, persons), db);
    }

    private static async Task<ItemId> CreateAsync(World w, Visibility visibility, params OwnerShare[] owners)
    {
        var result = await w.Items.CreateAsync(new NewItem("test", "thing", "Thing", visibility, null, owners), default);
        Assert.True(result.TryGetValue(out var id), result.ToString());
        return id;
    }

    [Fact]
    public async Task Private_item_of_alice_does_not_exist_for_bob()
    {
        var w = await NewWorldAsync();
        var item = await CreateAsync(w, Visibility.Private, new OwnerShare(w.Alice, 10_000));

        Assert.True((await w.Policy.CheckAsync(w.Alice, item, AccessKind.Write, default)).IsSuccess);

        var bobRead = await w.Policy.CheckAsync(w.Bob, item, AccessKind.Read, default);
        Assert.True(bobRead.TryGetFailure(out var failure));
        Assert.IsType<NotFoundFailure>(failure);
        await w.Db.DisposeAsync();
    }

    [Fact]
    public async Task Shared_item_of_alice_is_readable_but_not_writable_by_bob()
    {
        var w = await NewWorldAsync();
        var item = await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Alice, 10_000));

        Assert.True((await w.Policy.CheckAsync(w.Bob, item, AccessKind.Read, default)).IsSuccess);
        Assert.True((await w.Policy.CheckAsync(w.Bob, item, AccessKind.Write, default)).TryGetFailure(out var failure));
        Assert.IsType<UnauthorizedFailure>(failure);
        await w.Db.DisposeAsync();
    }

    [Fact]
    public async Task Item_owned_by_the_dependent_is_writable_by_both_adults()
    {
        var w = await NewWorldAsync();
        var item = await CreateAsync(w, Visibility.Shared, new OwnerShare(w.Kid, 10_000));

        Assert.True((await w.Policy.CheckAsync(w.Alice, item, AccessKind.Write, default)).IsSuccess);
        Assert.True((await w.Policy.CheckAsync(w.Bob, item, AccessKind.Write, default)).IsSuccess);
        await w.Db.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_item_is_not_found()
    {
        var w = await NewWorldAsync();
        var result = await w.Policy.CheckAsync(w.Alice, ItemId.New(), AccessKind.Read, default);
        Assert.True(result.TryGetFailure(out var failure));
        Assert.IsType<NotFoundFailure>(failure);
        await w.Db.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_ownership_is_refused_before_anything_is_written()
    {
        var w = await NewWorldAsync();
        var result = await w.Items.CreateAsync(
            new NewItem("test", "thing", "Thing", Visibility.Private, null, [new(w.Alice, 5_000), new(w.Bob, 5_000)]), default);

        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal("core.private_single_owner", failure!.Code);
        Assert.Empty(w.Db.Items);
        await w.Db.DisposeAsync();
    }
}
