using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Infrastructure.Persistence;

public sealed class SubscriptionLabels(SubscriptionsDbContext db) : ISubscriptionLabels
{
    public async Task<IReadOnlyDictionary<ItemId, IReadOnlyList<string>>> LabelKeysAsync(IReadOnlyCollection<ItemId> subscriptions, CancellationToken ct)
    {
        var ids = subscriptions.Select(s => s.Value).ToList();
        var rows = await db.Subscriptions.AsNoTracking().Where(s => ids.Contains(s.ItemId))
            .Join(db.Providers, s => s.ProviderKey, p => p.Key, (s, p) => new { s.ItemId, p.LabelKeys })
            .ToListAsync(ct);
        return rows.ToDictionary(r => new ItemId(r.ItemId), r => (IReadOnlyList<string>)r.LabelKeys);
    }
}
