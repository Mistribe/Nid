using Nid.Platform.Infrastructure.Audit;

namespace Nid.Goals.Infrastructure.Persistence;

/// <summary>
/// A goal row is logged against its own item, a progress row against its goal's. A funding row names an envelope too: it is
/// logged against both items, so a reader who cannot see the envelope never meets the row (as a purchase's grants).
/// </summary>
public sealed class GoalsAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(GoalRow)] = new(e => AuditItems.Of(e, nameof(GoalRow.ItemId))),
        [typeof(FundingRow)] = new(e => AuditItems.Of(e, nameof(FundingRow.GoalId)).Concat(AuditItems.Of(e, nameof(FundingRow.EnvelopeId))).Distinct()),
        [typeof(ProgressEntryRow)] = new(e => AuditItems.Of(e, nameof(ProgressEntryRow.GoalId))),
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
