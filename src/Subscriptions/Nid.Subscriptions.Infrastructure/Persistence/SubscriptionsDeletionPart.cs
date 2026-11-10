using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>
/// #23: Subscriptions' share of an item delete. A subscription goes with its phases, users, insurance (and guarantees) and the grants that
/// cover it; the purchases stay (they may cover other subscriptions), their facts are Core's. An insurance covering a property blocks its delete.
/// </summary>
public sealed class SubscriptionsDeletionPart(SubscriptionsDbContext db, IItemRegistry items) : IItemDeletionPart
{
    private static bool IsProperty(ItemAccess item) => item is { Module: "realestate", Kind: "property" };

    private static bool IsSubscription(ItemAccess item) => item is { Module: "subscriptions", Kind: "subscription" };

    /// <summary>The purchases whose grants all target the subscription.</summary>
    private IQueryable<PurchaseRow> OwnPurchases(Guid id)
        => db.Purchases.Where(p => p.Grants.Any(g => g.SubscriptionItemId == id) && p.Grants.All(g => g.SubscriptionItemId == id));

    public async Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsSubscription(item))
            return [];
        var id = item.Id.Value;
        return
        [
            new("billing_phases", await db.Phases.CountAsync(p => p.ItemId == id, ct)),
            new("coverage_grants", await db.Grants.CountAsync(g => g.SubscriptionItemId == id, ct)),
            new("purchases", await OwnPurchases(id).CountAsync(ct)),
            new("subscription_insurance", await db.Insurances.CountAsync(i => i.ItemId == id, ct)),
            new("subscription_users", await db.Set<SubscriptionUserRow>().CountAsync(u => u.ItemId == id, ct)),
        ];
    }

    /// <summary>The subscriptions whose insurance covers the property: detaching the insurance frees it.</summary>
    public async Task<IReadOnlyList<DeletionBlocker>> BlockersAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsProperty(item))
            return [];
        var subscriptions = await db.Insurances.Where(i => i.PropertyId == item.Id.Value).Select(i => i.ItemId).ToListAsync(ct);
        if (subscriptions.Count == 0)
            return [];
        var found = await items.FindManyAsync(subscriptions.Select(s => new ItemId(s)).ToList(), ct);
        return [new("insurance", subscriptions.Count, found.Values.Select(s => new DeletionBlockerItem(s.Id.Value, s.DisplayName)).ToList())];
    }

    public async Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct)
        => IsSubscription(item) && await db.Subscriptions.AnyAsync(s => s.ItemId == item.Id.Value && s.ImportRef != null, ct);

    public async Task RemoveAsync(ItemAccess item, CancellationToken ct)
    {
        if (!IsSubscription(item))
            return;
        var id = item.Id.Value;
        // Tracked removes: the audit sees every row. Grants have no foreign key to the subscription; the rest would cascade, but is removed explicitly.
        db.Purchases.RemoveRange(await OwnPurchases(id).ToListAsync(ct)); // their grants cascade; a shared purchase keeps its other grants
        db.Grants.RemoveRange(await db.Grants.Where(g => g.SubscriptionItemId == id).ToListAsync(ct));
        db.InsuranceGuarantees.RemoveRange(await db.InsuranceGuarantees.Where(g => g.ItemId == id).ToListAsync(ct));
        db.Insurances.RemoveRange(await db.Insurances.Where(i => i.ItemId == id).ToListAsync(ct));
        db.Set<SubscriptionUserRow>().RemoveRange(await db.Set<SubscriptionUserRow>().Where(u => u.ItemId == id).ToListAsync(ct));
        db.Phases.RemoveRange(await db.Phases.Where(p => p.ItemId == id).ToListAsync(ct));
        db.Subscriptions.RemoveRange(await db.Subscriptions.Where(s => s.ItemId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
