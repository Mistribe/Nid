using Nid.Core.Contracts;

namespace Nid.Subscriptions.Contracts;

/// <summary>The catalog labels of subscriptions' providers, for the category a bank line linked to one takes.</summary>
public interface ISubscriptionLabels
{
    /// <summary>
    /// Each subscription's provider <c>label_keys</c>, in catalog order; a subscription without a provider is absent. A registry-level
    /// read, no access check: the caller already checked the link.
    /// </summary>
    Task<IReadOnlyDictionary<ItemId, IReadOnlyList<string>>> LabelKeysAsync(IReadOnlyCollection<ItemId> subscriptions, CancellationToken ct);
}
