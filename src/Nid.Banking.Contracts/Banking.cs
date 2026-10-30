using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Contracts;

public enum ConnectionStatus { Pending, Active, Expired, Revoked, Error }

public enum BankAccountKind { Checking, Savings, Card, Other }

/// <summary>A consent at a bank (spec §2.2), its creator's only (D7). <paramref name="Source"/> is <c>enable_banking</c>.</summary>
public sealed record BankConnection(
    Guid Id, PersonId Person, string Source, string AspspName, string AspspCountry, ConnectionStatus Status, Instant? ValidUntil,
    Instant? LastSyncedAt, string? LastErrorCode, int ConsecutiveFailures, Instant CreatedAt);

/// <summary>An account seen at the bank (spec §2.3), once per installation however many connections see it (D21).</summary>
/// <param name="IbanMasked">The IBAN's last four characters, never more.</param>
/// <param name="CreatedBy">The creator of the connection that first saw it: unassociated, it stays theirs once that connection is gone.</param>
/// <param name="LastValuedOn">The last date the sync valued the envelope for (spec §4.1); with <paramref name="BalanceConflictOn"/>, the sync's own columns.</param>
/// <param name="AssociatedBy">Who associated it with <paramref name="Envelope"/>: the sync writes as them when the reading connection's creator cannot (R3).</param>
public sealed record BankAccount(
    Guid Id, string? IbanMasked, string Name, BankAccountKind Kind, Currency Currency, ItemId? Envelope, Guid? CardOf, bool AutoApplyBalance,
    bool Ignored, LocalDate? BalanceConflictOn, LocalDate? AssociatedOn, PersonId CreatedBy, LocalDate? LastValuedOn = null, PersonId? AssociatedBy = null);

/// <summary>A connection that sees an account, under the account's uid in that connection's session.</summary>
public sealed record BankAccountSource(Guid Account, Guid Connection, string ProviderUid, string IdentificationHash);

/// <summary>The balance kept for an account and a day (spec §2.4), of the type chosen by spec §4.2.</summary>
public sealed record BankBalance(Guid Account, LocalDate AsOf, decimal Amount, string BalanceType, Instant FetchedAt);

/// <summary>
/// An account as a bank session lists it: <paramref name="Uid"/> holds for that session only, <paramref name="IdentificationHash"/> across
/// sessions; <paramref name="CashAccountType"/> is the ISO 20022 code (<c>CACC</c>, <c>SVGS</c>, <c>CARD</c>…).
/// </summary>
public sealed record SessionAccount(string Uid, string IdentificationHash, string? Iban, string Name, string CashAccountType, string Currency);
