using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Contracts;

public enum ConnectionStatus { Pending, Active, Expired, Revoked, Error }

public enum BankAccountKind { Checking, Savings, Card, Other }

/// <summary>How a card's spending leaves its checking account, when the bank says so.</summary>
public enum CardDebit { Immediate, Deferred }

/// <summary>A consent at a bank (spec §2.2), its creator's only (D7). <paramref name="Source"/> is <c>enable_banking</c>.</summary>
public sealed record BankConnection(
    Guid Id, PersonId Person, string Source, string AspspName, string AspspCountry, ConnectionStatus Status, Instant? ValidUntil,
    Instant? LastSyncedAt, string? LastErrorCode, int ConsecutiveFailures, Instant CreatedAt);

/// <summary>An account seen at the bank (spec §2.3), once per installation however many connections see it (D21).</summary>
/// <param name="IbanMasked">The IBAN's last four characters (a card's number's, when it has no IBAN), never more.</param>
/// <param name="CreatedBy">The creator of the connection that first saw it: unassociated, it stays theirs once that connection is gone.</param>
/// <param name="LastValuedOn">The last date the sync valued the envelope for (spec §4.1); with <paramref name="BalanceConflictOn"/>, the sync's own columns.</param>
/// <param name="AssociatedBy">Who associated it with <paramref name="Envelope"/>: the sync writes as them when the reading connection's creator cannot (R3).</param>
/// <param name="Name">The name every screen and tool shows: <paramref name="HouseholdName"/> when the household set one, else <paramref name="BankName"/>.</param>
/// <param name="HouseholdName">The household's own name for it, which no session read touches; null: the bank's.</param>
/// <param name="BankName">The name the bank's last session gave (empty when it gave none); null on a record built without it, where it is <paramref name="Name"/>.</param>
/// <param name="Debit">A card's immediate or deferred debit, when the bank says which.</param>
/// <param name="Holder">The holder's name, as the bank gives it (#353).</param>
/// <param name="Product">The bank's product name (a card's range), as the bank gives it (#353).</param>
/// <param name="MinPayments">#362: a card's minimum payments a calendar month, which the household sets (null: no rule).</param>
/// <param name="CountWithdrawals">#362: whether a cash withdrawal counts as one of those payments.</param>
public sealed record BankAccount(
    Guid Id, string? IbanMasked, string Name, BankAccountKind Kind, Currency Currency, ItemId? Envelope, Guid? CardOf, bool AutoApplyBalance,
    bool Ignored, LocalDate? BalanceConflictOn, LocalDate? AssociatedOn, PersonId CreatedBy, LocalDate? LastValuedOn = null, PersonId? AssociatedBy = null,
    string? HouseholdName = null, string? BankName = null, CardDebit? Debit = null, string? Holder = null, string? Product = null,
    int? MinPayments = null, bool CountWithdrawals = false);

/// <summary>A connection that sees an account, under the account's uid in that connection's session.</summary>
public sealed record BankAccountSource(Guid Account, Guid Connection, string ProviderUid, string IdentificationHash);

/// <summary>
/// #365: one read of an account at its bank by the sync (its balances and lines, one pass), <paramref name="Attended"/> when the person's
/// browser was forwarded (a click on « Synchroniser »), <paramref name="RateLimited"/> when the bank refused it for its read limit.
/// </summary>
public sealed record BankRead(Guid Account, Instant At, bool Attended, bool RateLimited);

/// <summary>The balance kept for an account and a day (spec §2.4), of the type chosen by spec §4.2.</summary>
public sealed record BankBalance(Guid Account, LocalDate AsOf, decimal Amount, string BalanceType, Instant FetchedAt);

/// <summary>
/// An account as a bank session lists it: <paramref name="Uid"/> holds for that session only, <paramref name="IdentificationHash"/> across
/// sessions; <paramref name="CashAccountType"/> is the ISO 20022 code (<c>CACC</c>, <c>SVGS</c>, <c>CARD</c>…). <paramref name="Name"/> is the bank's
/// most readable field (empty when none reads as a name), <paramref name="NumberLast4"/> a card number's last four digits when it has no IBAN,
/// <paramref name="Holder"/> and <paramref name="Product"/> the bank's <c>name</c> and <c>product</c> when they read as words.
/// </summary>
public sealed record SessionAccount(
    string Uid, string IdentificationHash, string? Iban, string Name, string CashAccountType, string Currency, CardDebit? Debit = null,
    string? NumberLast4 = null, string? Holder = null, string? Product = null);

/// <summary>Where a bank transaction stands against the household's own records; <c>Unlinked</c> in 5B, the others are 5C's.</summary>
public enum TransactionLinkState { Unlinked, Linked, Ignored, Internal }

/// <summary>A booked line as read from the bank or a statement file (spec §2.5); <paramref name="FetchedVia"/> is <c>api</c> or <c>file</c>.</summary>
/// <param name="ProviderRef">The source's own id (<c>entry_reference</c>, FITID, AcctSvcrRef); the fingerprint stands in when it has none.</param>
public sealed record NewBankTransaction(
    string? ProviderRef, LocalDate BookedOn, LocalDate? ValueOn, decimal Amount, Currency Currency,
    string Label, string? Counterparty, string? Remittance, string FetchedVia);

/// <summary>A stored booked transaction, signed (debit negative); <paramref name="NormalizedLabel"/> is what rules and fingerprints read.</summary>
/// <param name="Category">The line's category; null with a <paramref name="CategorySource"/> of hand is « Sans catégorie » chosen by a person.</param>
public sealed record BankTransaction(
    Guid Id, Guid Account, string ProviderRef, string Fingerprint, LocalDate BookedOn, LocalDate? ValueOn,
    decimal Amount, Currency Currency, string Label, string? Counterparty, string? Remittance, string NormalizedLabel,
    TransactionLinkState LinkState, string FetchedVia, Instant CreatedAt, Guid? Category = null, CategorySource? CategorySource = null);

public sealed record TransactionWrite(int Added, int Known);

/// <summary>Why a line is linked (spec §6.3); the wire values are snake_case. <c>Contribution</c> is reserved for 5D.</summary>
public enum LinkOutcome { AsExpected, PriceDiffers, Income, Contribution, InternalTransfer, CardSettlement }

/// <summary>Whether a category is for money out, money in, or a move between the household's own accounts.</summary>
public enum CategoryKind { Expense, Income, Transfer }

/// <summary>What set a line's category; see <c>CategoryPrecedence</c> in the Domain.</summary>
public enum CategorySource { Link, Rule, Hand, Assistant, Hint }

/// <summary>Which side of the account a rule reads: a debit is a negative amount.</summary>
public enum RuleDirection { Debit, Credit }

/// <summary>A household's rule: a line whose label (or counterparty) contains <paramref name="Pattern"/> gets <paramref name="Category"/>.
/// <paramref name="MinAmount"/> and <paramref name="MaxAmount"/> bound the absolute amount, inclusive.</summary>
public sealed record CategoryRule(
    Guid Id, string Pattern, Guid? Account, RuleDirection? Direction, decimal? MinAmount, decimal? MaxAmount, Guid Category,
    PersonId CreatedBy, Instant CreatedAt, Instant? LastUsedAt);

/// <summary>A category of bank lines; <paramref name="Key"/> is a seeded one's stable key, null for the household's own.</summary>
/// <param name="MergedInto">Set on a merged (and archived) category: where its lines went.</param>
public sealed record BankCategory(Guid Id, Guid? Parent, string? Key, string Name, CategoryKind Kind, Instant? ArchivedAt, Guid? MergedInto);

/// <summary>A line's link (spec §2.6): to a charge of an item, or, for an internal transfer or a card settlement, to the counterpart account.</summary>
/// <param name="TargetKey">The charge's key, or <c>flow:{id:N}</c> for a property flow already recorded.</param>
/// <param name="DecidedBy"><c>person:{id}</c>, <c>rule:{id}</c> or <c>job</c>.</param>
public sealed record BankLink(
    Guid Transaction, string? TargetModule, ItemId? TargetItem, string? TargetKey, Guid? CounterpartAccount,
    LinkOutcome Outcome, Money? Expected, string DecidedBy, Instant DecidedAt);

/// <summary>A person's rule: a line of <paramref name="Account"/> whose label stem is <paramref name="LabelStem"/> belongs to <paramref name="TargetItem"/>.</summary>
public sealed record BankRule(
    Guid Id, Guid Account, string LabelStem, string TargetModule, ItemId TargetItem, PersonId CreatedBy, Instant CreatedAt, Instant? LastUsedAt);
