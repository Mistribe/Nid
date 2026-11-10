using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Goals.Infrastructure.Persistence;

/// <summary>#23: Goals' share of an item delete: the goal, its funding links and its progress entries. Nothing in another module points at a goal.</summary>
public sealed class GoalsDeletionPart(GoalsDbContext db) : IItemDeletionPart
{
    private static bool IsGoal(ItemAccess item) => item.Module == "goals";

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
        => !IsGoal(item) ? []
        :
        [
            new("progress_entries", await db.Entries.CountAsync(e => e.GoalId == item.Id.Value, ct)),
            new("funding", await db.Funding.CountAsync(f => f.GoalId == item.Id.Value, ct)),
        ];

    public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsGoal(item) && await db.Goals.AnyAsync(g => g.ItemId == item.Id.Value && g.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsGoal(item))
            return;
        var id = item.Id.Value;
        // Tracked removes, children first: the audit sees every row the cascade would have taken silently.
        db.Entries.RemoveRange(await db.Entries.Where(e => e.GoalId == id).ToListAsync(ct));
        db.Funding.RemoveRange(await db.Funding.Where(f => f.GoalId == id).ToListAsync(ct));
        db.Goals.RemoveRange(await db.Goals.Where(g => g.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
