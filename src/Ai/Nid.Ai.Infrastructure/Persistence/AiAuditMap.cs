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
        // #487: a provider's key is audited without the key (llm-providers.md §10).
        [typeof(LlmCredentialRow)] = AuditRule.NoItem with { Secret = new HashSet<string> { "secret_encrypted" } },
        [typeof(CloudProfileRow)] = AuditRule.NoItem,
        // #533: an adult's own consent and preference: a decision, audited.
        [typeof(AssistantPersonRow)] = AuditRule.NoItem,
    };

    /// <summary>A job's progress is operational state (sent, polled, retried), not a decision: its submission and the admin's
    /// actions are what is audited.</summary>
    /// <summary><c>llm_call</c> is itself the journal of model calls (§11).</summary>
    /// <summary>The budget's counters move with every paid call, as the jobs do; its ceiling (ai_settings) is audited.</summary>
    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type>
    {
        typeof(AuditEntryRow), typeof(JobRow), typeof(JobDispatchRow), typeof(LlmCallRow), typeof(BudgetMonthRow), typeof(BudgetReservationRow),
        // #531: a conversation is the person's words, purged with its retention; the audit is kept forever and never holds the
        // question nor the answer (§8.4).
        typeof(ConversationRow), typeof(ConversationMessageRow),
    };
}
