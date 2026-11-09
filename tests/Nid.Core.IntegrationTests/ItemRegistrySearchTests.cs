using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class ItemRegistrySearchTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ItemRow Item(string module, string kind, string name, Guid owner) => new()
    {
        Id = Guid.CreateVersion7(),
        Module = module,
        Kind = kind,
        DisplayName = name,
        Visibility = "shared",
        CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        Owners = [new OwnershipRow { ItemId = default, PersonId = owner, ShareBp = 10_000 }],
    };

    private static string[] Names(IEnumerable<ItemAccess> items) => items.Select(i => i.DisplayName).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Search_filters_by_module_kind_ids_and_name_with_wildcards_taken_literally()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var alice = Guid.CreateVersion7();
        db.Persons.Add(new PersonRow { Id = alice, Kind = "adult", OidcSubject = "sub-alice", DisplayName = "Alice", CreatedAt = SystemClock.Instance.GetCurrentInstant() });
        var items = new[]
        {
            Item("investments", "envelope", "PEA Alice", alice), Item("investments", "envelope", "Assurance vie Bob", alice),
            Item("investments", "envelope", "100% actions", alice), Item("realestate", "loan", "Prêt maison", alice),
        };
        foreach (var i in items)
            i.Owners[0].ItemId = i.Id;
        db.Items.AddRange(items);
        await db.SaveChangesAsync(Ct);

        var registry = new ItemRegistry(db, SystemClock.Instance);
        var all = items.Select(i => new ItemId(i.Id)).ToList();

        Assert.Equal(["PEA Alice"], Names(await registry.SearchAsync(all, "investments", "envelope", "pea", Ct)));
        Assert.Equal(["100% actions", "Assurance vie Bob", "PEA Alice"], Names(await registry.SearchAsync(all, "investments", "envelope", null, Ct)));
        Assert.Equal(["100% actions"], Names(await registry.SearchAsync(all, "investments", "envelope", "%", Ct)));
        Assert.Empty(await registry.SearchAsync(all, "investments", "envelope", "_", Ct));
        Assert.Empty(await registry.SearchAsync(all, "investments", "envelope", "\\", Ct));
        Assert.Empty(await registry.SearchAsync(all, "investments", "envelope", @"\", Ct));
        Assert.Empty(await registry.SearchAsync([all[0]], "realestate", "loan", null, Ct));
        var loan = Assert.Single(await registry.SearchAsync(all, "realestate", "loan", "maison", Ct));
        Assert.Equal(alice, loan.Owners.Single().PersonId.Value);
    }
}
