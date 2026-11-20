using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class SplitStore(BankingDbContext db) : ISplitStore
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TransactionPart>>> PartsAsync(IReadOnlyCollection<Guid> lines, CancellationToken ct)
        => (await db.TransactionParts.AsNoTracking().Where(p => lines.Contains(p.TransactionId)).OrderBy(p => p.Position).ToListAsync(ct))
            .GroupBy(p => p.TransactionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TransactionPart>)[.. g.Select(p => new TransactionPart(p.Id, p.TransactionId, p.Amount, p.CategoryId, p.Note))]);

    public async Task SplitAsync(Guid line, IReadOnlyList<PartInput> parts, string by, Instant at, CancellationToken ct)
    {
        await using var scope = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var row = await db.Transactions.SingleAsync(t => t.Id == line, ct);
        db.TransactionParts.RemoveRange(await db.TransactionParts.Where(p => p.TransactionId == line).ToListAsync(ct));
        // The old rows go first: (transaction, position) is unique.
        await db.SaveChangesAsync(ct);
        db.TransactionParts.AddRange(parts.Select((p, i) => new TransactionPartRow
        {
            Id = Guid.CreateVersion7(), TransactionId = line, Amount = p.Amount, CategoryId = p.CategoryId, Note = Clean(p.Note), Position = i,
        }));
        (row.CategoryId, row.CategorySource, row.CategoryRuleId, row.CategorisedAt, row.CategorisedBy) = (null, CategoryStore.Wire(CategorySource.Split), null, at, by);
        await db.SaveChangesAsync(ct);
        if (scope is not null)
            await scope.CommitAsync(ct);
    }

    public async Task<bool> UnsplitAsync(Guid line, CancellationToken ct)
    {
        await using var scope = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var row = await db.Transactions.SingleAsync(t => t.Id == line, ct);
        var parts = await db.TransactionParts.Where(p => p.TransactionId == line).ToListAsync(ct);
        if (parts.Count == 0 && row.CategorySource != CategoryStore.Wire(CategorySource.Split))
            return false;
        db.TransactionParts.RemoveRange(parts);
        (row.CategoryId, row.CategorySource, row.CategoryRuleId, row.CategorisedAt, row.CategorisedBy) = (null, null, null, null, null);
        await db.SaveChangesAsync(ct);
        if (scope is not null)
            await scope.CommitAsync(ct);
        return true;
    }

    public async Task SetNoteAsync(Guid line, string? note, CancellationToken ct)
    {
        (await db.Transactions.SingleAsync(t => t.Id == line, ct)).Note = Clean(note);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Trimmed; empty is none.</summary>
    internal static string? Clean(string? note) => note?.Trim() is { Length: > 0 } t ? t : null;
}
