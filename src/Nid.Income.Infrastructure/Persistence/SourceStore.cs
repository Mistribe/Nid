using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Income.Contracts;
using NodaTime;

namespace Nid.Income.Infrastructure.Persistence;

public sealed class SourceStore(IncomeDbContext db) : ISourceStore
{
    public async Task CreateAsync(IncomeSource source, ImportMark? import, CancellationToken ct)
    {
        var row = new SourceRow
        {
            ItemId = source.ItemId.Value,
            Kind = IncomeWire.Of(source.Kind),
            PayerLabel = source.PayerLabel,
            Note = source.Note,
            EndedOn = source.EndedOn,
        };
        Mark(row, import);
        db.Sources.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IncomeSource?> FindAsync(ItemId item, CancellationToken ct)
        => await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.ItemId == item.Value, ct) is { } row ? ToSource(row) : null;

    public async Task<IReadOnlyList<IncomeSource>> FindManyAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Value).ToList();
        var rows = await db.Sources.AsNoTracking().Where(s => ids.Contains(s.ItemId)).ToListAsync(ct);
        return rows.Select(ToSource).ToList();
    }

    public async Task UpdateAsync(ItemId item, IncomeSourceChange change, CancellationToken ct)
    {
        var row = await db.Sources.SingleAsync(s => s.ItemId == item.Value, ct);
        if (change.Kind is { } kind) row.Kind = IncomeWire.Of(kind);
        if (change.ClearPayerLabel) row.PayerLabel = null; else if (change.PayerLabel is { } payer) row.PayerLabel = payer;
        if (change.ClearNote) row.Note = null; else if (change.Note is { } note) row.Note = note;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEndedOnAsync(ItemId item, LocalDate? endedOn, CancellationToken ct)
    {
        var row = await db.Sources.SingleAsync(s => s.ItemId == item.Value, ct);
        row.EndedOn = endedOn;
        await db.SaveChangesAsync(ct);
    }

    private static void Mark(SourceRow row, ImportMark? import)
    {
        row.ImportRef = import?.Ref;
        row.ImportHash = import?.Hash;
        row.ImportedBy = import?.ImportedBy.Value;
    }

    private static IncomeSource ToSource(SourceRow row)
        => IncomeWire.TryKind(row.Kind, out var kind)
            ? new IncomeSource(new ItemId(row.ItemId), kind, row.PayerLabel, row.Note, row.EndedOn)
            : throw new InvalidOperationException($"Income source {row.ItemId} has a stored kind this code does not know.");
}
