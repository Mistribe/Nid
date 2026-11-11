using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>#156: the subscriptions a person uses, and the billing phases and purchases they pay.</summary>
public sealed class SubscriptionsPersonUsePart(SubscriptionsDbContext db) : IPersonUsePart
{
    public async Task<IReadOnlyList<DeletionCount>> UsesAsync(PersonId person, CancellationToken ct)
    {
        var id = person.Value;
        var count = await db.Set<SubscriptionUserRow>().CountAsync(u => u.PersonId == id, ct)
            + await db.Phases.CountAsync(p => p.PayerPersonId == id, ct)
            + await db.Purchases.CountAsync(p => p.PayerPersonId == id, ct);
        return [new("subscriptions", count)];
    }
}
