using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Subscriptions.IntegrationTests.RealEstate;

[Collection("postgres")]
public class PropertyStoreTests(PostgresFixture fixture)
{
    /// <summary>Every optional field set, cent-precise amounts and a half square metre: what the columns must keep.</summary>
    private static PropertyDetails Home() => new(PropertyType.Rental, new LocalDate(2020, 6, 15), 250_000.5m, 19_000m, 3_500.25m, 0m,
        85.5m, "Nantes", PropertyCadence.Manual, 200, new LocalDate(2020, 9, 1), "achat");

    [Fact]
    public async Task A_property_reads_back_whole_and_its_version_moves_on_a_change()
    {
        var store = new PropertyStore(PostgresFixture.NewRealEstateContext(await fixture.NewConnectionStringAsync()));
        var id = ItemId.New();
        await store.CreateAsync(id, Home(), default);

        var stored = (await store.FindAsync(id, default))!;
        Assert.Equal(Home(), stored.Details);
        Assert.NotEqual(0u, stored.Version);
        Assert.Null(await store.FindAsync(ItemId.New(), default));

        await store.UpdateAsync(id, Home() with { PurchasePrice = 255_000m, SurfaceM2 = null, Note = null }, default);
        var changed = (await store.FindAsync(id, default))!;
        Assert.Equal(Home() with { PurchasePrice = 255_000m, SurfaceM2 = null, Note = null }, changed.Details);
        Assert.NotEqual(stored.Version, changed.Version);

        await store.LockAsync(id, default); // outside a transaction the lock is released at once: it must simply not fail
        Assert.Equal([id], (await store.FindManyAsync([id, ItemId.New()], default)).Select(p => p.ItemId));
    }
}
