using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Infrastructure.Persistence;

public sealed class PropertyStore(RealEstateDbContext db) : IPropertyStore
{
    public async Task CreateAsync(ItemId item, PropertyDetails details, CancellationToken ct)
    {
        var row = new PropertyRow { ItemId = item.Value };
        Fill(row, details);
        db.Properties.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<StoredProperty?> FindAsync(ItemId item, CancellationToken ct)
        => (await FindManyAsync([item], ct)).SingleOrDefault();

    public async Task<IReadOnlyList<StoredProperty>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        return (await db.Properties.AsNoTracking().Where(p => ids.Contains(p.ItemId)).ToListAsync(ct)).Select(ToStored).ToList();
    }

    public Task LockAsync(ItemId item, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"select 1 from realestate.property where item_id = {item.Value} for update", ct);

    public async Task UpdateAsync(ItemId item, PropertyDetails details, CancellationToken ct)
    {
        var row = await db.Properties.SingleAsync(p => p.ItemId == item.Value, ct);
        Fill(row, details);
        await db.SaveChangesAsync(ct);
    }

    private static void Fill(PropertyRow row, PropertyDetails d)
    {
        row.PropertyType = LoanWire.Of(d.Type);
        row.PurchaseDate = d.PurchaseDate;
        row.PurchasePrice = d.PurchasePrice;
        row.NotaryFees = d.NotaryFees;
        row.AgencyFees = d.AgencyFees;
        row.OtherAcquisitionCosts = d.OtherAcquisitionCosts;
        row.SurfaceM2 = d.SurfaceM2;
        row.LocationLabel = d.LocationLabel;
        row.ValuationCadence = LoanWire.Of(d.Cadence);
        row.StaleAfterDays = d.StaleAfterDays;
        row.RentalStartsOn = d.RentalStartsOn;
        row.Note = d.Note;
    }

    private static StoredProperty ToStored(PropertyRow r) => new(new ItemId(r.ItemId), new PropertyDetails(
        Parse<PropertyType>(r.PropertyType), r.PurchaseDate, r.PurchasePrice, r.NotaryFees, r.AgencyFees, r.OtherAcquisitionCosts,
        r.SurfaceM2, r.LocationLabel, Parse<PropertyCadence>(r.ValuationCadence), r.StaleAfterDays, r.RentalStartsOn, r.Note), r.Version);

    /// <summary>The check constraints admit only these texts: anything else is a broken invariant.</summary>
    private static T Parse<T>(string text) where T : struct, Enum
        => LoanWire.TryParse<T>(text, out var value) ? value : throw new InvalidOperationException($"'{text}' is not a stored {typeof(T).Name}.");
}
