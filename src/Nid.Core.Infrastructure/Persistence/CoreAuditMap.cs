using Nid.Platform.Infrastructure.Audit;

namespace Nid.Core.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the Core tables.</summary>
public sealed class CoreAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(ItemRow)] = new(e => AuditItems.Of(e, nameof(ItemRow.Id))),
        [typeof(OwnershipRow)] = new(e => AuditItems.Of(e, nameof(OwnershipRow.ItemId))),
        [typeof(PersonRow)] = AuditRule.NoItem,
        [typeof(HouseholdSettingsRow)] = AuditRule.NoItem,
    };

    // fx_rate: one bulk upsert the interceptor never sees, and collections have their journal (U8).
    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(FxRateRow), typeof(FxSyncRunRow), typeof(AuditEntryRow) };
}
