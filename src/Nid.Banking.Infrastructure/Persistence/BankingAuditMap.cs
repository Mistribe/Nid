using Nid.Platform.Infrastructure.Audit;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>
/// A connection is its creator's setting, audited under no item and never with its session id or state hash. An account is logged
/// against its envelope, the item whose readers see it (D7). Balances, transactions and the run journal are fetched data, not decisions.
/// </summary>
public sealed class BankingAuditMap : IAuditMap
{
    public IReadOnlyDictionary<Type, AuditRule> Audited { get; } = new Dictionary<Type, AuditRule>
    {
        [typeof(ConnectionRow)] = new(_ => [], new HashSet<string> { "session_id", "state_hash" }),
        [typeof(AccountRow)] = new(e => AuditItems.Of(e, nameof(AccountRow.EnvelopeItemId))),
        [typeof(AccountSourceRow)] = AuditRule.NoItem,
    };

    public IReadOnlySet<Type> Excluded { get; } = new HashSet<Type> { typeof(AuditEntryRow), typeof(BalanceRow), typeof(TransactionRow), typeof(SyncRunRow) };
}
