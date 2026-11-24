using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Goals.Domain;
using static Nid.Goals.Domain.GoalDeletion;

namespace Nid.Goals.Infrastructure.Persistence;

/// <summary>#23: Goals' share of an item delete, by <see cref="GoalDeletion"/>'s rule.</summary>
public sealed class GoalsDeletionPart(GoalsDbContext db, IItemRegistry items) : IItemDeletionPart
{
    /// <summary>The goals the envelope funds.</summary>
    private async Task<IReadOnlyList<ItemAccess>> FundedAsync(ItemAccess envelope, CancellationToken ct)
    {
        var goals = await db.Funding.Where(f => f.EnvelopeId == envelope.Id.Value).Select(f => f.GoalId).ToListAsync(ct);
        return goals.Count == 0 ? [] : (await items.FindManyAsync(goals.Select(g => new ItemId(g)).ToList(), ct)).Values.ToList();
    }

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
    {
        if (IsEnvelope(item))
            // (goal, envelope) is the funding key: one line per goal, so goals counted are lines counted.
            return [new("goal_funding_closed", Released(await FundedAsync(item, ct)).Count)];
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
        var open = Blocking(await FundedAsync(item, ct));
        return open.Count == 0 ? [] : [new("goal_funding", open.Count, open.Select(g => new DeletionBlockerItem(g.Id.Value, g.DisplayName)).ToList())];
    }

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsGoal(item) && await db.Goals.AnyAsync(g => g.ItemId == item.Id.Value && g.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        if (IsEnvelope(item))
        {
            // Only closed goals' lines: a line an open goal added since the blocker check stays (and blocks the next attempt).
            var closed = Released(await FundedAsync(item, ct)).Select(g => g.Id.Value).ToList();
            db.Funding.RemoveRange(await db.Funding.Where(f => f.EnvelopeId == id && closed.Contains(f.GoalId)).ToListAsync(ct));
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
