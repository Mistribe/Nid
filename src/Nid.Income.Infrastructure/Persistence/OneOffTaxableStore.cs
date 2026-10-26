using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Income.Contracts;

namespace Nid.Income.Infrastructure.Persistence;

public sealed class OneOffTaxableStore(IncomeDbContext db) : IOneOffTaxableStore
{
    public async Task<IReadOnlyDictionary<Guid, decimal>> ForFlowsAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct)
    {
        if (flowIds.Count == 0)
            return new Dictionary<Guid, decimal>();
        var wanted = flowIds.Distinct().ToList();
        return await db.OneOffTaxables.AsNoTracking().Where(r => wanted.Contains(r.FlowId)).ToDictionaryAsync(r => r.FlowId, r => r.TaxableAmount, ct);
    }

    public async Task SetAsync(Guid flowId, ItemId source, decimal? taxable, CancellationToken ct)
    {
        var row = await db.OneOffTaxables.SingleOrDefaultAsync(r => r.FlowId == flowId, ct);
        if (taxable is not { } amount)
        {
            if (row is null)
                return;
            db.OneOffTaxables.Remove(row);
        }
        else if (row is null)
            db.OneOffTaxables.Add(new OneOffTaxableRow { FlowId = flowId, SourceId = source.Value, TaxableAmount = amount });
        else
            row.TaxableAmount = amount;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveStaleAsync(IReadOnlyCollection<ItemId> sources, IReadOnlyCollection<Guid> currentFlows, CancellationToken ct)
    {
        var ids = sources.Select(s => s.Value).ToList();
        var current = currentFlows.ToList();
        var stale = await db.OneOffTaxables.Where(r => ids.Contains(r.SourceId) && !current.Contains(r.FlowId)).ToListAsync(ct);
        if (stale.Count == 0)
            return;
        db.OneOffTaxables.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }
}
