using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Core.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Core.IntegrationTests;

[Collection("postgres")]
public class ScenarioStoreTests(PostgresFixture postgres)
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 22, 9, 0);

    private static async Task<ItemId> AddItemAsync(CoreDbContext db, string name = "Achat")
    {
        var id = Guid.CreateVersion7();
        db.Items.Add(new ItemRow { Id = id, Module = "core", Kind = "scenario", DisplayName = name, Currency = "EUR", CreatedAt = Now });
        await db.SaveChangesAsync();
        return new ItemId(id);
    }

    private static PropertyPurchase Document(ItemId funding) => new(
        1, PurchaseUse.Rental, new LocalDate(2027, 3, 1), 250_000m, 19_000m, 5_000m, 1_500m, 120m, 0m, 1_100m, 1, "micro",
        [new FundingLine(funding, 40_000m)],
        [new ScenarioLoan("Principal", 200_000m, 0.035m, 240, new ScenarioDeferral(DeferralKind.Total, 6),
            new ScenarioInsurance(ScenarioInsuranceKind.RateOnInitial, 0.0034m), 1_000m, 2_000m)],
        new ScenarioAssumptions(10, new Dictionary<string, decimal> { ["cash"] = 0.02m, ["equity"] = 0.06m }, 0.015m, 500m, "cash"));

    [Fact]
    public async Task A_scenario_document_round_trips()
    {
        var connection = await postgres.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewCoreContext(connection);
        var item = await AddItemAsync(db);
        var saved = new StoredScenario(item, ScenarioKinds.PropertyPurchase, VisibilityScope.Me, Document(item));

        await new ScenarioStore(db).SaveAsync(saved, default);
        await new ScenarioStore(db).SaveAsync(saved with { Scope = VisibilityScope.Household }, default); // replaces

        await using var other = PostgresFixture.NewCoreContext(connection);
        var found = await new ScenarioStore(other).FindAsync(item, default);

        Assert.NotNull(found);
        Assert.Equal(VisibilityScope.Household, found.Scope);
        // Records compare their lists by reference: the lists are compared by sequence below, the rest by value.
        static PropertyPurchase Flat(PropertyPurchase d) => d with
        {
            Funding = Array.Empty<FundingLine>(), Loans = Array.Empty<ScenarioLoan>(),
            Assumptions = d.Assumptions with { ClassRates = new Dictionary<string, decimal>() },
        };
        Assert.Equal(Flat(saved.Document) with { Assumptions = null! }, Flat(found.Document) with { Assumptions = null! });
        Assert.Equal(Flat(saved.Document).Assumptions with { ClassRates = null! }, Flat(found.Document).Assumptions with { ClassRates = null! });
        Assert.Equal(saved.Document.Funding, found.Document.Funding);
        Assert.Equal(saved.Document.Loans, found.Document.Loans);
        Assert.Equal(saved.Document.Assumptions.ClassRates.OrderBy(p => p.Key), found.Document.Assumptions.ClassRates.OrderBy(p => p.Key));
    }

    [Fact]
    public async Task Find_many_skips_unknown_ids()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var item = await AddItemAsync(db);
        var store = new ScenarioStore(db);
        await store.SaveAsync(new StoredScenario(item, ScenarioKinds.PropertyPurchase, VisibilityScope.Me, Document(item)), default);

        var found = await store.FindManyAsync([item, ItemId.New()], default);

        Assert.Equal([item], found.Select(s => s.Item));
    }

    [Fact]
    public async Task The_migration_refuses_an_unknown_kind()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var item = await AddItemAsync(db);

        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "insert into core.scenario (item_id, kind, scope, data) values ({0}, 'salary', 'me', '{}')", item.Value));
    }
}
