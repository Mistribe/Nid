using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Assets.Infrastructure.Persistence;

/// <summary>An asset has no dependants: deleting its item removes its row.</summary>
public sealed class AssetsDeletionPart(AssetsDbContext db) : IItemDeletionPart
{
    private static bool IsAsset(ItemAccess item) => item.Module == "assets" && item.Kind == "asset";

    public Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionCount>>([]);

    public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsAsset(item) && await db.Assets.AnyAsync(a => a.ItemId == item.Id.Value && a.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsAsset(item))
            return;
        // Tracked remove: the audit sees the row.
        db.Assets.RemoveRange(await db.Assets.Where(a => a.ItemId == item.Id.Value).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
