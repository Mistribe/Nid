using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.RealEstate.Infrastructure.Persistence;

public sealed partial class PropertyStore(RealEstateDbContext db) : IPropertyStore
{
    /// <summary>Property return spec §2: the stored document, versioned.</summary>
    private sealed record ReturnDocument(int Version, ReturnAssumptions Assumptions);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(ReturnDocument))]
    private sealed partial class ReturnJson : JsonSerializerContext;

    public async Task<ReturnAssumptions?> FindReturnAssumptionsAsync(ItemId item, CancellationToken ct)
    {
        var json = await db.Properties.AsNoTracking().Where(p => p.ItemId == item.Value).Select(p => p.ReturnAssumptions).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize(json, ReturnJson.Default.ReturnDocument)?.Assumptions;
    }

    public async Task SaveReturnAssumptionsAsync(ItemId item, ReturnAssumptions? assumptions, CancellationToken ct)
    {
        var json = assumptions is null ? null : JsonSerializer.Serialize(new ReturnDocument(1, assumptions), ReturnJson.Default.ReturnDocument);
        // Tracked, as every other write here: the write log reads the change tracker, which a bulk update would skip.
        var row = await db.Properties.SingleAsync(p => p.ItemId == item.Value, ct);
        row.ReturnAssumptions = json;
        await db.SaveChangesAsync(ct);
    }

    public async Task CreateAsync(ItemId item, PropertyDetails details, CancellationToken ct)
    {
        var row = new PropertyRow { ItemId = item.Value };
        Fill(row, details);
        db.Properties.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<StoredProperty?> FindAsync(ItemId item, CancellationToken ct)
        => (await FindManyAsync([item], ct)).SingleOrDefault();

    public async Task<IReadOnlyList<ItemId>> WithTenancyLabelAsync(IReadOnlyCollection<ItemId> items, string labelContains, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var pattern = LikePattern.Contains(labelContains);
        var found = await db.Tenancies.AsNoTracking().Where(t => ids.Contains(t.PropertyItemId) && EF.Functions.ILike(t.Label, pattern, @"\"))
            .Select(t => t.PropertyItemId).Distinct().ToListAsync(ct);
        return found.Select(i => new ItemId(i)).ToList();
    }

    public async Task<IReadOnlyList<StoredProperty>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var rows = await db.Properties.AsNoTracking().Where(p => ids.Contains(p.ItemId)).ToListAsync(ct);
        if (rows.Count == 0)
            return [];

        var found = rows.Select(p => p.ItemId).ToList();
        var costs = (await db.RecurringCosts.AsNoTracking().Where(c => found.Contains(c.PropertyItemId)).ToListAsync(ct)).ToLookup(c => c.PropertyItemId);
        var tenancies = (await db.Tenancies.AsNoTracking().Where(t => found.Contains(t.PropertyItemId)).ToListAsync(ct)).ToLookup(t => t.PropertyItemId);

        // Ordered here, by start date then id: the same order on every server (as a loan's fees).
        return rows.Select(r => new StoredProperty(new ItemId(r.ItemId), ToDetails(r), r.Version,
                costs[r.ItemId].OrderBy(c => c.StartsOn).ThenBy(c => c.Id).Select(ToCost).ToList(),
                tenancies[r.ItemId].OrderBy(t => t.StartsOn).ThenBy(t => t.Id).Select(ToTenancy).ToList()))
            .ToList();
    }

    public Task LockAsync(ItemId item, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"select 1 from realestate.property where item_id = {item.Value} for update", ct);

    public async Task UpdateAsync(ItemId item, PropertyDetails details, CancellationToken ct)
    {
        var row = await db.Properties.SingleAsync(p => p.ItemId == item.Value, ct);
        Fill(row, details);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddCostAsync(ItemId property, Guid id, RecurringCost cost, CancellationToken ct)
    {
        var row = new PropertyRecurringCostRow { Id = id, PropertyItemId = property.Value };
        Fill(row, cost);
        db.RecurringCosts.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateCostAsync(ItemId property, Guid id, RecurringCost cost, CancellationToken ct)
    {
        var row = await db.RecurringCosts.SingleAsync(c => c.Id == id && c.PropertyItemId == property.Value, ct);
        Fill(row, cost);
        db.Entry(row).State = EntityState.Modified; // the version moves even when nothing changed, as a fee's
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteCostAsync(ItemId property, Guid id, CancellationToken ct)
    {
        db.RecurringCosts.Remove(await db.RecurringCosts.SingleAsync(c => c.Id == id && c.PropertyItemId == property.Value, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task AddTenancyAsync(ItemId property, Guid id, Tenancy tenancy, CancellationToken ct, Guid? revisedFrom = null)
    {
        var row = new PropertyTenancyRow { Id = id, PropertyItemId = property.Value, RevisedFrom = revisedFrom };
        Fill(row, tenancy);
        db.Tenancies.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateTenancyAsync(ItemId property, Guid id, Tenancy tenancy, CancellationToken ct)
    {
        var row = await db.Tenancies.SingleAsync(t => t.Id == id && t.PropertyItemId == property.Value, ct);
        Fill(row, tenancy);
        db.Entry(row).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteTenancyAsync(ItemId property, Guid id, CancellationToken ct)
    {
        db.Tenancies.Remove(await db.Tenancies.SingleAsync(t => t.Id == id && t.PropertyItemId == property.Value, ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An identity field emptied (or only whitespace) is not stored.</summary>
    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

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
        row.EnergyClass = d.EnergyClass;
        row.PostalAddress = Blank(d.PostalAddress);
    }

    private static void Fill(PropertyRecurringCostRow row, RecurringCost c)
    {
        row.Category = c.Category;
        row.Amount = c.Amount;
        row.EveryCount = c.Every.Count;
        row.EveryUnit = LoanWire.Of(c.Every.Unit);
        row.StartsOn = c.StartsOn;
        row.EndsOn = c.EndsOn;
        row.Note = c.Note;
    }

    private static void Fill(PropertyTenancyRow row, Tenancy t)
    {
        row.Label = t.Label;
        row.StartsOn = t.StartsOn;
        row.EndsOn = t.EndsOn;
        row.RentMonthly = t.RentMonthly;
        row.ChargesMonthly = t.ChargesMonthly;
        row.Deposit = t.Deposit;
        row.Note = t.Note;
        row.Indexed = t.Indexed;
        row.IrlReferenceQuarter = t.IrlReferenceQuarter;
        row.TenantNames = Blank(t.TenantNames);
        row.LandlordName = Blank(t.LandlordName);
        row.LandlordAddress = Blank(t.LandlordAddress);
        row.RentsTrackedFrom = t.RentsTrackedFrom;
    }

    private static PropertyDetails ToDetails(PropertyRow r) => new(
        Parse<PropertyType>(r.PropertyType), r.PurchaseDate, r.PurchasePrice, r.NotaryFees, r.AgencyFees, r.OtherAcquisitionCosts,
        r.SurfaceM2, r.LocationLabel, Parse<PropertyCadence>(r.ValuationCadence), r.StaleAfterDays, r.RentalStartsOn, r.Note, r.EnergyClass, r.PostalAddress);

    /// <summary>The check constraints admit counts of 1 to 120 only: the Core's period always builds.</summary>
    private static StoredRecurringCost ToCost(PropertyRecurringCostRow r)
        => new(r.Id, new RecurringCost(r.Category, r.Amount,
            Duration.Create(r.EveryCount, Parse<DurationUnit>(r.EveryUnit)).TryGetValue(out var every)
                ? every
                : throw new InvalidOperationException($"{r.EveryCount} is not a stored period's count."),
            r.StartsOn, r.EndsOn, r.Note), r.Version);

    private static StoredTenancy ToTenancy(PropertyTenancyRow r)
        => new(r.Id, new Tenancy(r.Label, r.StartsOn, r.EndsOn, r.RentMonthly, r.ChargesMonthly, r.Deposit, r.Note, r.Indexed, r.IrlReferenceQuarter, r.TenantNames, r.LandlordName, r.LandlordAddress,
                r.RentsTrackedFrom),
            r.Version, r.RevisedFrom);

    /// <summary>The check constraints admit only these texts: anything else is a broken invariant.</summary>
    private static T Parse<T>(string text) where T : struct, Enum
        => LoanWire.TryParse<T>(text, out var value) ? value : throw new InvalidOperationException($"'{text}' is not a stored {typeof(T).Name}.");
}
