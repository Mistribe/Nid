using Nid.Platform.Infrastructure.Audit;

namespace Nid.Tax.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the tax tables: the household's profile and the notices are about no item.</summary>
public sealed class TaxAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(TaxProfileRow)] = AuditRule.NoItem,
        [typeof(TaxAssessmentRow)] = AuditRule.NoItem,
        [typeof(TaxAssessmentDeclarantRow)] = AuditRule.NoItem,
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
