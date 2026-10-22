using Microsoft.EntityFrameworkCore;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class VisibleItemsTests(PostgresFixture fixture)
{
    private static ItemRow Item(Guid id, string visibility, params (Guid person, int share)[] owners) => new()
    {
        Id = id,
        Module = "subscriptions",
        Kind = "subscription",
        DisplayName = $"item-{id:N}"[..12],
        Visibility = visibility,
        CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        Owners = owners.Select(o => new OwnershipRow { ItemId = id, PersonId = o.person, ShareBp = o.share }).ToList(),
    };

    private static PersonRow Person(Guid id, string kind, string? subject) => new()
    {
        Id = id,
        Kind = kind,
        OidcSubject = subject,
        DisplayName = kind + "-" + id.ToString("N")[..4],
        CreatedAt = SystemClock.Instance.GetCurrentInstant(),
    };

    /// <summary>A, B and D with a shared item each, a private item each, and one owned jointly.</summary>
    private static async Task<(Guid a, Guid b, Guid shared, Guid privateA, Guid privateB, Guid joint)> SeedAsync(CoreDbContext db)
    {
        Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), d = Guid.CreateVersion7();
        Guid shared = Guid.CreateVersion7(), privateA = Guid.CreateVersion7(),
             privateB = Guid.CreateVersion7(), joint = Guid.CreateVersion7();

        db.Persons.AddRange(Person(a, "adult", "sub-a"), Person(b, "adult", "sub-b"), Person(d, "dependent", null));
        db.Items.AddRange(
            Item(shared, "shared", (a, 10_000)),
            Item(privateA, "private", (a, 10_000)),
            Item(privateB, "private", (b, 10_000)),
            Item(joint, "shared", (a, 5_000), (b, 5_000)));
        await db.SaveChangesAsync();
        return (a, b, shared, privateA, privateB, joint);
    }

    [Fact]
    public async Task Household_shows_shared_items_and_no_private_one()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, shared, privateA, privateB, joint) = await SeedAsync(db);

        var visible = await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, default);

        Assert.Contains(new ItemId(shared), visible);
        Assert.Contains(new ItemId(joint), visible);
        Assert.DoesNotContain(new ItemId(privateA), visible);
        Assert.DoesNotContain(new ItemId(privateB), visible);
    }

    [Fact]
    public async Task Me_adds_the_actors_own_private_items_only()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, shared, privateA, privateB, joint) = await SeedAsync(db);

        var visible = await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, default);

        Assert.Contains(new ItemId(shared), visible);
        Assert.Contains(new ItemId(joint), visible);
        Assert.Contains(new ItemId(privateA), visible);
        Assert.DoesNotContain(new ItemId(privateB), visible);
    }

    [Fact]
    public async Task The_other_adults_private_item_is_never_visible_in_either_scope()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, b, _, privateA, privateB, _) = await SeedAsync(db);

        foreach (var scope in new[] { VisibilityScope.Household, VisibilityScope.Me })
        {
            Assert.DoesNotContain(new ItemId(privateB), await new VisibleItems(db).ForAsync(new PersonId(a), scope, default));
            Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), scope, default));
        }
    }

    [Fact]
    public async Task An_archived_item_is_not_visible()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, shared, _, _, _) = await SeedAsync(db);
        var item = await db.Items.SingleAsync(i => i.Id == shared);
        item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync();

        Assert.DoesNotContain(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, default));
    }

    [Fact]
    public async Task An_archived_shared_item_is_absent_from_active_sets_and_present_with_includeArchived()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, shared, _, _, _) = await SeedAsync(db);
        var item = await db.Items.SingleAsync(i => i.Id == shared);
        item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync();

        // Absent from active sets
        Assert.DoesNotContain(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, default));
        Assert.DoesNotContain(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, default));

        // Present with includeArchived: true
        Assert.Contains(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, includeArchived: true, default));
        Assert.Contains(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, includeArchived: true, default));
    }

    [Fact]
    public async Task An_archived_private_item_is_visible_only_to_its_owner_with_includeArchived()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, b, _, privateA, _, _) = await SeedAsync(db);
        var item = await db.Items.SingleAsync(i => i.Id == privateA);
        item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync();

        // Owner A: not visible without includeArchived, visible with it in Me scope (never in Household)
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, default));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, default));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, includeArchived: true, default));
        Assert.Contains(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, includeArchived: true, default));

        // Other adult B: never visible in any combination
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Household, default));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Me, default));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Household, includeArchived: true, default));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Me, includeArchived: true, default));
    }

    [Fact]
    public async Task Private_items_query_is_true_only_for_an_owner_of_an_active_private_item()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, _, privateA, privateB, _) = await SeedAsync(db);
        var d = Guid.CreateVersion7();
        db.Persons.Add(Person(d, "adult", "sub-d"));
        await db.SaveChangesAsync();

        async Task<bool> OwnsAsync(Guid person) => (await new GetMyPrivateItemsHandler(new VisibleItems(db),
            new ActorAccessor { Current = Actor.Browser(new PersonId(person)) }).HandleAsync(new GetMyPrivateItemsQuery())).Ok().Owns;

        Assert.True(await OwnsAsync(a));
        Assert.False(await OwnsAsync(d));

        // An archived private item does not count: « Moi » would show nothing more.
        foreach (var item in await db.Items.Where(i => i.Id == privateA || i.Id == privateB).ToListAsync())
            item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync();
        Assert.False(await OwnsAsync(a));
    }
}
