using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class HoldingStore(InvestmentsDbContext db) : IHoldingStore
{
    public async Task<IReadOnlyList<StoredHolding>> HoldingsAsync(ItemId envelope, CancellationToken ct)
    {
        var rows = await db.Holdings.AsNoTracking().Where(h => h.EnvelopeItemId == envelope.Value)
            .OrderBy(h => h.Name).ThenBy(h => h.Id).ToListAsync(ct);
        return rows.Select(r => new StoredHolding(ToHolding(r), r.Version)).ToList();
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
        => await db.HoldingSnapshotLines.AnyAsync(l => l.HoldingId == holding, ct) || await db.HoldingMovements.AnyAsync(m => m.HoldingId == holding, ct)
            || await db.HoldingCostReports.AnyAsync(r => r.HoldingId == holding, ct);

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
        return rows.Select(ToMovement).ToList();
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

    public async Task ReplaceMovementAsync(HoldingMovement movement, CancellationToken ct)
    {
        var row = await db.HoldingMovements.SingleAsync(m => m.Id == movement.Id, ct);
        row.HoldingId = movement.HoldingId;
        row.OccurredOn = movement.OccurredOn;
        row.Kind = movement.Kind;
        row.Units = movement.Units;
        row.UnitPrice = movement.UnitPrice;
        row.Amount = movement.Amount;
        row.EnvelopeFlowId = movement.EnvelopeFlowId;
        row.SwitchRef = movement.SwitchRef;
        row.Label = movement.Label;
        row.ExternalRef = movement.ExternalRef;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ImportedHoldingRow>>> FindImportedAsync(
        HoldingTable table, IReadOnlyCollection<string> refs, CancellationToken ct)
    {
        var wanted = refs.Distinct(StringComparer.Ordinal).ToList();
        var rows = await Marked(table).AsNoTracking().Where(r => r.ImportRef != null && wanted.Contains(r.ImportRef))
            .Select(r => new { r.Id, r.EnvelopeItemId, r.ImportRef, r.ImportHash, r.ImportedBy }).ToListAsync(ct);
        return rows.GroupBy(r => r.ImportRef!, StringComparer.Ordinal).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<ImportedHoldingRow>)g.Select(r => new ImportedHoldingRow(r.Id, new ItemId(r.EnvelopeItemId), r.ImportHash!.Trim(),
                new PersonId(r.ImportedBy!.Value))).ToList(),
            StringComparer.Ordinal);
    }

    public async Task<HoldingsExport> ExportAsync(IReadOnlyCollection<ItemId> envelopes, CancellationToken ct)
    {
        var ids = envelopes.Select(e => e.Value).ToList();
        var holdings = await db.Holdings.AsNoTracking().Where(h => ids.Contains(h.EnvelopeItemId)).ToListAsync(ct);
        var snapshots = await db.HoldingSnapshots.AsNoTracking().Include(s => s.Lines).Where(s => ids.Contains(s.EnvelopeItemId)).ToListAsync(ct);
        var movements = await db.HoldingMovements.AsNoTracking().Where(m => ids.Contains(m.EnvelopeItemId)).ToListAsync(ct);
        var marks = holdings.Cast<IImportMarked>().Concat(snapshots).Concat(movements).Where(r => r.ImportRef != null)
            .ToDictionary(r => r.Id, r => new ImportMark(r.ImportRef!, r.ImportHash!.Trim(), new PersonId(r.ImportedBy!.Value)));
        return new HoldingsExport(
            holdings.Select(ToHolding).ToList(),
            snapshots.Select(r => new HoldingSnapshot(r.Id, new ItemId(r.EnvelopeItemId), r.AsOf, r.SourceLabel, r.Complete,
                r.Lines.Select(l => new SnapshotLine(l.HoldingId, l.Units, l.UnitPrice, l.Value, l.CostBasisPerUnit)).ToList())).ToList(),
            movements.Select(ToMovement).ToList(),
            marks);
    }

    public async Task MarkAsync(HoldingTable table, Guid id, ImportMark mark, CancellationToken ct)
    {
        var row = await Marked(table).SingleAsync(r => r.Id == id, ct);
        row.ImportRef = mark.Ref;
        row.ImportHash = mark.Hash;
        row.ImportedBy = mark.ImportedBy.Value;
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<IImportMarked> Marked(HoldingTable table) => table switch
    {
        HoldingTable.Holding => db.Holdings,
        HoldingTable.Snapshot => db.HoldingSnapshots,
        _ => db.HoldingMovements,
    };

    private static EnvelopeHolding ToHolding(HoldingRow r) => new(r.Id, new ItemId(r.EnvelopeItemId), r.Name, r.Isin, r.Kind, r.AssetClass,
        r.Issuer, r.RiskIndicator, r.OpenedOn, r.MaturesOn, r.EntryCostPct, r.ClosedOn, r.Note);

    private static HoldingMovement ToMovement(HoldingMovementRow r) => new(r.Id, new ItemId(r.EnvelopeItemId), r.HoldingId, r.OccurredOn, r.Kind,
        r.Units, r.UnitPrice, r.Amount, r.EnvelopeFlowId, r.SwitchRef, r.Label, r.ExternalRef);

    public async Task<IReadOnlyList<CostReport>> CostReportsAsync(ItemId envelope, CancellationToken ct)
    {
        var rows = await db.HoldingCostReports.AsNoTracking().Where(r => r.EnvelopeItemId == envelope.Value)
            .OrderBy(r => r.HoldingId).ThenBy(r => r.Year).ToListAsync(ct);
        return rows.Select(r => new CostReport(r.HoldingId, r.Year, r.GrossReturnPct, r.TotalFeesPct, r.NetReturnPct, r.ContractFeesPct, r.SupportFeesPct,
            r.RetrocessionPct, r.NetAnnualized5yPct, r.SourceLabel)).ToList();
    }

    public async Task SaveCostReportAsync(ItemId envelope, CostReport report, CancellationToken ct)
    {
        var row = await db.HoldingCostReports.SingleOrDefaultAsync(r => r.HoldingId == report.HoldingId && r.Year == report.Year, ct);
        if (row is null)
            db.HoldingCostReports.Add(row = new HoldingCostReportRow { HoldingId = report.HoldingId, Year = report.Year, EnvelopeItemId = envelope.Value });
        row.GrossReturnPct = report.GrossReturnPct;
        row.TotalFeesPct = report.TotalFeesPct;
        row.NetReturnPct = report.NetReturnPct;
        row.ContractFeesPct = report.ContractFeesPct;
        row.SupportFeesPct = report.SupportFeesPct;
        row.RetrocessionPct = report.RetrocessionPct;
        row.NetAnnualized5yPct = report.NetAnnualized5yPct;
        row.SourceLabel = report.SourceLabel;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MaturingHolding>> MaturingAsync(IReadOnlyCollection<ItemId> envelopes, LocalDate from, LocalDate until, CancellationToken ct)
    {
        var ids = envelopes.Select(e => e.Value).ToList();
        var rows = await db.Holdings.AsNoTracking()
            .Where(h => ids.Contains(h.EnvelopeItemId) && h.ClosedOn == null && h.MaturesOn >= from && h.MaturesOn <= until)
            .Select(h => new { h.EnvelopeItemId, h.Id, h.Name, h.MaturesOn }).ToListAsync(ct);
        return rows.Select(r => new MaturingHolding(new ItemId(r.EnvelopeItemId), r.Id, r.Name, r.MaturesOn!.Value)).ToList();
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
