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

        await new ScenarioStore(db).SaveAsync(saved, TestContext.Current.CancellationToken);
        await new ScenarioStore(db).SaveAsync(saved with { Scope = VisibilityScope.Household }, TestContext.Current.CancellationToken); // replaces

        await using var other = PostgresFixture.NewCoreContext(connection);
        var found = await new ScenarioStore(other).FindAsync(item, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(VisibilityScope.Household, found.Scope);
        // Records compare their lists by reference: the lists are compared by sequence below, the rest by value.
        static PropertyPurchase Flat(PropertyPurchase d) => d with
        {
            Funding = Array.Empty<FundingLine>(), Loans = Array.Empty<ScenarioLoan>(),
            Assumptions = d.Assumptions with { ClassRates = new Dictionary<string, decimal>() },
        };
        Assert.Equal(Flat(saved.Document!) with { Assumptions = null! }, Flat(found.Document!) with { Assumptions = null! });
        Assert.Equal(Flat(saved.Document!).Assumptions with { ClassRates = null! }, Flat(found.Document!).Assumptions with { ClassRates = null! });
        Assert.Equal(saved.Document!.Funding, found.Document!.Funding);
        Assert.Equal(saved.Document!.Loans, found.Document!.Loans);
        Assert.Equal(saved.Document!.Assumptions.ClassRates.OrderBy(p => p.Key), found.Document!.Assumptions.ClassRates.OrderBy(p => p.Key));
    }

    [Fact]
    public async Task Find_many_skips_unknown_ids()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var item = await AddItemAsync(db);
        var store = new ScenarioStore(db);
        await store.SaveAsync(new StoredScenario(item, ScenarioKinds.PropertyPurchase, VisibilityScope.Me, Document(item)), TestContext.Current.CancellationToken);

        var found = await store.FindManyAsync([item, ItemId.New()], TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task Projection_scenario_round_trips()
    {
        var connection = await postgres.NewConnectionStringAsync();
        await using var db = PostgresFixture.NewCoreContext(connection);
        var item = await AddItemAsync(db, "Projection");
        var person = PersonId.New();
        var loan = ItemId.New();
        var property = ItemId.New();
        var origin = ItemId.New();
        var projection = new ProjectionScenario(
            1, new ScenarioHorizon(null, person),
            new ProjectionOverrides(new Dictionary<string, decimal> { ["equity"] = 0.08m }, 0.03m, 750m, "equity", false),
            [
                new ProjectionEvent(Guid.NewGuid(), ProjectionEventKinds.IncomeChange, new EventDate(new LocalDate(2028, 1, 1), null, null),
                    new EventDate(null, person, 60), "Temps partiel", -500m, null, null, null, null, null, null, null, null, null),
                new ProjectionEvent(Guid.NewGuid(), ProjectionEventKinds.SavingsChange, new EventDate(null, person, 45), null, null, 200m,
                    null, null, null, null, null, null, null, null, null),
                new ProjectionEvent(Guid.NewGuid(), ProjectionEventKinds.EarlyRepayment, new EventDate(new LocalDate(2029, 6, 1), null, null), null,
                    null, null, 10_000m, "even", loan, "shorten", 100m, null, null, null, null),
                new ProjectionEvent(Guid.NewGuid(), ProjectionEventKinds.PropertySale, new EventDate(new LocalDate(2030, 1, 1), null, null), null,
                    null, null, null, null, null, null, null, property, 300_000m, 15_000m, null),
                new ProjectionEvent(Guid.NewGuid(), ProjectionEventKinds.PropertyPurchase, new EventDate(new LocalDate(2031, 1, 1), null, null),
                    null, null, null, null, null, null, null, null, null, null, null, origin),
            ]);

        await new ScenarioStore(db).SaveAsync(
            new StoredScenario(item, ScenarioKinds.Projection, VisibilityScope.Household, null, projection), TestContext.Current.CancellationToken);

        await using var other = PostgresFixture.NewCoreContext(connection);
        var found = await new ScenarioStore(other).FindAsync(item, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(ScenarioKinds.Projection, found.Kind);
        Assert.Null(found.Document);
        var read = found.Projection!;
        Assert.Equal(projection.Version, read.Version);
        Assert.Equal(projection.Horizon, read.Horizon);
        Assert.Equal(projection.Assumptions with { ClassRates = null }, read.Assumptions with { ClassRates = null });
        Assert.Equal(projection.Assumptions.ClassRates!.OrderBy(p => p.Key), read.Assumptions.ClassRates!.OrderBy(p => p.Key));
        Assert.Equal(projection.Events, read.Events);
    }

    [Fact]
    public async Task The_migration_accepts_a_projection_kind()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var item = await AddItemAsync(db);

        var inserted = await db.Database.ExecuteSqlAsync(
            $"insert into core.scenario (item_id, kind, scope, data) values ({item.Value}, 'projection', 'me', '{{}}')", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, inserted);
    }

    [Fact]
    public async Task Old_projection_scenario_json_reads_with_null_yearly_overrides()
    {
        await using var db = await postgres.NewDatabaseAsync();
        var item = await AddItemAsync(db, "Projection");
        // The shape stored before the yearly overrides (#419 PR 3).
        db.Scenarios.Add(new ScenarioRow
        {
            ItemId = item.Value, Kind = ScenarioKinds.Projection, Scope = "household",
            Data = """{"version":1,"horizon":{"years":10,"retireOf":null},"assumptions":{"classRates":{"equity":0.08},"inflation":0.03,"monthlySavings":null,"savingsClass":null,"freedPaymentsToSavings":null},"events":[]}""",
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var found = await new ScenarioStore(db).FindAsync(item, TestContext.Current.CancellationToken);

        var overrides = found!.Projection!.Assumptions;
        Assert.Null(overrides.InflationByYear);
        Assert.Null(overrides.ClassRatesByYear);
        Assert.Null(overrides.IncomeGrowth);
        Assert.Equal((0.03m, 0.08m), (overrides.Inflation, overrides.ClassRates!["equity"]));
    }
}
