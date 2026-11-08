using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Application.Catalog;

/// <summary>
/// The providers of subscriptions a read already holds (design §5 "Views"): their keys, then one provider query and
/// one label query, whatever the number of subscriptions, and one more when a bundle is among them. No visibility filter: the subscription is visible, so is
/// its provider (§3.5).
/// </summary>
internal static class ProviderLookup
{
    public static async Task<IReadOnlyDictionary<ItemId, ProviderSummaryView>> ForAsync(
        ISubscriptionStore subscriptions, IProviderStore providers, IReadOnlyCollection<ItemId> ids, CancellationToken ct)
    {
        var keys = await subscriptions.ProviderKeysAsync(ids, ct);
        if (keys.Count == 0)
            return new Dictionary<ItemId, ProviderSummaryView>();

        var found = await providers.FindAsync(keys.Values.Distinct(StringComparer.Ordinal).ToList(), ct);
        var labels = await ProviderViews.LabelsAsync(providers, ct);
        var included = await ProviderViews.IncludedAsync(providers, found.Values, ct);
        return keys
            .Where(k => found.ContainsKey(k.Value))
            .ToDictionary(k => k.Key, k => ProviderSummaryView.From(found[k.Value], labels, included));
    }
}
