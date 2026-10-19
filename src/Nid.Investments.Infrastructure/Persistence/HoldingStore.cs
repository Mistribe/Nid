using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class HoldingStore(InvestmentsDbContext db) : IHoldingStore
{
    public async Task<IReadOnlyList<StoredHolding>> HoldingsAsync(ItemId envelope, CancellationToken ct)
    {
        var rows = await db.Holdings.AsNoTracking().Where(h => h.EnvelopeItemId == envelope.Value)
            .OrderBy(h => h.Name).ThenBy(h => h.Id).ToListAsync(ct);
        return rows.Select(r => new StoredHolding(new EnvelopeHolding(r.Id, new ItemId(r.EnvelopeItemId), r.Name, r.Isin, r.Kind, r.AssetClass,
            r.Issuer, r.RiskIndicator, r.OpenedOn, r.MaturesOn, r.EntryCostPct, r.ClosedOn, r.Note), r.Version)).ToList();
    }

    public async Task AddAsync(EnvelopeHolding holding, CancellationToken ct)
    {
        var row = new HoldingRow { Id = holding.Id, EnvelopeItemId = holding.Envelope.Value };
        Copy(holding, row);
        db.Holdings.Add(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReplaceAsync(EnvelopeHolding holding, CancellationToken ct)
    {
        Copy(holding, await db.Holdings.SingleAsync(h => h.Id == holding.Id, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid holding, CancellationToken ct)
    {
        db.Holdings.Remove(await db.Holdings.SingleAsync(h => h.Id == holding, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> InUseAsync(Guid holding, CancellationToken ct)
        => await db.HoldingSnapshotLines.AnyAsync(l => l.HoldingId == holding, ct) || await db.HoldingMovements.AnyAsync(m => m.HoldingId == holding, ct);

    public async Task<IReadOnlyList<HoldingSnapshot>> SnapshotsAsync(ItemId envelope, CancellationToken ct)
    {
        var rows = await db.HoldingSnapshots.AsNoTracking().Include(s => s.Lines).Where(s => s.EnvelopeItemId == envelope.Value)
            .OrderByDescending(s => s.AsOf).ToListAsync(ct);
        return rows.Select(r => new HoldingSnapshot(r.Id, envelope, r.AsOf, r.SourceLabel, r.Complete,
            r.Lines.OrderBy(l => l.HoldingId).Select(l => new SnapshotLine(l.HoldingId, l.Units, l.UnitPrice, l.Value, l.CostBasisPerUnit)).ToList())).ToList();
    }

    public async Task SaveSnapshotAsync(HoldingSnapshot snapshot, CancellationToken ct)
    {
        var row = await db.HoldingSnapshots.Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == snapshot.Id, ct);
        if (row is null)
        {
            row = new HoldingSnapshotRow { Id = snapshot.Id, EnvelopeItemId = snapshot.Envelope.Value };
            db.HoldingSnapshots.Add(row);
        }
        row.AsOf = snapshot.AsOf;
        row.SourceLabel = snapshot.SourceLabel;
        row.Complete = snapshot.Complete;

        // Lines kept in place by holding: removing and re-adding the same key in one save is refused by the change tracker.
        var wanted = snapshot.Lines.ToDictionary(l => l.HoldingId);
        foreach (var gone in row.Lines.Where(l => !wanted.ContainsKey(l.HoldingId)).ToList())
            row.Lines.Remove(gone);
        foreach (var line in snapshot.Lines)
        {
            var target = row.Lines.SingleOrDefault(l => l.HoldingId == line.HoldingId);
            if (target is null)
                row.Lines.Add(target = new HoldingSnapshotLineRow { SnapshotId = snapshot.Id, HoldingId = line.HoldingId, EnvelopeItemId = snapshot.Envelope.Value });
            target.Units = line.Units;
            target.UnitPrice = line.UnitPrice;
            target.Value = line.Value;
            target.CostBasisPerUnit = line.CostBasisPerUnit;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSnapshotAsync(Guid snapshot, CancellationToken ct)
    {
        db.HoldingSnapshots.Remove(await db.HoldingSnapshots.Include(s => s.Lines).SingleAsync(s => s.Id == snapshot, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<HoldingMovement>> MovementsAsync(ItemId envelope, CancellationToken ct)
    {
        var rows = await db.HoldingMovements.AsNoTracking().Where(m => m.EnvelopeItemId == envelope.Value)
            .OrderBy(m => m.OccurredOn).ThenBy(m => m.Id).ToListAsync(ct);
        return rows.Select(r => new HoldingMovement(r.Id, envelope, r.HoldingId, r.OccurredOn, r.Kind, r.Units, r.UnitPrice, r.Amount,
            r.EnvelopeFlowId, r.SwitchRef, r.Label, r.ExternalRef)).ToList();
    }

    public async Task AddMovementsAsync(IReadOnlyList<HoldingMovement> movements, CancellationToken ct)
    {
        db.HoldingMovements.AddRange(movements.Select(m => new HoldingMovementRow
        {
            Id = m.Id, HoldingId = m.HoldingId, EnvelopeItemId = m.Envelope.Value, OccurredOn = m.OccurredOn, Kind = m.Kind, Units = m.Units,
            UnitPrice = m.UnitPrice, Amount = m.Amount, EnvelopeFlowId = m.EnvelopeFlowId, SwitchRef = m.SwitchRef, Label = m.Label,
            ExternalRef = m.ExternalRef,
        }));
        await db.SaveChangesAsync(ct);
    }

    private static void Copy(EnvelopeHolding from, HoldingRow to)
    {
        to.Name = from.Name;
        to.Isin = from.Isin;
        to.Kind = from.Kind;
        to.AssetClass = from.AssetClass;
        to.Issuer = from.Issuer;
        to.RiskIndicator = from.RiskIndicator;
        to.OpenedOn = from.OpenedOn;
        to.MaturesOn = from.MaturesOn;
        to.EntryCostPct = from.EntryCostPct;
        to.ClosedOn = from.ClosedOn;
        to.Note = from.Note;
    }
}
