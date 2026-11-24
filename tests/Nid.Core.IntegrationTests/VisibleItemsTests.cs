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

        var visible = await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, TestContext.Current.CancellationToken);

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

        var visible = await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, TestContext.Current.CancellationToken);

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
            Assert.DoesNotContain(new ItemId(privateB), await new VisibleItems(db).ForAsync(new PersonId(a), scope, TestContext.Current.CancellationToken));
            Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), scope, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task An_archived_item_is_not_visible()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, shared, _, _, _) = await SeedAsync(db);
        var item = await db.Items.SingleAsync(i => i.Id == shared, cancellationToken: TestContext.Current.CancellationToken);
        item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_archived_shared_item_is_absent_from_active_sets_and_present_with_includeArchived()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, shared, _, _, _) = await SeedAsync(db);
        var item = await db.Items.SingleAsync(i => i.Id == shared, cancellationToken: TestContext.Current.CancellationToken);
        item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Absent from active sets
        Assert.DoesNotContain(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, TestContext.Current.CancellationToken));

        // Present with includeArchived: true
        Assert.Contains(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, includeArchived: true, TestContext.Current.CancellationToken));
        Assert.Contains(new ItemId(shared), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, includeArchived: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_archived_private_item_is_visible_only_to_its_owner_with_includeArchived()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, b, _, privateA, _, _) = await SeedAsync(db);
        var item = await db.Items.SingleAsync(i => i.Id == privateA, cancellationToken: TestContext.Current.CancellationToken);
        item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Owner A: not visible without includeArchived, visible with it in Me scope (never in Household)
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Household, includeArchived: true, TestContext.Current.CancellationToken));
        Assert.Contains(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(a), VisibilityScope.Me, includeArchived: true, TestContext.Current.CancellationToken));

        // Other adult B: never visible in any combination
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Household, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Me, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Household, includeArchived: true, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(new ItemId(privateA), await new VisibleItems(db).ForAsync(new PersonId(b), VisibilityScope.Me, includeArchived: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Private_items_query_is_true_only_for_an_owner_of_an_active_private_item()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, _, privateA, privateB, _) = await SeedAsync(db);
        var d = Guid.CreateVersion7();
        db.Persons.Add(Person(d, "adult", "sub-d"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        async Task<bool> OwnsAsync(Guid person) => (await new GetMyPrivateItemsHandler(new VisibleItems(db),
            new ActorAccessor { Current = Actor.Browser(new PersonId(person)) }, new NoNotices()).HandleAsync(new GetMyPrivateItemsQuery())).Ok().Owns;

        Assert.True(await OwnsAsync(a));
        Assert.False(await OwnsAsync(d));

        // An archived private item does not count: « Moi » would show nothing more.
        foreach (var item in await db.Items.Where(i => i.Id == privateA || i.Id == privateB).ToListAsync(cancellationToken: TestContext.Current.CancellationToken))
            item.ArchivedAt = SystemClock.Instance.GetCurrentInstant();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(await OwnsAsync(a));
    }

    [Fact]
    public async Task Mine_leaves_out_the_items_only_dependents_own_which_Me_keeps()
    {
        await using var db = await fixture.NewDatabaseAsync();
        var (a, _, shared, privateA, privateB, joint) = await SeedAsync(db);
        Guid child = Guid.CreateVersion7(), childItem = Guid.CreateVersion7(), withChild = Guid.CreateVersion7();
        db.Persons.Add(Person(child, "dependent", null));
        db.Items.AddRange(Item(childItem, "shared", (child, 10_000)), Item(withChild, "shared", (a, 5_000), (child, 5_000)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var items = new VisibleItems(db);

        var mine = await items.ForAsync(new PersonId(a), VisibilityScope.Mine, TestContext.Current.CancellationToken);
        var me = await items.ForAsync(new PersonId(a), VisibilityScope.Me, TestContext.Current.CancellationToken);

        // #360: a child's account is neither adult's « Moi »; one an adult holds with the child still is.
        Assert.Equal(new HashSet<ItemId> { new(shared), new(privateA), new(joint), new(withChild) }, mine);
        Assert.Equal(new HashSet<ItemId> { new(shared), new(privateA), new(joint), new(withChild), new(childItem) }, me);
        Assert.DoesNotContain(new ItemId(privateB), mine);
    }

    /// <summary>A household with no tax notice: the private items alone decide (a solo notice is TaxAssessmentEndpointTests').</summary>
    private sealed class NoNotices : IHouseholdTax
    {
        public Task<bool> DeclaresAloneAsync(PersonId person, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> DeclaresAsync(PersonId person, CancellationToken ct) => Task.FromResult(false);

        public Task<UnambitiousFx.Functional.Result<TaxYearFigures>> YearAsync(int year, VisibilityScope scope, LocalDate today, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<UnambitiousFx.Functional.Result<decimal>> RentalPurchaseAsync(VisibilityScope scope, LocalDate today, RentalPurchaseTax purchase, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<UnambitiousFx.Functional.Result<SaleItemView>> PropertySaleAsync(
            PersonId reader, ItemId property, LocalDate today, LocalDate on, decimal netPrice, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<UnambitiousFx.Functional.Result<IReadOnlyList<DashboardEventView>>> PerDeadlineAsync(
            PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DashboardEventView>> CalendarAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
