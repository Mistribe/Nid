using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>#23: Subscriptions' share of an item delete: a subscription insurance that covers a property blocks the property's delete.</summary>
public sealed class SubscriptionsDeletionPart(SubscriptionsDbContext db, IItemRegistry items) : IItemDeletionPart
{
    private static bool IsProperty(ItemAccess item) => item is { Module: "realestate", Kind: "property" };

    public Task<IReadOnlyList<DeletionCount>> CountAsync(ItemAccess item, CancellationToken ct) => Task.FromResult<IReadOnlyList<DeletionCount>>([]);

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

    public Task<bool> ImportedAsync(ItemAccess item, CancellationToken ct) => Task.FromResult(false);

    public Task RemoveAsync(ItemAccess item, CancellationToken ct) => Task.CompletedTask;
}
