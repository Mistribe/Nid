using Nid.Platform.Infrastructure.Audit;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>Workers and keys belong to no household item. A key row is audited without its private key.</summary>
public sealed class AiAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(ModelWorkerRow)] = AuditRule.NoItem,
        [typeof(AiKeyRow)] = AuditRule.NoItem with { Secret = new HashSet<string> { "private_key" } },
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow) };
}
