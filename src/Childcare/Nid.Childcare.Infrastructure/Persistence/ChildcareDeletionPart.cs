using Microsoft.EntityFrameworkCore;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;

namespace Nid.Childcare.Infrastructure.Persistence;

/// <summary>
/// #23: Childcare's share of an item delete: the arrangement with its terms, week types, weekdays, exceptions, gaps and statements. A
/// validated month blocks (it feeds the expected Pajemploi debits and the tax credit): reopening the months frees it.
/// </summary>
public sealed class ChildcareDeletionPart(ChildcareDbContext db) : IItemDeletionPart
{
    private static bool IsArrangement(ItemAccess item) => item is { Module: ChildcareModule.Key, Kind: ChildcareKinds.Childminder };

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
        => !IsArrangement(item) ? []
        :
        [
            new("statements", await db.Statements.CountAsync(s => s.ItemId == item.Id.Value, ct)),
            new("gaps", await db.Gaps.CountAsync(g => g.ItemId == item.Id.Value, ct)),
            new("week_overrides", await db.WeekOverrides.CountAsync(o => o.ItemId == item.Id.Value, ct)),
        ];

    /// <summary>The validated months, counted only: they are months, not items.</summary>
    public async Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsArrangement(item))
            return [];
        var validated = await db.Statements.CountAsync(s => s.ItemId == item.Id.Value && s.Validated, ct);
        return validated == 0 ? [] : [new("validated_statements", validated, [])];
    }

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsArrangement(item) && await db.Arrangements.AnyAsync(a => a.ItemId == item.Id.Value && a.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsArrangement(item))
            return;
        var id = item.Id.Value;
        // Tracked removes, children first: the audit sees every row the cascade would have taken silently.
        db.StatementDays.RemoveRange(await db.StatementDays.Where(d => d.ItemId == id).ToListAsync(ct));
        db.Statements.RemoveRange(await db.Statements.Where(s => s.ItemId == id).ToListAsync(ct));
        db.Gaps.RemoveRange(await db.Gaps.Where(g => g.ItemId == id).ToListAsync(ct));
        db.WeekOverrides.RemoveRange(await db.WeekOverrides.Where(o => o.ItemId == id).ToListAsync(ct));
        db.Weekdays.RemoveRange(await db.Weekdays.Where(w => w.ItemId == id).ToListAsync(ct));
        db.WeekTypes.RemoveRange(await db.WeekTypes.Where(w => w.ItemId == id).ToListAsync(ct));
        db.Terms.RemoveRange(await db.Terms.Where(t => t.ItemId == id).ToListAsync(ct));
        db.Arrangements.RemoveRange(await db.Arrangements.Where(a => a.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
