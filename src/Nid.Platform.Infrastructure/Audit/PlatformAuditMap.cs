using Nid.Platform.Infrastructure.Persistence;

namespace Nid.Platform.Infrastructure.Audit;

/// <summary>Platform's own tables: staging is transient and the log does not audit itself.</summary>
public sealed class PlatformAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>();

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(ImportSessionRow), typeof(ImportBatchRow), typeof(AuditEntryRow), typeof(NotificationRow), typeof(NotificationRunRow) };
}
