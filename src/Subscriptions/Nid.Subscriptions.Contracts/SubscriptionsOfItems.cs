using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Subscriptions.Contracts;

/// <summary>A subscription linked to an item, as the item's page lists it. <paramref name="Period"/>: day, week, month or year when the current phase bills each one, else null.</summary>
public sealed record LinkedSubscription(ItemId Id, string Name, Money? Price, string? Period, LocalDate? NextDeadline);

/// <summary>
/// #648: the subscriptions linked to items of another module (an asset). Only the ones <paramref name="reader"/> sees
/// in <paramref name="scope"/>; a subscription they cannot see is not named. One call whatever the number of items.
/// </summary>
public interface ISubscriptionsOfItems
{
    Task<IReadOnlyDictionary<ItemId, IReadOnlyList<LinkedSubscription>>> ForAsync(
        PersonId reader, VisibilityScope scope, IReadOnlyCollection<ItemId> items, CancellationToken ct);
}
