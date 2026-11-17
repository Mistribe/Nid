using Microsoft.EntityFrameworkCore;
using Nid.Childcare.Application;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Childcare.Infrastructure.Persistence;

public sealed class NannyStore(ChildcareDbContext db) : INannyStore
{
    public async Task CreateAsync(ItemId id, CancellationToken ct, ImportMark? import = null)
    {
        db.Nannies.Add(new NannyRow { ItemId = id.Value, ImportRef = import?.Ref, ImportHash = import?.Hash, ImportedBy = import?.ImportedBy.Value });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ItemId>> FindManyAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        return (await db.Nannies.AsNoTracking().Where(n => wanted.Contains(n.ItemId)).Select(n => n.ItemId).ToListAsync(ct)).Select(i => new ItemId(i)).ToList();
    }

    public async Task<IReadOnlyList<NannyMonth>> MonthsOfAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        return (await db.NannyMonths.AsNoTracking().Where(m => wanted.Contains(m.ItemId)).OrderBy(m => m.ItemId).ThenBy(m => m.Month).ToListAsync(ct))
            .Select(m => new NannyMonth(new ItemId(m.ItemId), new YearMonth(m.Month.Year, m.Month.Month), m.Paid, m.Cmg, m.Hours)).ToList();
    }

    public async Task PutMonthAsync(NannyMonth month, CancellationToken ct)
    {
        var first = month.Month.OnDayOfMonth(1);
        var row = await db.NannyMonths.SingleOrDefaultAsync(m => m.ItemId == month.ItemId.Value && m.Month == first, ct);
        if (row is null)
            db.NannyMonths.Add(row = new NannyMonthRow { ItemId = month.ItemId.Value, Month = first });
        (row.Paid, row.Cmg, row.Hours) = (month.Paid, month.Cmg, month.Hours);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteMonthAsync(ItemId id, YearMonth month, CancellationToken ct)
    {
        var first = month.OnDayOfMonth(1);
        if (await db.NannyMonths.SingleOrDefaultAsync(m => m.ItemId == id.Value && m.Month == first, ct) is not { } row)
            return false;
        db.NannyMonths.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedArrangement>>> FindImportedAsync(IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Nannies.AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ImportRef)).ToListAsync(ct);
        return rows.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedArrangement>)g.Select(r => new ImportedArrangement(new ItemId(r.ItemId), r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value))).ToList(),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<ItemId, ImportMark>> ImportMarksAsync(IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var wanted = ids.Select(i => i.Value).ToList();
        return (await db.Nannies.AsNoTracking().Where(r => wanted.Contains(r.ItemId) && r.ImportRef != null).ToListAsync(ct))
            .ToDictionary(r => new ItemId(r.ItemId), r => new ImportMark(r.ImportRef!, r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value)));
    }

    public async Task ReplaceImportedAsync(ItemId id, IReadOnlyList<NannyMonth> months, ImportMark import, CancellationToken ct)
    {
        var row = await db.Nannies.SingleAsync(n => n.ItemId == id.Value, ct);
        (row.ImportRef, row.ImportHash, row.ImportedBy) = (import.Ref, import.Hash, import.ImportedBy.Value);
        db.NannyMonths.RemoveRange(await db.NannyMonths.Where(m => m.ItemId == id.Value).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        db.NannyMonths.AddRange(months.Select(m => new NannyMonthRow { ItemId = id.Value, Month = m.Month.OnDayOfMonth(1), Paid = m.Paid, Cmg = m.Cmg, Hours = m.Hours }));
        await db.SaveChangesAsync(ct);
    }
}
