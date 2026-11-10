using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;

namespace Nid.Investments.Infrastructure.Persistence;

/// <summary>
/// #23: Investments' share of an item delete: the envelope, its holdings with their movements, cost reports and snapshot lines, and its
/// snapshots. Its flows and valuations are Core facts: Core's part counts and removes them. An active transfer to another envelope blocks.
/// </summary>
public sealed class InvestmentsDeletionPart(InvestmentsDbContext db, IFlows flows, IItemRegistry items) : IItemDeletionPart
{
    private static bool IsEnvelope(ItemAccess item) => item is { Module: "investments", Kind: "envelope" };

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
        => !IsEnvelope(item) ? []
        :
        [
            new("holdings", await db.Holdings.CountAsync(h => h.EnvelopeItemId == item.Id.Value, ct)),
            new("holding_snapshots", await db.HoldingSnapshots.CountAsync(s => s.EnvelopeItemId == item.Id.Value, ct)),
        ];

    /// <summary>The envelope's active transfer legs whose other leg is still active on another envelope: voiding either leg frees it.</summary>
    public async Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsEnvelope(item))
            return [];
        // Read without an access check: a blocker holds whoever asks (Core's own tests preview as the system).
        var others = (await flows.CounterpartsAsync(item.Id, ct)).Where(f => f.Category == FlowCategories.Transfer).ToList();
        if (others.Count == 0)
            return [];
        var envelopes = await items.FindManyAsync(others.Select(f => f.Item).Distinct().ToList(), ct);
        return
        [
            new("transfers", others.Select(f => f.ExternalRef).Distinct().Count(),
                envelopes.Values.Select(e => new DeletionBlockerItem(e.Id.Value, e.DisplayName)).ToList()),
        ];
    }

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsEnvelope(item) && await db.Envelopes.AnyAsync(e => e.ItemId == item.Id.Value && e.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsEnvelope(item))
            return;
        var id = item.Id.Value;
        var holdings = await db.Holdings.Where(h => h.EnvelopeItemId == id).ToListAsync(ct);
        var holdingIds = holdings.Select(h => h.Id).ToList();
        var snapshots = await db.HoldingSnapshots.Where(s => s.EnvelopeItemId == id).ToListAsync(ct);
        var snapshotIds = snapshots.Select(s => s.Id).ToList();

        // Tracked removes, children first: the audit sees every row the cascade would have taken silently, and the holding FKs restrict.
        db.HoldingSnapshotLines.RemoveRange(await db.HoldingSnapshotLines
            .Where(l => snapshotIds.Contains(l.SnapshotId) || holdingIds.Contains(l.HoldingId)).ToListAsync(ct));
        db.HoldingSnapshots.RemoveRange(snapshots);
        db.HoldingMovements.RemoveRange(await db.HoldingMovements.Where(m => m.EnvelopeItemId == id || holdingIds.Contains(m.HoldingId)).ToListAsync(ct));
        db.HoldingCostReports.RemoveRange(await db.HoldingCostReports.Where(r => r.EnvelopeItemId == id || holdingIds.Contains(r.HoldingId)).ToListAsync(ct));
        db.Holdings.RemoveRange(holdings);
        db.Envelopes.RemoveRange(await db.Envelopes.Where(e => e.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
