using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Documents.Contracts;

namespace Nid.Documents.Infrastructure.Persistence;

/// <summary>
/// #23: Documents' share of an item delete: the links to the item (or anchored on it through a sub-entry), their « pas disponible »
/// and unused upload tickets. The documents stay: they may be linked elsewhere, and an unlinked one stays in the list.
/// </summary>
public sealed class DocumentsDeletionPart(DocumentsDbContext db) : IItemDeletionPart
{
    private IQueryable<DocumentLinkRow> Links(Guid id)
        => db.Links.Where(l => (l.TargetKind == TargetKinds.CoreItem && l.TargetId == id) || l.AnchorItemId == id);

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
        => [new("documents", await Links(item.Id.Value).Select(l => l.DocumentId).Distinct().CountAsync(ct))];

    public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);

    public Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct) => Task.FromResult(false);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        db.Links.RemoveRange(await Links(id).ToListAsync(ct));
        db.ExpectedDismissals.RemoveRange(await db.ExpectedDismissals.Where(d => d.TargetKind == TargetKinds.CoreItem && d.TargetId == id).ToListAsync(ct));
        db.UploadTickets.RemoveRange(await db.UploadTickets.Where(t => t.TargetKind == TargetKinds.CoreItem && t.TargetId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
