using Nid.Platform.Infrastructure.Audit;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>Workers and keys belong to no household item. A key row is audited without its private key.</summary>
public sealed class AiAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(ModelWorkerRow)] = AuditRule.NoItem,
        [typeof(AiSettingsRow)] = AuditRule.NoItem,
        [typeof(ConsentRow)] = AuditRule.NoItem,
        [typeof(AiKeyRow)] = AuditRule.NoItem with { Secret = new HashSet<string> { "private_key" } },
    };

    /// <summary>A job's progress is operational state (sent, polled, retried), not a decision: its submission and the admin's
    /// actions are what is audited.</summary>
    /// <summary><c>llm_call</c> is itself the journal of model calls (§11).</summary>
    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow), typeof(JobRow), typeof(LlmCallRow) };
}
