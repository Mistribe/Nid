using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
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
}
