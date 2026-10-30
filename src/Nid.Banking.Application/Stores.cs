using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>Bank connections (spec §2.2); the session id is encrypted at rest and only <see cref="SessionIdAsync"/> reads it back.</summary>
public interface IConnectionStore
{
    /// <summary>A connection waiting for the bank's callback; only the SHA-256 of its <c>state</c> is kept, until <paramref name="expiresAt"/>.</summary>
    Task<Guid> CreatePendingAsync(PersonId person, string aspspName, string country, byte[] stateHash, Instant expiresAt, CancellationToken ct);

    /// <summary>
    /// The connection holding this state hash (pending, or being renewed), which it clears: a state is used once, even past its time.
    /// <c>null</c> when none holds it or its time ran out.
    /// </summary>
    Task<BankConnection?> FindByStateAsync(byte[] stateHash, CancellationToken ct);

    /// <summary>A renewal: the connection waits for the bank's callback under this state hash, keeping its status meanwhile.</summary>
    Task SetStateAsync(Guid id, byte[] stateHash, Instant expiresAt, CancellationToken ct);

    /// <summary>Removes the person's pending connections created before <paramref name="before"/>: flows abandoned at the bank.</summary>
    Task DeletePendingAsync(PersonId person, Instant before, CancellationToken ct);

    Task ActivateAsync(Guid id, string sessionId, Instant validUntil, CancellationToken ct);

    /// <summary>The person's bank connections, whatever their status, oldest first; never their <c>file</c> connection.</summary>
    Task<IReadOnlyList<BankConnection>> MineAsync(PersonId person, CancellationToken ct);

    /// <summary>The connections the sync reads: active, or in error with a session that may work again; oldest first. A <c>file</c> connection has none.</summary>
    Task<IReadOnlyList<BankConnection>> ActiveAsync(CancellationToken ct);

    /// <summary>
    /// The person's <c>file</c> connection (spec §2.2: active, no session), created on first use: the accounts they import hang off it.
    /// Holds the person's import lock until the caller's transaction ends.
    /// </summary>
    Task<Guid> FileConnectionAsync(PersonId person, CancellationToken ct);

    /// <summary>The decrypted session id; <c>null</c> without one or when the key ring cannot read it any more.</summary>
    Task<string?> SessionIdAsync(Guid id, CancellationToken ct);

    Task SetStatusAsync(Guid id, ConnectionStatus status, string? errorCode, CancellationToken ct);

    /// <summary>A failure adds to the consecutive failures; a success resets them, stamps <paramref name="at"/> and brings an errored connection back to active.</summary>
    Task RecordSyncAsync(Guid id, Instant at, bool failed, CancellationToken ct);

    /// <summary>Removes the connection and its sources; its accounts stay.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);
}

public interface IAccountStore
{
    /// <summary>
    /// The session's accounts, in its order: each one found again by its IBAN, else by its identification hash (D21), or created; its
    /// source for this connection is added or given the session's uid. This connection's sources of accounts the session leaves out are
    /// removed (the accounts stay, as on a deletion).
    /// </summary>
    Task<IReadOnlyList<BankAccount>> UpsertFromSessionAsync(Guid connection, IReadOnlyList<SessionAccount> accounts, CancellationToken ct);

    Task<BankAccount?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// The account of a statement file: by its IBAN, else by the hash of its account id (upper-cased, without spaces) as a file source
    /// recorded it; <c>null</c> when none.
    /// </summary>
    Task<BankAccount?> FindFromFileAsync(string? iban, string accountId, CancellationToken ct);

    /// <summary>
    /// <see cref="FindFromFileAsync"/>'s account, else a new checking account named <paramref name="name"/> under the file connection, its
    /// source keyed by the hash of the IBAN or account id (never the number itself). A found account is returned as it is: a file source
    /// would make the importer its owner. <paramref name="kind"/> and <paramref name="ibanLast4"/> are a <c>nid.data</c> account's own.
    /// </summary>
    Task<BankAccount> UpsertFromFileAsync(
        Guid fileConnection, string? iban, string accountId, string currency, string name, CancellationToken ct,
        BankAccountKind kind = BankAccountKind.Checking, string? ibanLast4 = null);

    /// <summary>
    /// A bank connection outranks a statement file: when the account's creator holds it through their <c>file</c> connection only, it
    /// becomes <paramref name="person"/>'s, its balance settings (auto-apply, ignored) reset. Whether it changed hands.
    /// </summary>
    Task<bool> ClaimFromFileAsync(Guid account, PersonId person, CancellationToken ct);

    Task<BankAccount?> ForEnvelopeAsync(ItemId envelope, CancellationToken ct);

    Task<IReadOnlyList<BankAccount>> ForConnectionAsync(Guid connection, CancellationToken ct);

    /// <summary>Every account of the installation: two adults' few, filtered by the caller's access rules.</summary>
    Task<IReadOnlyList<BankAccount>> AllAsync(CancellationToken ct);

    /// <summary>The connections that see the account, with its uid in each.</summary>
    Task<IReadOnlyList<BankAccountSource>> SourcesAsync(Guid account, CancellationToken ct);

    /// <summary>
    /// Writes the account's settings (envelope and association date, card's account, auto-apply, ignored), never the sync's columns but to
    /// clear them when the envelope changes. Fails with <see cref="BankingCodes.EnvelopeTaken"/> when another account holds the envelope.
    /// </summary>
    Task<Result> UpdateAsync(BankAccount account, CancellationToken ct);

    /// <summary>
    /// The sync's columns only: the last date valued, and the date of a value it left alone — <c>null</c> once a later date is valued,
    /// the warning gone with it.
    /// </summary>
    Task RecordValuedAsync(Guid account, LocalDate valuedOn, LocalDate? conflictOn, CancellationToken ct);
}

public interface IBalanceStore
{
    /// <summary>One balance per account and day: a second one of the same day replaces the first.</summary>
    Task UpsertAsync(BankBalance balance, CancellationToken ct);

    Task<BankBalance?> LatestAsync(Guid account, CancellationToken ct);

    /// <summary>The account's balances in the month of <paramref name="monthEnd"/>, by date.</summary>
    Task<IReadOnlyList<BankBalance>> InMonthAsync(Guid account, LocalDate monthEnd, CancellationToken ct);
}

/// <summary>
/// One sync, <c>banking.sync_run</c> (spec §2.8): counts and codes only, never a bank, an account, an amount or a person.
/// <paramref name="Trigger"/> is <c>schedule</c> or <c>manual</c>; <paramref name="Outcome"/> <c>running</c>, <c>succeeded</c> or <c>failed</c>.
/// </summary>
public sealed record BankSyncRunView(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Trigger, string Outcome, int Connections, int Accounts, int Balances,
    int Proposals, int Written, int Skipped, string? FailureCode, int Transactions = 0);

public interface IBankSyncRunLog
{
    /// <summary>
    /// Starts a run, or returns <c>null</c> when one is already running: the database holds at most one running row. A run still running
    /// an hour on is closed as failed (<see cref="BankingCodes.RunAbandoned"/>) first.
    /// </summary>
    Task<BankSyncRunView?> TryStartAsync(string trigger, CancellationToken ct);

    Task FinishAsync(BankSyncRunView run, CancellationToken ct);

    Task<bool> IsRunningAsync(CancellationToken ct);

    /// <summary>The most recent runs first.</summary>
    Task<IReadOnlyList<BankSyncRunView>> RecentAsync(int count, CancellationToken ct);

    /// <summary>Deletes finished runs started before <paramref name="before"/>.</summary>
    Task<int> PurgeBeforeAsync(Instant before, CancellationToken ct);
}

/// <summary>Booked transactions (spec §2.5), raw bank data: added once, never updated by a later read.</summary>
public interface ITransactionStore
{
    /// <summary>Adds the lines not already stored by provider ref or fingerprint; never updates a stored one. <paramref name="reconciled"/>: the new lines are not for the reconciliation job (an <c>nid.data</c> import).</summary>
    Task<TransactionWrite> AddAsync(Guid account, IReadOnlyList<NewBankTransaction> lines, CancellationToken ct, bool reconciled = false);

    /// <summary>The latest booking of the account, of one source (<c>api</c>, <c>file</c>) when <paramref name="fetchedVia"/> is given.</summary>
    Task<LocalDate?> LatestBookedOnAsync(Guid account, string? fetchedVia, CancellationToken ct);

    Task<bool> HasFetchedViaAsync(Guid account, string fetchedVia, CancellationToken ct);

    /// <summary>Stored lines of the same amount booked within ±1 day of each given (date, amount).</summary>
    Task<IReadOnlyList<BankTransaction>> NearAsync(Guid account, IReadOnlyList<(LocalDate BookedOn, decimal Amount)> lines, CancellationToken ct);

    /// <summary>
    /// The accounts' transactions, newest booked first, within [<paramref name="from"/>, <paramref name="to"/>] when given; <paramref name="state"/> is a
    /// <c>link_state</c> wire value, null for all. With <paramref name="readable"/> (D19), an internal line whose counterpart account is not in it
    /// counts as <c>linked</c>.
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> ListAsync(
        IReadOnlyList<Guid> accounts, LocalDate? from, LocalDate? to, int skip, int take, CancellationToken ct, string? state = null,
        IReadOnlyList<Guid>? readable = null);

    /// <summary>The lines the reconciliation job has not looked at yet, oldest booked first; null accounts = all.</summary>
    Task<IReadOnlyList<BankTransaction>> UnreconciledAsync(IReadOnlyCollection<Guid>? accounts, CancellationToken ct);

    /// <summary>Unlinked lines booked within [<paramref name="from"/>, <paramref name="to"/>], all accounts.</summary>
    Task<IReadOnlyList<BankTransaction>> UnlinkedBetweenAsync(LocalDate from, LocalDate to, CancellationToken ct);

    Task<BankTransaction?> FindAsync(Guid id, CancellationToken ct);

    Task MarkReconciledAsync(IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct);

    /// <summary>Retention (D3): deletes the unlinked and ignored lines booked before <paramref name="before"/>; returns how many.</summary>
    Task<int> PurgeBookedBeforeAsync(LocalDate before, CancellationToken ct);
}

/// <summary>Links between bank lines and the household's records (spec §2.6).</summary>
public interface ILinkStore
{
    Task<IReadOnlyDictionary<Guid, BankLink>> ForTransactionsAsync(IReadOnlyCollection<Guid> transactions, CancellationToken ct);

    Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> ForItemsAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> OutcomeSinceAsync(LinkOutcome outcome, LocalDate since, CancellationToken ct);

    /// <summary>Upserts the link and sets the line's <c>link_state</c> (linked or internal) in one transaction.</summary>
    Task SetAsync(BankLink link, TransactionLinkState state, CancellationToken ct);

    /// <summary>Deletes the link, if any, and sets the line's state (unlinked or ignored).</summary>
    Task ClearAsync(Guid transaction, TransactionLinkState state, CancellationToken ct);

    /// <summary>Whether a link already has the property flow as its target (<c>target_key</c> <c>flow:{id:N}</c>).</summary>
    Task<bool> FlowTakenAsync(Guid flowId, CancellationToken ct);
}

/// <summary>A person's label rules and the recurring groups already proposed (spec §6.2, §6.4).</summary>
public interface IRuleStore
{
    Task<IReadOnlyList<BankRule>> ForAccountAsync(Guid account, CancellationToken ct);

    Task<IReadOnlyList<BankRule>> ByAsync(PersonId creator, CancellationToken ct);

    /// <summary>Creates the rule, or replaces the target of the creator's rule for (<paramref name="account"/>, <paramref name="stem"/>).</summary>
    Task<BankRule> UpsertAsync(Guid account, string stem, string module, ItemId target, PersonId by, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, PersonId creator, CancellationToken ct);

    Task TouchAsync(Guid id, Instant at, CancellationToken ct);

    /// <summary>Remembers that the group was proposed; false when it already was.</summary>
    Task<bool> TryRememberGroupAsync(Guid account, string stem, Instant at, CancellationToken ct);
}
