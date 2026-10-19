using Nid.Platform.Infrastructure.Audit;

namespace Nid.Investments.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the Investments tables.</summary>
public sealed class InvestmentsAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(EnvelopeRow)] = new(e => AuditItems.Of(e, nameof(EnvelopeRow.ItemId))),
        [typeof(HoldingRow)] = new(e => AuditItems.Of(e, nameof(HoldingRow.EnvelopeItemId))),
        [typeof(HoldingSnapshotRow)] = new(e => AuditItems.Of(e, nameof(HoldingSnapshotRow.EnvelopeItemId))),
        [typeof(HoldingSnapshotLineRow)] = new(e => AuditItems.Of(e, nameof(HoldingSnapshotLineRow.EnvelopeItemId))),
        [typeof(HoldingMovementRow)] = new(e => AuditItems.Of(e, nameof(HoldingMovementRow.EnvelopeItemId))),
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
