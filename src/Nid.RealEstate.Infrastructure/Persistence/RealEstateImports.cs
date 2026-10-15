using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Infrastructure.Persistence;

/// <summary>A row an import can mark (import-format.md §8): ref, hash and importer come together.</summary>
internal interface IImportMarked
{
    string? ImportRef { get; set; }
    string? ImportHash { get; set; }
    Guid? ImportedBy { get; set; }
}

/// <summary>Spec 3F1 §2: the marks of the module's eight tables; the rows themselves are written by <see cref="PropertyStore"/> and <see cref="LoanStore"/>.</summary>
public sealed class RealEstateImports(RealEstateDbContext db) : IRealEstateImports
{
    public Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedEstateRow>>> FindAsync(EstateTable table, IReadOnlyCollection<string> refs, CancellationToken ct)
        => table switch
        {
            EstateTable.Property => Find(db.Properties, refs, r => (r.ItemId, r.ItemId), ct),
            EstateTable.RecurringCost => Find(db.RecurringCosts, refs, r => (r.Id, r.PropertyItemId), ct),
            EstateTable.Tenancy => Find(db.Tenancies, refs, r => (r.Id, r.PropertyItemId), ct),
            EstateTable.Loan => Find(db.Loans, refs, r => (r.ItemId, r.ItemId), ct),
            EstateTable.Phase => Find(db.Phases, refs, r => (r.Id, r.LoanItemId), ct),
            EstateTable.Insurance => Find(db.Insurances, refs, r => (r.Id, r.LoanItemId), ct),
            EstateTable.Fee => Find(db.Fees, refs, r => (r.Id, r.LoanItemId), ct),
            EstateTable.Event => Find(db.Events, refs, r => (r.Id, r.LoanItemId), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };

    public async Task<IReadOnlyDictionary<Guid, ImportMark>> MarksAsync(EstateTable table, IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        return table switch
        {
            EstateTable.Property => await Marks(db.Properties.Where(r => ids.Contains(r.ItemId)), r => r.ItemId, ct),
            EstateTable.RecurringCost => await Marks(db.RecurringCosts.Where(r => ids.Contains(r.PropertyItemId)), r => r.Id, ct),
            EstateTable.Tenancy => await Marks(db.Tenancies.Where(r => ids.Contains(r.PropertyItemId)), r => r.Id, ct),
            EstateTable.Loan => await Marks(db.Loans.Where(r => ids.Contains(r.ItemId)), r => r.ItemId, ct),
            EstateTable.Fee => await Marks(db.Fees.Where(r => ids.Contains(r.LoanItemId)), r => r.Id, ct),
            EstateTable.Event => await Marks(db.Events.Where(r => ids.Contains(r.LoanItemId)), r => r.Id, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(table), "A phase's or an insurance's mark is read with its loan (LoanPhaseEntry.Import)."),
        };
    }

    public async Task MarkAsync(EstateTable table, Guid id, ImportMark mark, CancellationToken ct)
    {
        // Tracked: a row written a moment ago on this context is the same instance, so a later save never writes it back unmarked.
        IImportMarked row = table switch
        {
            EstateTable.Property => await db.Properties.SingleAsync(r => r.ItemId == id, ct),
            EstateTable.RecurringCost => await db.RecurringCosts.SingleAsync(r => r.Id == id, ct),
            EstateTable.Tenancy => await db.Tenancies.SingleAsync(r => r.Id == id, ct),
            EstateTable.Loan => await db.Loans.SingleAsync(r => r.ItemId == id, ct),
            EstateTable.Fee => await db.Fees.SingleAsync(r => r.Id == id, ct),
            EstateTable.Event => await db.Events.SingleAsync(r => r.Id == id, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(table), "A phase's or an insurance's mark is written with its loan's contract."),
        };
        Mark(row, mark);
        await db.SaveChangesAsync(ct);
    }

    internal static void Mark(IImportMarked row, ImportMark? mark)
    {
        row.ImportRef = mark?.Ref;
        row.ImportHash = mark?.Hash;
        row.ImportedBy = mark?.ImportedBy.Value;
    }

    internal static ImportMark? MarkOf(IImportMarked row)
        => row.ImportRef is null ? null : new ImportMark(row.ImportRef, row.ImportHash!.Trim(), new PersonId(row.ImportedBy!.Value));

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedEstateRow>>> Find<T>(
        IQueryable<T> rows, IReadOnlyCollection<string> refs, Func<T, (Guid Id, Guid Item)> keys, CancellationToken ct)
        where T : class, IImportMarked
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var found = await rows.AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ImportRef)).ToListAsync(ct);
        return found.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedEstateRow>)g.Select(r =>
            {
                var (id, item) = keys(r);
                return new ImportedEstateRow(id, new ItemId(item), r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value));
            }).ToList(),
            StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyDictionary<Guid, ImportMark>> Marks<T>(IQueryable<T> rows, Func<T, Guid> id, CancellationToken ct)
        where T : class, IImportMarked
        => (await rows.AsNoTracking().Where(r => r.ImportRef != null).ToListAsync(ct)).ToDictionary(id, r => MarkOf(r)!);
}
