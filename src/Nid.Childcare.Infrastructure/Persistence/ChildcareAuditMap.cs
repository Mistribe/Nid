using Nid.Platform.Infrastructure.Audit;

namespace Nid.Childcare.Infrastructure.Persistence;

/// <summary>Every childcare row is logged against its arrangement's item.</summary>
public sealed class ChildcareAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(ArrangementRow)] = new(e => AuditItems.Of(e, nameof(ArrangementRow.ItemId))),
        [typeof(TermsRow)] = new(e => AuditItems.Of(e, nameof(TermsRow.ItemId))),
        [typeof(WeekdayRow)] = new(e => AuditItems.Of(e, nameof(WeekdayRow.ItemId))),
        [typeof(GapRow)] = new(e => AuditItems.Of(e, nameof(GapRow.ItemId))),
        [typeof(StatementRow)] = new(e => AuditItems.Of(e, nameof(StatementRow.ItemId))),
        [typeof(StatementDayRow)] = new(e => AuditItems.Of(e, nameof(StatementDayRow.ItemId))),
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
