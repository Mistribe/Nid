using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.RealEstate.Infrastructure.Persistence;

/// <summary>
/// #23: Real estate's share of an item delete. A property goes with its tenancies and recurring costs, a loan with its phases, insurances,
/// fees, early repayments and bank table; their valuations and flows are Core facts (Core's part). A loan financing a property blocks it.
/// </summary>
public sealed class RealEstateDeletionPart(RealEstateDbContext db, IItemRegistry items) : IItemDeletionPart
{
    private static bool IsProperty(ItemAccess item) => item is { Module: "realestate", Kind: "property" };

    private static bool IsLoan(ItemAccess item) => item is { Module: "realestate", Kind: "loan" };

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        if (IsProperty(item))
            return
            [
                new("tenancies", await db.Tenancies.CountAsync(t => t.PropertyItemId == id, ct)),
                new("recurring_costs", await db.RecurringCosts.CountAsync(c => c.PropertyItemId == id, ct)),
            ];
        return !IsLoan(item) ? []
        :
        [
            new("loan_phases", await db.Phases.CountAsync(p => p.LoanItemId == id, ct)),
            new("loan_insurances", await db.Insurances.CountAsync(i => i.LoanItemId == id, ct)),
            new("loan_fees", await db.Fees.CountAsync(f => f.LoanItemId == id, ct)),
            new("loan_repayments", await db.Events.CountAsync(e => e.LoanItemId == id, ct)),
            new("bank_schedule_rows", await db.BankSchedule.CountAsync(r => r.LoanItemId == id, ct)),
        ];
    }

    private static bool IsEnvelope(ItemAccess item) => item is { Module: "investments", Kind: "envelope" };

    private static bool IsAsset(ItemAccess item) => item is { Module: "assets", Kind: "asset" };

    /// <summary>The loans that finance the property, (#180) the SCPI envelope or (#188) the asset: deleting them (or editing their link) frees it.</summary>
    public async Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        if (!IsProperty(item) && !IsEnvelope(item) && !IsAsset(item))
            return [];
        var byProperty = IsProperty(item);
        var byAsset = IsAsset(item);
        var loans = await db.Loans.Where(l => byProperty ? l.PropertyItemId == id : byAsset ? l.AssetItemId == id : l.EnvelopeItemId == id).Select(l => l.ItemId).ToListAsync(ct);
        if (loans.Count == 0)
            return [];
        var found = await items.FindManyAsync(loans.Select(l => new ItemId(l)).ToList(), ct);
        return [new(byProperty ? "loan" : byAsset ? "asset_loan" : "scpi_loan", loans.Count, found.Values.Select(l => new DeletionBlockerItem(l.Id.Value, l.DisplayName)).ToList())];
    }

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsProperty(item) ? await db.Properties.AnyAsync(p => p.ItemId == item.Id.Value && p.ImportRef != null, ct)
            : IsLoan(item) && await db.Loans.AnyAsync(l => l.ItemId == item.Id.Value && l.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        // Tracked removes: the audit sees every row. EF orders the tenancies' deletes by their RevisedFrom key, a revision before the one it continues.
        if (IsProperty(item))
        {
            db.Tenancies.RemoveRange(await db.Tenancies.Where(t => t.PropertyItemId == id).ToListAsync(ct));
            db.RecurringCosts.RemoveRange(await db.RecurringCosts.Where(c => c.PropertyItemId == id).ToListAsync(ct));
            db.Properties.RemoveRange(await db.Properties.Where(p => p.ItemId == id).ToListAsync(ct));
        }
        else if (IsLoan(item))
        {
            db.Events.RemoveRange(await db.Events.Where(e => e.LoanItemId == id).ToListAsync(ct));
            db.Fees.RemoveRange(await db.Fees.Where(f => f.LoanItemId == id).ToListAsync(ct));
            db.BankSchedule.RemoveRange(await db.BankSchedule.Where(r => r.LoanItemId == id).ToListAsync(ct));
            db.Insurances.RemoveRange(await db.Insurances.Where(i => i.LoanItemId == id).ToListAsync(ct));
            db.Phases.RemoveRange(await db.Phases.Where(p => p.LoanItemId == id).ToListAsync(ct));
            db.Loans.RemoveRange(await db.Loans.Where(l => l.ItemId == id).ToListAsync(ct));
        }
        else
            return;
        await db.SaveChangesAsync(ct);
    }
}
