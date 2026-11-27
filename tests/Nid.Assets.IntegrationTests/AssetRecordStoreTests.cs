using Microsoft.Extensions.DependencyInjection;
using Nid.Assets.Application;
using Nid.Assets.Contracts;
using Nid.Assets.Domain;
using Nid.Assets.Infrastructure.Persistence;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Nid.TestSupport;
using NodaTime;

namespace Nid.Assets.IntegrationTests;

public class AssetRecordStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ItemId> NewItemAsync(IServiceProvider sp, NidApp app)
    {
        sp.GetRequiredService<IActorAccessor>().Current = Actor.System;
        return (await sp.GetRequiredService<IItemRegistry>().CreateAsync(
            new NewItem("assets", "asset", "Asset " + Guid.NewGuid(), Visibility.Shared, null, [new OwnerShare(app.AdultA, 10_000)]), default)).Ok();
    }

    private static AssetDetails Art(decimal price = 1000m, int day = 1)
        => new(AssetKinds.Art, price, new LocalDate(2025, 1, day), null, null, null);

    [Fact]
    public async Task Add_then_find_round_trips_every_field()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        var details = new AssetDetails(AssetKinds.PreciousMetal, 2500.5m, new LocalDate(2024, 3, 9), "a bar", 31.103m, 999.9m);

        await store.AddAsync(id, details, Ct);

        var found = (await store.FindAsync(id, Ct))!;
        Assert.Equal(details, found.Details);
        Assert.Equal(id, found.ItemId);
        Assert.Equal(details, Assert.Single(await store.FindManyAsync([id], Ct)).Details);
    }

    [Fact]
    public async Task Add_then_find_round_trips_the_vehicle_fields()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        var details = new AssetDetails(AssetKinds.Vehicle, 9000m, new LocalDate(2022, 3, 9), null, null, null,
            new LocalDate(2022, 3, 1), 12_345, new LocalDate(2025, 1, 2), 6, AssetEnergies.PlugInHybrid, 12.5m);

        await store.AddAsync(id, details, Ct);

        Assert.Equal(details, (await store.FindAsync(id, Ct))!.Details);
    }

    [Fact]
    public async Task Lock_takes_the_row_without_failing()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.AddAsync(id, Art(), Ct);

        await store.LockAsync(id, Ct); // outside a transaction the lock is released at once: it must simply not fail
    }

    [Fact]
    public async Task Update_with_stale_version_returns_false()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.AddAsync(id, Art(), Ct);
        var read = (await store.FindAsync(id, Ct))!;

        Assert.True(await store.UpdateAsync(id, Art(1200m), read.Version, Ct));
        Assert.False(await store.UpdateAsync(id, Art(1300m), read.Version, Ct));
        Assert.Equal(1200m, (await store.FindAsync(id, Ct))!.Details.PurchasePrice);
    }

    [Fact]
    public async Task List_filters_by_kind_and_pages_over_candidates()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var ids = new List<ItemId>();
        for (var i = 0; i < 25; i++)
        {
            var id = await NewItemAsync(scope.ServiceProvider, app);
            ids.Add(id);
            await store.AddAsync(id, i < 5 ? new AssetDetails(AssetKinds.Vehicle, 900m, new LocalDate(2025, 1, 1 + i), null, null, null) : i >= 23 ? new AssetDetails(AssetKinds.Art, null, null, "valued", null, null) : Art(100m, 1 + i), Ct);
        }
        var outsider = await NewItemAsync(scope.ServiceProvider, app);
        await store.AddAsync(outsider, new AssetDetails(AssetKinds.Other, 1m, new LocalDate(2025, 1, 1), "x", null, null), Ct);

        var first = await store.ListAsync(ids, null, 0, 20, Ct);
        var second = await store.ListAsync(ids, null, 20, 20, Ct);
        var vehicles = await store.ListAsync(ids, AssetKinds.Vehicle, 0, 20, Ct);

        Assert.Equal(20, first.Rows.Count);
        var dates = first.Rows.Select(r => r.Details.PurchasedOn!.Value).ToList();
        Assert.Equal(dates.OrderByDescending(d => d).ToList(), dates);
        Assert.Equal([false, false, false, true, true], second.Rows.Select(r => r.Details.PurchasedOn is null).ToList());
        Assert.Equal(5, second.Rows.Count);
        Assert.Equal(25, first.Total);
        Assert.Equal(["art", "vehicle"], first.Kinds);
        Assert.Equal(5, vehicles.Total);
        Assert.Equal(["art", "vehicle"], vehicles.Kinds);
        Assert.DoesNotContain(first.Rows.Concat(second.Rows), r => r.ItemId == outsider);
    }

    [Fact]
    public async Task Deletion_part_removes_the_row()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(sp, app);
        await store.AddAsync(id, Art(), Ct);
        var access = (await sp.GetRequiredService<IItemRegistry>().FindManyAsync([id], Ct)).Values.Single();

        await sp.GetServices<IItemDeletionPart>().OfType<AssetsDeletionPart>().Single().RemoveAsync(access, Ct);

        Assert.Null(await store.FindAsync(id, Ct));
    }

    [Fact]
    public async Task Deletion_part_reports_an_imported_asset_only()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IAssetRecordStore>();
        var (plain, imported) = (await NewItemAsync(sp, app), await NewItemAsync(sp, app));
        await store.AddAsync(plain, Art(), Ct);
        await store.AddAsync(imported, Art(), Ct, new ImportMark("car-1", "hash", app.AdultA));
        var part = new AssetsDeletionPart(sp.GetRequiredService<AssetsDbContext>());

        Assert.False(await part.ImportedAsync(new ItemAccess(plain, "A", Visibility.Shared, []) with { Module = "assets", Kind = "asset" }, Ct));
        Assert.True(await part.ImportedAsync(new ItemAccess(imported, "B", Visibility.Shared, []) with { Module = "assets", Kind = "asset" }, Ct));
    }

    private static AssetDetails Car(int? km, LocalDate? on)
        => new(AssetKinds.Vehicle, 9000m, new LocalDate(2024, 1, 1), null, null, null, MileageKm: km, MileageOn: on);

    [Fact]
    public async Task Each_mileage_written_is_kept_as_a_reading()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.AddAsync(id, Car(12_000, new LocalDate(2026, 1, 10)), Ct);

        Assert.True(await store.UpdateAsync(id, Car(15_000, new LocalDate(2026, 6, 10)), (await store.FindAsync(id, Ct))!.Version, Ct));

        Assert.Equal([new MileageReading(new LocalDate(2026, 1, 10), 12_000), new MileageReading(new LocalDate(2026, 6, 10), 15_000)],
            await store.ReadingsAsync(id, Ct));
    }

    [Fact]
    public async Task A_reading_on_the_same_day_replaces_it()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.AddAsync(id, Car(15_000, new LocalDate(2026, 6, 10)), Ct);

        Assert.True(await store.UpdateAsync(id, Car(15_200, new LocalDate(2026, 6, 10)), (await store.FindAsync(id, Ct))!.Version, Ct));

        Assert.Equal([new MileageReading(new LocalDate(2026, 6, 10), 15_200)], await store.ReadingsAsync(id, Ct));
    }

    [Fact]
    public async Task Clearing_the_mileage_keeps_the_readings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(scope.ServiceProvider, app);
        await store.AddAsync(id, Car(12_000, new LocalDate(2026, 1, 10)), Ct);

        Assert.True(await store.UpdateAsync(id, Car(null, null), (await store.FindAsync(id, Ct))!.Version, Ct));

        Assert.Equal([new MileageReading(new LocalDate(2026, 1, 10), 12_000)], await store.ReadingsAsync(id, Ct));
    }

    [Fact]
    public async Task Deletion_part_removes_the_readings()
    {
        await using var app = await NidApp.StartAsync(fixture);
        await using var scope = app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var store = sp.GetRequiredService<IAssetRecordStore>();
        var id = await NewItemAsync(sp, app);
        await store.AddAsync(id, Car(12_000, new LocalDate(2026, 1, 10)), Ct);
        var access = (await sp.GetRequiredService<IItemRegistry>().FindManyAsync([id], Ct)).Values.Single();

        await sp.GetServices<IItemDeletionPart>().OfType<AssetsDeletionPart>().Single().RemoveAsync(access, Ct);

        Assert.Empty(await store.ReadingsAsync(id, Ct));
    }
}
