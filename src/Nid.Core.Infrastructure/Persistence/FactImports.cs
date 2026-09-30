using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>2B1b: unguarded reads for the import and the export (see <see cref="IFactImports"/>). Current rows only.</summary>
public sealed class FactImports(CoreDbContext db) : IFactImports
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedFact>>> FindImportedAsync(
        FactTable table, IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var rows = table == FactTable.Valuation
            ? await db.Valuations.AsNoTracking().Where(r => r.RetiredAt == null && r.ImportRef != null && wanted.Contains(r.ImportRef))
                .Select(r => new { r.Id, r.ItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct)
            : await db.Flows.AsNoTracking().Where(r => r.RetiredAt == null && r.ImportRef != null && wanted.Contains(r.ImportRef))
                .Select(r => new { r.Id, r.ItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct);
        return rows.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedFact>)g.Select(r => new ImportedFact(r.Id, new ItemId(r.ItemId), r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value))).ToList(),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<StoredValuation>> ValuationsOfAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var rows = await db.Valuations.AsNoTracking().Where(r => r.RetiredAt == null && ids.Contains(r.ItemId))
            .OrderBy(r => r.ItemId).ThenBy(r => r.AsOf).ThenBy(r => r.Id).ToListAsync(ct);
        return rows.Select(r => new StoredValuation(Valuations.ToValuation(r), Mark(r.ImportRef, r.ImportHash, r.ImportedBy))).ToList();
    }

    public async Task<IReadOnlyList<StoredFlow>> FlowsOfAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var rows = await db.Flows.AsNoTracking().Where(r => r.RetiredAt == null && ids.Contains(r.ItemId))
            .OrderBy(r => r.ItemId).ThenBy(r => r.OccurredOn).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        return rows.Select(r => new StoredFlow(Flows.ToFlow(r), Mark(r.ImportRef, r.ImportHash, r.ImportedBy))).ToList();
    }

    private static ImportMark? Mark(string? reference, string? hash, Guid? by)
        => reference is null ? null : new ImportMark(reference, hash!.Trim(), new PersonId(by!.Value));
}
