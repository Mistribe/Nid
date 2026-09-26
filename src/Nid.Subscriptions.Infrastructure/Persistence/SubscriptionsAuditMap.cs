using Nid.Platform.Infrastructure.Audit;

namespace Nid.Subscriptions.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the Subscriptions tables.</summary>
public sealed class SubscriptionsAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(SubscriptionRow)] = new(e => AuditItems.Of(e, nameof(SubscriptionRow.ItemId))),
        [typeof(SubscriptionUserRow)] = new(e => AuditItems.Of(e, nameof(SubscriptionUserRow.ItemId))),
        [typeof(BillingPhaseRow)] = new(e => AuditItems.Of(e, nameof(BillingPhaseRow.ItemId))),
        [typeof(GrantRow)] = new(e => AuditItems.Of(e, nameof(GrantRow.SubscriptionItemId))),
        // A purchase has no item of its own: it belongs to every subscription its grants reach. The store always
        // loads the grants with the purchase it changes (DeletePurchaseAsync includes them).
        [typeof(PurchaseRow)] = new(e => ((PurchaseRow)e.Entity).Grants.Select(g => g.SubscriptionItemId)),
        [typeof(ProviderRow)] = AuditRule.NoItem,
        [typeof(LabelRow)] = AuditRule.NoItem,
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(CatalogSyncRunRow), typeof(AuditEntryRow) };
}
