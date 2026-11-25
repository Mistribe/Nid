using Microsoft.EntityFrameworkCore;
using Nid.Assets.Application;
using Nid.Assets.Domain;
using Nid.Core.Contracts;

namespace Nid.Assets.Infrastructure.Persistence;

public sealed class AssetRecordStore(AssetsDbContext db) : IAssetRecordStore
{
    public async Task AddAsync(ItemId id, AssetDetails details, CancellationToken ct, ImportMark? import = null)
    {
        var row = new AssetRow { ItemId = id.Value };
        Apply(row, details);
        Mark(row, import);
        db.Assets.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedAsset>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Assets.AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ImportRef))
            .Select(r => new { r.ItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct);
        return rows.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedAsset>)g.Select(r => new ImportedAsset(new ItemId(r.ItemId), r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value))).ToList(),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<Guid, ImportMark>> MarksAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        var rows = await db.Assets.AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ItemId))
            .Select(r => new { r.ItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct);
        return rows.ToDictionary(r => r.ItemId, r => new ImportMark(r.ImportRef!, r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value)));
    }

    public async Task ReplaceAsync(ItemId id, AssetDetails details, ImportMark import, CancellationToken ct)
    {
        var row = await db.Assets.SingleAsync(a => a.ItemId == id.Value, ct);
        Apply(row, details);
        Mark(row, import);
        await db.SaveChangesAsync(ct);
    }

    public Task LockAsync(ItemId id, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"select 1 from assets.asset where item_id = {id.Value} for update", ct);

    public async Task<AssetRecord?> FindAsync(ItemId id, CancellationToken ct)
        => await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.ItemId == id.Value, ct) is { } row ? ToRecord(row) : null;

    public async Task<IReadOnlyList<AssetRecord>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        return (await db.Assets.AsNoTracking().Where(a => wanted.Contains(a.ItemId)).ToListAsync(ct)).Select(ToRecord).ToList();
    }

    public async Task<bool> UpdateAsync(ItemId id, AssetDetails details, uint version, CancellationToken ct)
    {
        var row = await db.Assets.SingleOrDefaultAsync(a => a.ItemId == id.Value, ct);
        if (row is null)
            return false;
        Apply(row, details);
        db.Entry(row).Property(r => r.Version).OriginalValue = version;
        // Always written, so every edit bumps xmin and a later stale edit meets the conflict.
        db.Entry(row).Property(r => r.Kind).IsModified = true;
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task<AssetPage> ListAsync(IReadOnlyCollection<ItemId> candidates, string? kind, int skip, int take, CancellationToken ct)
    {
        var ids = candidates.Select(i => i.Value).ToList();
        var all = db.Assets.AsNoTracking().Where(a => ids.Contains(a.ItemId));
        var kinds = await all.Select(a => a.Kind).Distinct().OrderBy(k => k).ToListAsync(ct);
        var filtered = kind is null ? all : all.Where(a => a.Kind == kind);
        var total = await filtered.CountAsync(ct);
        var rows = await filtered.OrderByDescending(a => a.PurchasedOn != null).ThenByDescending(a => a.PurchasedOn).ThenBy(a => a.ItemId)
            .Skip(skip).Take(take).ToListAsync(ct);
        return new AssetPage(rows.Select(ToRecord).ToList(), total, kinds);
    }

    private static void Mark(AssetRow row, ImportMark? import)
    {
        row.ImportRef = import?.Ref;
        row.ImportHash = import?.Hash;
        row.ImportedBy = import?.ImportedBy.Value;
    }

    private static void Apply(AssetRow row, AssetDetails d)
    {
        row.Kind = d.Kind;
        row.PurchasePrice = d.PurchasePrice;
        row.PurchasedOn = d.PurchasedOn;
        row.Note = d.Note;
        row.WeightGrams = d.WeightGrams;
        row.Fineness = d.Fineness;
    }

    private static AssetRecord ToRecord(AssetRow r)
        => new(new ItemId(r.ItemId), new AssetDetails(r.Kind, r.PurchasePrice, r.PurchasedOn, r.Note, r.WeightGrams, r.Fineness), r.Version);
}
