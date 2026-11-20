using Nid.Platform.Infrastructure.Audit;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>
/// A connection is its creator's setting, audited under no item and never with its session id or state hash. An account is logged
/// against its envelope, the item whose readers see it (D7). Balances, transactions and the run journal and the reads (#365) are fetched data, not decisions; the recurring groups are the job's memory.
/// </summary>
public sealed class BankingAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(ApplicationRow)] = new(_ => [], new HashSet<string> { "private_key" }),
        [typeof(ConnectionRow)] = new(_ => [], new HashSet<string> { "session_id", "state_hash" }),
        [typeof(AccountRow)] = new(e => AuditItems.Of(e, nameof(AccountRow.EnvelopeItemId))),
        [typeof(AccountSourceRow)] = AuditRule.NoItem,
        // §0 n°1: a link or a rule tells of a line of its account (its stem, its id), which the item's readers may not read, and a rule is
        // its creator's alone: neither is shown in the item's history.
        [typeof(LinkRow)] = AuditRule.NoItem,
        [typeof(RuleRow)] = AuditRule.NoItem,
        // A category is the household's, no item's; a line's category is not audited (TransactionRow), categorised_by is its record.
        [typeof(CategoryRow)] = AuditRule.NoItem,
        [typeof(CategoryRuleRow)] = AuditRule.NoItem,
        [typeof(CategoryBudgetRow)] = AuditRule.NoItem,
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow), typeof(BalanceRow), typeof(TransactionRow), typeof(RecurringGroupRow), typeof(SyncRunRow), typeof(AccountReadRow) };
}
