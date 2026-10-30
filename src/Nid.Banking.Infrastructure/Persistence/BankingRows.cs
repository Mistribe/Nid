using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class ConnectionRow
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Source { get; set; } = "enable_banking";
    public string AspspName { get; set; } = "";
    public string AspspCountry { get; set; } = "";

    /// <summary>The session id, protected by Data Protection; never audited.</summary>
    public byte[]? SessionId { get; set; }

    /// <summary>The SHA-256 of the pending callback's <c>state</c>; never audited.</summary>
    public byte[]? StateHash { get; set; }

    public Instant? StateExpiresAt { get; set; }
    public string Status { get; set; } = "pending";
    public Instant? ValidUntil { get; set; }
    public Instant? LastSyncedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public int ConsecutiveFailures { get; set; }
    public Instant CreatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class AccountRow
{
    public Guid Id { get; set; }

    /// <summary>The SHA-256 of the IBAN, upper-cased without spaces: one account per IBAN in the installation (D21).</summary>
    public byte[]? IbanHash { get; set; }

    public string? IbanMasked { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Currency { get; set; } = "";
    public Guid? EnvelopeItemId { get; set; }
    public Guid? CardOfAccountId { get; set; }
    public bool AutoApplyBalance { get; set; }
    public bool Ignored { get; set; }
    public LocalDate? BalanceConflictOn { get; set; }
    public LocalDate? AssociatedOn { get; set; }

    /// <summary>Who associated the account with its envelope; cleared with it.</summary>
    public Guid? AssociatedByPersonId { get; set; }

    /// <summary>The creator of the connection that first saw the account; never changes.</summary>
    public Guid CreatedByPersonId { get; set; }

    /// <summary>The sync's: the last date it valued the envelope for.</summary>
    public LocalDate? LastValuedOn { get; set; }

    public uint Version { get; set; }
}

public sealed class AccountSourceRow
{
    public Guid AccountId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ProviderUid { get; set; } = "";
    public string IdentificationHash { get; set; } = "";
}

public sealed class BalanceRow
{
    public Guid AccountId { get; set; }
    public LocalDate AsOf { get; set; }
    public decimal Amount { get; set; }
    public string BalanceType { get; set; } = "";
    public Instant FetchedAt { get; set; }
}

public sealed class TransactionRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string ProviderRef { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public LocalDate BookedOn { get; set; }
    public LocalDate? ValueOn { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Counterparty { get; set; }
    public string? Remittance { get; set; }
    public string NormalizedLabel { get; set; } = "";
    public string LinkState { get; set; } = "unlinked";
    public string FetchedVia { get; set; } = "";
    public Instant CreatedAt { get; set; }

    /// <summary>When the reconciliation job looked at the line; null until then.</summary>
    public Instant? ReconciledAt { get; set; }
}

public sealed class LinkRow
{
    public Guid TransactionId { get; set; }
    public string? TargetModule { get; set; }
    public Guid? TargetItemId { get; set; }
    public string? TargetKey { get; set; }
    public Guid? CounterpartAccountId { get; set; }
    public string Outcome { get; set; } = "";
    public decimal? ExpectedAmount { get; set; }
    public string? ExpectedCurrency { get; set; }

    /// <summary><c>person:{id}</c>, <c>rule:{id}</c> or <c>job</c>.</summary>
    public string DecidedBy { get; set; } = "";

    public Instant DecidedAt { get; set; }
}

public sealed class RuleRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string LabelStem { get; set; } = "";
    public string TargetModule { get; set; } = "";
    public Guid TargetItemId { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? LastUsedAt { get; set; }
}

/// <summary>A recurring group already proposed: the job's memory, never a decision.</summary>
public sealed class RecurringGroupRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string LabelStem { get; set; } = "";
    public Instant ProposedAt { get; set; }
}

public sealed class SyncRunRow
{
    public Guid Id { get; set; }
    public Instant StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public string Trigger { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int Connections { get; set; }
    public int Accounts { get; set; }
    public int Balances { get; set; }
    public int Proposals { get; set; }
    public int Written { get; set; }
    public int Skipped { get; set; }
    public int Transactions { get; set; }
    public string? FailureCode { get; set; }
}
