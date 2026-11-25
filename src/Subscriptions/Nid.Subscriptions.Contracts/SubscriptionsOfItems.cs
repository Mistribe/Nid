using Nid.Core.Contracts;
using NodaTime;
using Duration = Nid.Core.Contracts.Duration;

namespace Nid.Subscriptions.Contracts;

/// <summary>A subscription linked to an item, as the item's page lists it. <paramref name="Every"/> is the billing period of the phase that bills the next charge.</summary>
public sealed record LinkedSubscription(ItemId Id, string Name, Money? Price, Duration? Every, LocalDate? NextDeadline);

/// <summary>
/// #648: the subscriptions linked to items of another module (an asset). Only the ones <paramref name="reader"/> sees
/// in <paramref name="scope"/>; a subscription they cannot see is not named. One call whatever the number of items.
/// </summary>
public interface ISubscriptionsOfItems
{
    Task<IReadOnlyDictionary<ItemId, IReadOnlyList<LinkedSubscription>>> ForAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyCollection<ItemId> items, CancellationToken ct);
}
