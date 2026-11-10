using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>
/// #23: Core's share of an item delete: its facts, retired rows included (the only physical fact delete), and its pending
/// proposals, superseded rather than deleted (proposals are history), except the one being approved.
/// </summary>
public sealed class CoreDeletionPart(CoreDbContext db, IApprovingProposal approving, INotifier notifier) : IItemDeletionPart
{
    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
        =>
        [
            new("flows", await db.Flows.CountAsync(f => f.ItemId == item.Id.Value, ct)),
            new("valuations", await db.Valuations.CountAsync(v => v.ItemId == item.Id.Value, ct)),
        ];

    public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);

    public Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct) => Task.FromResult(false);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        db.Flows.RemoveRange(await db.Flows.Where(f => f.ItemId == id).ToListAsync(ct));
        db.Valuations.RemoveRange(await db.Valuations.Where(v => v.ItemId == id).ToListAsync(ct));
        var kept = approving.Id;
        var pending = await db.Proposals.Where(p => p.TargetItemId == id && p.Status == "pending" && p.Id != kept).ToListAsync(ct);
        foreach (var proposal in pending)
            proposal.Status = "superseded";
        await db.SaveChangesAsync(ct);

        foreach (var proposal in pending)
            await notifier.ResolveAsync($"proposal_pending:{proposal.Id}", ct); // ProposalAccess.NoticeKey, internal to Core.Application
    }
}
