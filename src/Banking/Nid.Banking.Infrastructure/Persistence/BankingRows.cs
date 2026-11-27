using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

/// <summary>#513: Banking's single settings row, moved from <c>core.household_settings</c>.</summary>
public sealed class SettingsRow
{
    public int Id { get; set; } = 1;

    /// <summary>Bank sync spec D14: the installation's Enable Banking application, deprecated by #414.</summary>
    public string? EnableBankingAppId { get; set; }

    /// <summary>Bank sync D3: how long unlinked and ignored transactions are kept, in months; null keeps them all.</summary>
    public int? BankRetentionMonths { get; set; }
}

public sealed class ApplicationRow
{
    public Guid PersonId { get; set; }
    public string ApplicationId { get; set; } = "";

    /// <summary>The private key PEM, protected by Data Protection; never audited.</summary>
    public byte[] PrivateKey { get; set; } = [];

    public Instant KeyLoadedAt { get; set; }
    public Instant CreatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class ConnectionRow
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Source { get; set; } = "enable_banking";

    /// <summary>The Enable Banking application the connection was made with; null: the installation's.</summary>
    public string? ApplicationId { get; set; }
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

    /// <summary>The bank's name for it, refreshed on every session read.</summary>
    public string Name { get; set; } = "";

    /// <summary>The household's own name for it: no session read touches it.</summary>
    public string? HouseholdName { get; set; }

    public string Kind { get; set; } = "";

    /// <summary>A card's <c>immediate</c> or <c>deferred</c> debit, when the bank says which.</summary>
    public string? CardDebit { get; set; }

    /// <summary>#353: the holder's name and the bank's product name, as its latest session gave them; null when it gave none.</summary>
    public string? Holder { get; set; }

    public string? Product { get; set; }

    /// <summary>#362: a card's minimum payments a month, the household's rule; null: none.</summary>
    public int? CardMinPayments { get; set; }

    public bool CardCountWithdrawals { get; set; }

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

    /// <summary>The bank's own transaction code (raw bank data, 140 characters at most); null for lines stored before it was kept.</summary>
    public string? BankCode { get; set; }

    public string NormalizedLabel { get; set; } = "";

    /// <summary>#449: <c>CategoryRules.Suggest</c> of <see cref="NormalizedLabel"/>, written at import; null when no pattern fits.</summary>
    public string? SuggestedPattern { get; set; }
    public string LinkState { get; set; } = "unlinked";
    public string FetchedVia { get; set; } = "";
    public Instant CreatedAt { get; set; }

    /// <summary>When the reconciliation job looked at the line; null until then.</summary>
    public Instant? ReconciledAt { get; set; }

    public Guid? CategoryId { get; set; }

    /// <summary><c>link</c>, <c>rule</c>, <c>hand</c>, <c>assistant</c> or <c>hint</c>; <c>hand</c> with no category is « Sans catégorie ».</summary>
    public string? CategorySource { get; set; }

    /// <summary>The <see cref="CategoryRuleRow"/> that set the category; null once the rule is deleted.</summary>
    public Guid? CategoryRuleId { get; set; }

    public Instant? CategorisedAt { get; set; }

    /// <summary><c>person:{id}</c>, <c>rule:{id}</c> or <c>job</c>, like <see cref="LinkRow.DecidedBy"/>.</summary>
    public string? CategorisedBy { get; set; }

    /// <summary>#84: a person's note on the line, 500 characters at most; null when none.</summary>
    public string? Note { get; set; }
}

/// <summary>#84: a part of a split line; <see cref="Amount"/> is signed like the line, the parts add up to it. Kept in <see cref="Position"/> order.</summary>
public sealed class TransactionPartRow
{
    public Guid Id { get; set; }
    public Guid TransactionId { get; set; }
    public decimal Amount { get; set; }
    public Guid? CategoryId { get; set; }
    public string? Note { get; set; }
    public int Position { get; set; }
}

/// <summary>A category of bank lines, two levels deep; never deleted (lines and the audit refer to it).</summary>
public sealed class CategoryRow
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>A seeded category's stable key (<c>food.groceries</c>); null for the household's own.</summary>
    public string? Key { get; set; }

    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public Instant? ArchivedAt { get; set; }
    public Guid? MergedIntoId { get; set; }
    public uint Version { get; set; }
}

/// <summary>A household's category rule: a line whose label or counterparty contains <see cref="Pattern"/> takes <see cref="CategoryId"/>.</summary>
public sealed class CategoryRuleRow
{
    public Guid Id { get; set; }

    /// <summary>Normalised like a line's label (<c>TransactionLabel.Normalize</c>).</summary>
    public string Pattern { get; set; } = "";

    public Guid? AccountId { get; set; }

    /// <summary><c>debit</c>, <c>credit</c> or null for both.</summary>
    public string? Direction { get; set; }

    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public Guid CategoryId { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant? LastUsedAt { get; set; }
    public uint Version { get; set; }
}

/// <summary>#449: a dismissed category suggestion, the household's: the pattern, of debits or credits, is never again suggested into the category.</summary>
public sealed class SuggestionDismissalRow
{
    public Guid Id { get; set; }

    /// <summary>A suggested pattern (<c>CategoryRules.Suggest</c>).</summary>
    public string Pattern { get; set; } = "";

    /// <summary><c>debit</c> or <c>credit</c>.</summary>
    public string Direction { get; set; } = "";

    public Guid CategoryId { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public Instant CreatedAt { get; set; }
}

/// <summary>#450: a calibration an admin asked for, of one implementation on one model at one digest.</summary>
public sealed class DecideCalibrationRow
{
    public Guid Id { get; set; }
    public string Implementation { get; set; } = "";
    public string Version { get; set; } = "";
    public string Model { get; set; } = "";
    public string Digest { get; set; } = "";

    /// <summary><c>queued</c>, <c>running</c>, <c>done</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = "";

    public string? Code { get; set; }
    public Instant QueuedAt { get; set; }
    public Instant? FinishedAt { get; set; }
}

/// <summary>#450: the evaluation profile of one implementation on one model at one digest, its latest calibration's.</summary>
public sealed class DecideProfileRow
{
    public string Implementation { get; set; } = "";
    public string Version { get; set; } = "";
    public string Model { get; set; } = "";
    public string Digest { get; set; } = "";
    public int EvalVersion { get; set; }

    /// <summary>The most options a case had; recorded, not part of the key.</summary>
    public int Options { get; set; }

    public double? Threshold { get; set; }
    public double? ColdThreshold { get; set; }
    public double? Temperature { get; set; }
    public bool Scoreless { get; set; }

    /// <summary>What was measured (<c>DecideCalibration.Results</c>), as JSON.</summary>
    public string Results { get; set; } = "{}";

    public Instant MeasuredAt { get; set; }
}

/// <summary>#85: the household's budget of an expense category, one per category and per branch (a parent or its children).</summary>
public sealed class CategoryBudgetRow
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }

    /// <summary>In the household's reference currency; null follows what is committed.</summary>
    public decimal? Amount { get; set; }

    /// <summary><c>month</c> or <c>year</c>.</summary>
    public string Period { get; set; } = "";

    public uint Version { get; set; }
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

/// <summary>#365: a read of an account at its bank, fetched data (not a decision): never audited, purged after a week.</summary>
public sealed class AccountReadRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Instant ReadAt { get; set; }
    public bool Attended { get; set; }
    public bool RateLimited { get; set; }
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
    public int RateLimited { get; set; }
    public int Proposals { get; set; }
    public int Written { get; set; }
    public int Skipped { get; set; }
    public int Transactions { get; set; }
    public string? FailureCode { get; set; }
}
