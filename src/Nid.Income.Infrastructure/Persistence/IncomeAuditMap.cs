using Nid.Platform.Infrastructure.Audit;

namespace Nid.Income.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the Income tables: a phase, or a one-off's taxable amount, is logged against its source's item.</summary>
public sealed class IncomeAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(SourceRow)] = new(e => AuditItems.Of(e, nameof(SourceRow.ItemId))),
        [typeof(PhaseRow)] = new(e => AuditItems.Of(e, nameof(PhaseRow.SourceItemId))),
        [typeof(OneOffTaxableRow)] = new(e => AuditItems.Of(e, nameof(OneOffTaxableRow.SourceId))),
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
