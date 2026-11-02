using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.IntegrationTests;

public class PropertyStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>Every optional field set, cent-precise amounts and a half square metre: what the columns must keep.</summary>
    private static PropertyDetails Home() => new(PropertyType.Rental, new LocalDate(2020, 6, 15), 250_000.5m, 19_000m, 3_500.25m, 0m,
        85.5m, "Nantes", PropertyCadence.Manual, 200, new LocalDate(2020, 9, 1), "achat", "D");

    [Fact]
    public async Task A_property_reads_back_whole_and_its_version_moves_on_a_change()
    {
        var store = new PropertyStore(PostgresFixture.NewRealEstateContext(await fixture.NewConnectionStringAsync()));
        var id = ItemId.New();
        await store.CreateAsync(id, Home(), TestContext.Current.CancellationToken);

        var stored = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(Home(), stored.Details);
        Assert.NotEqual(0u, stored.Version);
        Assert.Null(await store.FindAsync(ItemId.New(), TestContext.Current.CancellationToken));

        await store.UpdateAsync(id, Home() with { PurchasePrice = 255_000m, SurfaceM2 = null, Note = null }, TestContext.Current.CancellationToken);
        var changed = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(Home() with { PurchasePrice = 255_000m, SurfaceM2 = null, Note = null }, changed.Details);
        Assert.NotEqual(stored.Version, changed.Version);

        await store.LockAsync(id, TestContext.Current.CancellationToken); // outside a transaction the lock is released at once: it must simply not fail
        Assert.Equal([id], (await store.FindManyAsync([id, ItemId.New()], TestContext.Current.CancellationToken)).Select(p => p.ItemId));
    }

    private static Duration Every(int count, DurationUnit unit)
        => Duration.Create(count, unit).TryGetValue(out var every) ? every : throw new InvalidOperationException();

    [Fact]
    public async Task Costs_and_tenancies_read_back_by_start_date_and_their_versions_move_alone()
    {
        var store = new PropertyStore(PostgresFixture.NewRealEstateContext(await fixture.NewConnectionStringAsync()));
        var (id, other) = (ItemId.New(), ItemId.New());
        await store.CreateAsync(id, Home(), TestContext.Current.CancellationToken);
        await store.CreateAsync(other, Home(), TestContext.Current.CancellationToken);

        var yearly = new RecurringCost("property_tax", 1_200.5m, Every(1, DurationUnit.Year), new LocalDate(2021, 10, 15), new LocalDate(2030, 1, 1), "taxe");
        var quarterly = new RecurringCost("condo_fees", 250m, Every(3, DurationUnit.Month), new LocalDate(2020, 7, 1), null, null);
        var lease = new Tenancy("T2 meublé", new LocalDate(2020, 9, 1), new LocalDate(2023, 9, 1), 900.25m, 50m, 1_800m, "bail", true, 2);
        var (c1, c2, t1) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await store.AddCostAsync(id, c1, yearly, TestContext.Current.CancellationToken);
        await store.AddCostAsync(id, c2, quarterly, TestContext.Current.CancellationToken);
        await store.AddTenancyAsync(id, t1, lease, TestContext.Current.CancellationToken);

        var stored = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal([(c2, quarterly), (c1, yearly)], stored.Costs.Select(c => (c.Id, c.Cost))); // by start date
        Assert.Equal([(t1, lease)], stored.Tenancies.Select(t => (t.Id, t.Tenancy)));
        Assert.Null(stored.Tenancies[0].RevisedFrom);
        var t2 = Guid.CreateVersion7();
        await store.AddTenancyAsync(id, t2, lease with { StartsOn = new LocalDate(2023, 9, 1), EndsOn = null }, TestContext.Current.CancellationToken, revisedFrom: t1);
        Assert.Equal(t1, (await store.FindAsync(id, TestContext.Current.CancellationToken))!.Tenancies[1].RevisedFrom);
        await store.DeleteTenancyAsync(id, t2, TestContext.Current.CancellationToken);
        Assert.All(stored.Costs, c => Assert.NotEqual(0u, c.Version));
        Assert.Empty((await store.FindAsync(other, TestContext.Current.CancellationToken))!.Costs);

        var ended = lease with { EndsOn = new LocalDate(2024, 1, 1), Deposit = null };
        await store.UpdateTenancyAsync(id, t1, ended, TestContext.Current.CancellationToken);
        await store.UpdateCostAsync(id, c1, yearly, TestContext.Current.CancellationToken); // the same values: its version moves all the same
        var changed = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal(ended, changed.Tenancies[0].Tenancy);
        Assert.NotEqual(stored.Tenancies[0].Version, changed.Tenancies[0].Version);
        Assert.NotEqual(stored.Costs[1].Version, changed.Costs[1].Version);
        Assert.Equal(stored.Version, changed.Version); // the property's own row is untouched

        await store.DeleteCostAsync(id, c2, TestContext.Current.CancellationToken);
        await store.DeleteTenancyAsync(id, t1, TestContext.Current.CancellationToken);
        var left = (await store.FindAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal([c1], left.Costs.Select(c => c.Id));
        Assert.Empty(left.Tenancies);
    }
}
