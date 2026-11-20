using Nid.Banking.Contracts;
using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Banking.Application;

/// <summary>What an adult's own application is, without its key.</summary>
public sealed record StoredApplication(PersonId Person, string ApplicationId, Instant KeyLoadedAt, bool KeyReadable);

/// <summary>Each adult's Enable Banking application; the private key is encrypted at rest and only <see cref="CredentialAsync"/> reads it back.</summary>
public interface IApplicationStore
{
    Task<StoredApplication?> FindAsync(PersonId person, CancellationToken ct);

    /// <summary>The decrypted key; null without an application or when the key ring cannot read it.</summary>
    Task<EnableBankingApp?> CredentialAsync(PersonId person, CancellationToken ct);

    /// <summary>
    /// Creates, or changes the id and/or the key (null <paramref name="privateKeyPem"/>: keeps the key). False when a concurrent save
    /// of the same person won: nothing was written.
    /// </summary>
    Task<bool> SaveAsync(PersonId person, string applicationId, string? privateKeyPem, CancellationToken ct);

    /// <summary>False when a concurrent change of the same person won: nothing was removed.</summary>
    Task<bool> DeleteAsync(PersonId person, CancellationToken ct);

    Task<IReadOnlyList<PersonId>> OwnersAsync(CancellationToken ct);
}

/// <summary>Bank connections (spec §2.2); the session id is encrypted at rest and only <see cref="SessionIdAsync"/> reads it back.</summary>
public interface IConnectionStore
{
    /// <summary>A connection waiting for the bank's callback; only the SHA-256 of its <c>state</c> is kept, until <paramref name="expiresAt"/>.</summary>
    Task<Guid> CreatePendingAsync(PersonId person, string applicationId, string aspspName, string country, byte[] stateHash, Instant expiresAt, CancellationToken ct);

    /// <summary>Whether a connection past its pending state was made with this application (the person's, when given).</summary>
    Task<bool> InUseAsync(string applicationId, PersonId? person, CancellationToken ct);

    /// <summary>
    /// Whether a connection past its pending state was made with an application id that is neither <paramref name="installationId"/> nor
    /// its creator's own declared one: a previous installation application, which a new installation id would orphan.
    /// </summary>
    Task<bool> HasOtherApplicationThanAsync(string installationId, CancellationToken ct);

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

    /// <summary>#355: the currency a read learned for an account stored in <see cref="Currency.None"/>; any other account is left as it is.</summary>
    Task ResolveCurrencyAsync(Guid account, Currency currency, CancellationToken ct);

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

    /// <summary>Issue #81: the account's balances from the last one on or before <paramref name="from"/> onwards, by date.</summary>
    Task<IReadOnlyList<BankBalance>> SinceAsync(Guid account, LocalDate from, CancellationToken ct);
}

/// <summary>
/// One sync, <c>banking.sync_run</c> (spec §2.8): counts and codes only, never a bank, an account, an amount or a person.
/// <paramref name="Trigger"/> is <c>schedule</c> or <c>manual</c>; <paramref name="Outcome"/> <c>running</c>, <c>succeeded</c> or <c>failed</c>.
/// </summary>
/// <param name="RateLimited">#365: the accounts a bank refused for its read limit, apart from <paramref name="Skipped"/>.</param>
public sealed record BankSyncRunView(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Trigger, string Outcome, int Connections, int Accounts, int Balances,
    int Proposals, int Written, int Skipped, string? FailureCode, int Transactions = 0, int RateLimited = 0);

/// <summary>#365: the reads the sync made of each account, kept <see cref="Nid.Banking.Domain.BankReads.Kept"/>.</summary>
public interface IAccountReadLog
{
    Task RecordAsync(BankRead read, CancellationToken ct);

    /// <summary>The reads of <paramref name="accounts"/> since <paramref name="since"/>, every account when null.</summary>
    Task<IReadOnlyList<BankRead>> SinceAsync(IReadOnlyCollection<Guid>? accounts, Instant since, CancellationToken ct);

    Task PurgeBeforeAsync(Instant before, CancellationToken ct);
}

public sealed record BankSyncRunPage(IReadOnlyList<BankSyncRunView> Items, int Total, int Page)
{
    public const int PageSize = 50;
}

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

    /// <summary>One page of runs, newest first, and the total number of runs.</summary>
    Task<(IReadOnlyList<BankSyncRunView> Items, int Total)> PageAsync(int skip, int take, CancellationToken ct);

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
    /// counts as <c>linked</c>. <paramref name="uncategorised"/> keeps the lines with no category, no category source and a state other than <c>ignored</c>.
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> ListAsync(
        IReadOnlyList<Guid> accounts, LocalDate? from, LocalDate? to, int skip, int take, CancellationToken ct, string? state = null,
        IReadOnlyList<Guid>? readable = null, bool uncategorised = false);

    /// <summary>The lines the reconciliation job has not looked at yet, oldest booked first; null accounts = all.</summary>
    Task<IReadOnlyList<BankTransaction>> UnreconciledAsync(IReadOnlyCollection<Guid>? accounts, CancellationToken ct);

    /// <summary>Unlinked lines booked within [<paramref name="from"/>, <paramref name="to"/>], all accounts.</summary>
    Task<IReadOnlyList<BankTransaction>> UnlinkedBetweenAsync(LocalDate from, LocalDate to, CancellationToken ct);

    Task<BankTransaction?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Linked or internal lines with no category source yet, by id after <paramref name="after"/> (the category backfill's page).</summary>
    Task<IReadOnlyList<BankTransaction>> UncategorisedLinkedAsync(Guid? after, int take, CancellationToken ct);

    Task MarkReconciledAsync(IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct);

    /// <summary>Retention (D3): deletes the unlinked and ignored lines booked before <paramref name="before"/>; returns how many.</summary>
    Task<int> PurgeBookedBeforeAsync(LocalDate before, CancellationToken ct);

    /// <summary>
    /// #85: the accounts' lines booked within [<paramref name="from"/>, <paramref name="to"/>], ignored and internal ones (transfers, card
    /// settlements) left out, and linked ones too without <paramref name="withLinked"/>, summed by category, currency and month.
    /// </summary>
    Task<IReadOnlyList<SpendingSum>> SpendingAsync(IReadOnlyList<Guid> accounts, LocalDate from, LocalDate to, CancellationToken ct, bool withLinked = true);

    /// <summary>
    /// One page of the lines <see cref="SpendingAsync"/> sums (same filter), newest first, and how many there are: those filed in one of
    /// <paramref name="categories"/>, or, with <paramref name="uncategorised"/>, the debits with no category.
    /// </summary>
    Task<(IReadOnlyList<BankTransaction> Items, int Count)> SpendingLinesAsync(
        IReadOnlyList<Guid> accounts, IReadOnlyList<Guid> categories, bool uncategorised, LocalDate from, LocalDate to, int skip, int take, CancellationToken ct);

    /// <summary>
    /// #84: one page of the lines of <paramref name="filter"/>.Accounts (the actor's pick among <paramref name="readable"/>, already resolved), newest booked
    /// first, with how many there are, and the filter's <see cref="SpendingSum"/>s (ignored and internal lines and ignored accounts left out, no category).
    /// <paramref name="categories"/> are the filter's categories with their children. D19 and the state filter work as in <see cref="ListAsync"/>.
    /// </summary>
    Task<(IReadOnlyList<BankTransaction> Items, int Total, IReadOnlyList<SpendingSum> Sums)> OperationsAsync(
        IReadOnlyList<Guid> readable, Operations.OperationFilter filter, IReadOnlyList<Guid> categories, int skip, int take, CancellationToken ct);

    /// <summary>The categories the accounts' lines are filed in.</summary>
    Task<IReadOnlyList<Guid>> OperationCategoriesAsync(IReadOnlyList<Guid> accounts, CancellationToken ct);

    /// <summary>The accounts' earliest booking, null with no line: how far back their history goes.</summary>
    Task<LocalDate?> EarliestBookedOnAsync(IReadOnlyList<Guid> accounts, CancellationToken ct);
}

/// <summary>One month's lines of a category (null: none) in one currency: <paramref name="Debits"/> negative or zero, <paramref name="Credits"/> positive or zero.</summary>
public sealed record SpendingSum(Guid? Category, Currency Currency, YearMonth Month, decimal Debits, decimal Credits);

/// <summary>Links between bank lines and the household's records (spec §2.6).</summary>
public interface ILinkStore
{
    Task<IReadOnlyDictionary<Guid, BankLink>> ForTransactionsAsync(IReadOnlyCollection<Guid> transactions, CancellationToken ct);

    Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> ForItemsAsync(IReadOnlyCollection<ItemId> items, CancellationToken ct);

    Task<IReadOnlyList<(BankLink Link, BankTransaction Line)>> OutcomeSinceAsync(LinkOutcome outcome, LocalDate since, CancellationToken ct);

    /// <summary>
    /// Upserts the link and sets the line's <c>link_state</c> (linked or internal) in one transaction; with a <paramref name="categoryKey"/>,
    /// the line takes that category as a <c>link</c> one, unless a person's choice holds it (<c>CategoryPrecedence</c>).
    /// </summary>
    Task SetAsync(BankLink link, TransactionLinkState state, CancellationToken ct, string? categoryKey = null);

    /// <summary>
    /// Deletes the link, if any, and sets the line's state (unlinked or ignored); clears a <c>link</c> category, and when ignored a
    /// <c>rule</c> or <c>hint</c> one too.
    /// </summary>
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

/// <summary>The household's categories of bank lines, and the category of each line (spec §Category). A category is never deleted.</summary>
public interface ICategoryStore
{
    /// <summary>Every category, archived and merged ones included: top-level ones first, then by name.</summary>
    Task<IReadOnlyList<BankCategory>> AllAsync(CancellationToken ct);

    Task<BankCategory?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>The category of a seed key, following its merges to the end; null when no category has the key.</summary>
    Task<Guid?> ResolveKeyAsync(string key, CancellationToken ct);

    /// <summary>A new category; under a parent it takes the parent's kind, <paramref name="kind"/> is then ignored.</summary>
    Task<Result<BankCategory>> CreateAsync(Guid? parent, string name, CategoryKind kind, CancellationToken ct);

    /// <summary>
    /// Renames when <paramref name="name"/> is given, moves under <paramref name="parent"/> (null: top-level) when <paramref name="moveParent"/>,
    /// archives or unarchives when <paramref name="archived"/> is given. Archiving a parent archives its children; unarchiving a child
    /// unarchives its parent. A budgeted category moves under no budgeted parent (<see cref="BankingCodes.BudgetBranch"/>).
    /// </summary>
    Task<Result<BankCategory>> UpdateAsync(Guid id, string? name, Guid? parent, bool moveParent, bool? archived, CancellationToken ct);

    /// <summary>
    /// In one transaction: moves the lines and the rules of <paramref name="from"/> to <paramref name="into"/> (their source unchanged), merges each child
    /// into the target's child of the same name (whatever the case) or moves it under the target, then archives <paramref name="from"/> as merged.
    /// A merged category is neither merged again, merged into, moved nor unarchived (<see cref="BankingCodes.CategoryArchived"/>).
    /// A budget follows its category, refused when two would meet on one category or one branch (<see cref="BankingCodes.BudgetConflict"/>).
    /// </summary>
    Task<Result<BankCategory>> MergeAsync(Guid from, Guid into, CancellationToken ct);

    /// <summary>
    /// Sets the category of the lines whose current source <paramref name="source"/> may replace (<c>CategoryPrecedence</c>); a null
    /// <paramref name="source"/> clears category and source (« Laisser Nid décider »), a hand or assistant one only when
    /// <paramref name="byPerson"/>. <paramref name="ruleId"/> is the rule behind a <see cref="CategorySource.Rule"/> write, cleared by any
    /// other. How many lines changed.
    /// </summary>
    Task<int> SetForLinesAsync(
        IReadOnlyCollection<Guid> lines, Guid? category, CategorySource? source, string by, Instant at, bool byPerson, CancellationToken ct,
        Guid? ruleId = null);

    /// <summary>Clears the category of the lines whose current source is one of <paramref name="sources"/>; how many changed.</summary>
    Task<int> ClearSourceAsync(IReadOnlyCollection<Guid> lines, IReadOnlyCollection<CategorySource> sources, CancellationToken ct);
}

/// <summary>#85: the household's budgets, one per category; which category may take one is the caller's to check.</summary>
public interface ICategoryBudgetStore
{
    /// <summary>Every budget, archived categories' included.</summary>
    Task<IReadOnlyList<CategoryBudget>> AllAsync(CancellationToken ct);

    /// <summary>Creates or replaces the budget of <see cref="CategoryBudget.Category"/>.</summary>
    Task SetAsync(CategoryBudget budget, CancellationToken ct);

    /// <summary>Whether the category had one.</summary>
    Task<bool> DeleteAsync(Guid category, CancellationToken ct);
}

/// <summary>The household's category rules (spec §Rules); who sees and applies one is the caller's to check.</summary>
public interface ICategoryRuleStore
{
    /// <summary>Every rule, oldest first.</summary>
    Task<IReadOnlyList<CategoryRule>> AllAsync(CancellationToken ct);

    Task<CategoryRule?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Saves <paramref name="draft"/> as given, under a new id and the current instant; its pattern is normalised already.</summary>
    Task<CategoryRule> CreateAsync(CategoryRule draft, CancellationToken ct);

    /// <summary>Its pattern, account, direction, bounds and category; null when no such rule.</summary>
    Task<CategoryRule?> UpdateAsync(CategoryRule rule, CancellationToken ct);

    /// <summary>Whether it existed; the lines it categorised keep their category, without the rule.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Records that the rules categorised lines at <paramref name="at"/>.</summary>
    Task TouchAsync(IReadOnlyCollection<Guid> ids, Instant at, CancellationToken ct);

    /// <summary>For each rule, how many lines of <paramref name="accounts"/> it categorised, and the latest time it did.</summary>
    Task<IReadOnlyDictionary<Guid, (int Lines, Instant? LastUsed)>> UsageAsync(IReadOnlyCollection<Guid> accounts, CancellationToken ct);

    /// <summary>
    /// The lines of <paramref name="accounts"/> the rule may categorise, newest first: not ignored, with no category or a rule's or a hint's
    /// other than the rule's, in its direction and bounds, whose label or counterparty may hold its pattern. More than it matches: the
    /// caller confirms each line.
    /// </summary>
    Task<IReadOnlyList<BankTransaction>> CandidatesAsync(CategoryRule rule, IReadOnlyCollection<Guid> accounts, CancellationToken ct);
}
