using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Banking.Application.Reconciliation;

/// <summary>The category key a link gives its line (<see cref="LinkCategory.KeyFor"/>), reading only what the link's target needs.</summary>
public sealed class LinkCategoryReader(IItemRegistry items, ISubscriptionLabels labels)
{
    public async Task<string?> KeyAsync(BankLink link, string? charge, bool credit, CancellationToken ct)
    {
        // The item's kind tells a property from a loan, only when no charge does.
        var kind = charge is null && link.TargetModule == "realestate" && link.TargetItem is { } item ? (await items.FindAsync(item, ct))?.Kind : null;
        IReadOnlyList<string> keys = (charge == ChargeKinds.Subscription || link.TargetModule == "subscriptions") && link.TargetItem is { } subscription
            ? (await labels.LabelKeysAsync([subscription], ct)).GetValueOrDefault(subscription) ?? []
            : [];
        return LinkCategory.KeyFor(link.Outcome, charge, link.TargetModule, kind, credit, keys);
    }
}
