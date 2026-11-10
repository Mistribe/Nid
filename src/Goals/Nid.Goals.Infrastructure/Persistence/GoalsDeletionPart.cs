using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Goals.Infrastructure.Persistence;

/// <summary>
/// #23: Goals' share of an item delete: the goal, its funding links and its progress entries; for an envelope, the open goals it funds
/// block (edit their funding), the closed ones' funding lines go with it (a closed goal cannot be edited).
/// </summary>
public sealed class GoalsDeletionPart(GoalsDbContext db, IItemRegistry items) : IItemDeletionPart
{
    private static bool IsGoal(ItemAccess item) => item.Module == "goals";

    private static bool IsEnvelope(ItemAccess item) => item is { Module: "investments", Kind: "envelope" };

    /// <summary>The goals the envelope funds.</summary>
    private async Task<IReadOnlyList<ItemAccess>> FundedAsync(ItemAccess envelope, CancellationToken ct)
    {
        var goals = await db.Funding.Where(f => f.EnvelopeId == envelope.Id.Value).Select(f => f.GoalId).ToListAsync(ct);
        return goals.Count == 0 ? [] : (await items.FindManyAsync(goals.Select(g => new ItemId(g)).ToList(), ct)).Values.ToList();
    }

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
    {
        if (IsEnvelope(item))
            return [new("goal_funding_closed", (await FundedAsync(item, ct)).Count(g => g.ArchivedAt is not null))];
        return !IsGoal(item) ? []
        :
        [
            new("progress_entries", await db.Entries.CountAsync(e => e.GoalId == item.Id.Value, ct)),
            new("funding", await db.Funding.CountAsync(f => f.GoalId == item.Id.Value, ct)),
        ];
    }

    public async Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsEnvelope(item))
            return [];
        var open = (await FundedAsync(item, ct)).Where(g => g.ArchivedAt is null).ToList();
        return open.Count == 0 ? [] : [new("goal_funding", open.Count, open.Select(g => new DeletionBlockerItem(g.Id.Value, g.DisplayName)).ToList())];
    }

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsGoal(item) && await db.Goals.AnyAsync(g => g.ItemId == item.Id.Value && g.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        if (IsEnvelope(item))
        {
            // Only closed goals' lines are left: an open goal's blocked the delete.
            db.Funding.RemoveRange(await db.Funding.Where(f => f.EnvelopeId == id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            return;
        }
        if (!IsGoal(item))
            return;
        // Tracked removes, children first: the audit sees every row the cascade would have taken silently.
        db.Entries.RemoveRange(await db.Entries.Where(e => e.GoalId == id).ToListAsync(ct));
        db.Funding.RemoveRange(await db.Funding.Where(f => f.GoalId == id).ToListAsync(ct));
        db.Goals.RemoveRange(await db.Goals.Where(g => g.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
