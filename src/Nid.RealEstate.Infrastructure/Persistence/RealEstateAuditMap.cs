using Nid.Platform.Infrastructure.Audit;

namespace Nid.RealEstate.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the module's tables (specs 3A2 §2, 3C1 §2, 3C2 §2): every row is logged against its loan's or property's item.</summary>
public sealed class RealEstateAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(LoanRow)] = new(e => AuditItems.Of(e, nameof(LoanRow.ItemId))),
        [typeof(LoanPhaseRow)] = new(e => AuditItems.Of(e, nameof(LoanPhaseRow.LoanItemId))),
        [typeof(LoanInsuranceRow)] = new(e => AuditItems.Of(e, nameof(LoanInsuranceRow.LoanItemId))),
        [typeof(LoanFeeRow)] = new(e => AuditItems.Of(e, nameof(LoanFeeRow.LoanItemId))),
        [typeof(LoanEventRow)] = new(e => AuditItems.Of(e, nameof(LoanEventRow.LoanItemId))),
        [typeof(PropertyRow)] = new(e => AuditItems.Of(e, nameof(PropertyRow.ItemId))),
        [typeof(PropertyRecurringCostRow)] = new(e => AuditItems.Of(e, nameof(PropertyRecurringCostRow.PropertyItemId))),
        [typeof(PropertyTenancyRow)] = new(e => AuditItems.Of(e, nameof(PropertyTenancyRow.PropertyItemId))),
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
