using Microsoft.EntityFrameworkCore;
using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>#23: Banking's share of an item delete: the transaction links (their lines unlinked) and rules aimed at it, and the account's envelope association.</summary>
public sealed class BankingDeletionPart(BankingDbContext db) : IItemDeletionPart
{
    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        return
        [
            new("bank_links", await db.Links.CountAsync(l => l.TargetItemId == id, ct)),
            new("bank_rules", await db.Rules.CountAsync(r => r.TargetItemId == id, ct)),
            new("bank_account", await db.Accounts.CountAsync(a => a.EnvelopeItemId == id, ct)),
        ];
    }

    public Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionBlocker>>([]);

    public Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct) => Task.FromResult(false);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        var id = item.Id.Value;
        // The lines keep a category a person chose; the one their link gave goes with it. Unlinked and unreconciled, the next pass looks at
        // them again: link rules, charges, then category rules.
        var linked = db.Transactions.Where(t => db.Links.Any(l => l.TransactionId == t.Id && l.TargetItemId == id));
        await CategoryStore.ClearSourceAsync(linked, [CategorySource.Link], ct);
        await linked.ExecuteUpdateAsync(s => s
            .SetProperty(t => t.LinkState, LinkStore.Wire(TransactionLinkState.Unlinked)).SetProperty(t => t.ReconciledAt, (Instant?)null), ct);
        db.Links.RemoveRange(await db.Links.Where(l => l.TargetItemId == id).ToListAsync(ct));
        db.Rules.RemoveRange(await db.Rules.Where(r => r.TargetItemId == id).ToListAsync(ct));
        foreach (var account in await db.Accounts.Where(a => a.EnvelopeItemId == id).ToListAsync(ct))
        {
            account.EnvelopeItemId = null;
            account.AssociatedOn = null;
            account.AssociatedByPersonId = null;
        }
        await db.SaveChangesAsync(ct);
    }
}
