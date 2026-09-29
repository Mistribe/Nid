using Nid.Platform.Infrastructure.Audit;

namespace Nid.Investments.Infrastructure.Persistence;

/// <summary>1F3a spec §4.4 for the Investments tables.</summary>
public sealed class InvestmentsAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(EnvelopeRow)] = new(e => AuditItems.Of(e, nameof(EnvelopeRow.ItemId))),
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
