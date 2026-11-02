using NodaTime;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class HouseholdSettingsRow
{
    public int Id { get; set; } = 1;
    public string Name { get; set; } = "Nid";
    public string ReferenceCurrency { get; set; } = "EUR";
    public string Timezone { get; set; } = "Europe/Paris";
    public string Locale { get; set; } = "fr-FR";
    public int MaxAdults { get; set; } = 2;
    /// <summary>Issue #104: the target allocation, a JSON object of class → %; null without one.</summary>
    public string? AllocationTarget { get; set; }
    /// <summary>Net worth projection spec §2.1: the household's assumptions as JSON (fractions); null until it chose some.</summary>
    public string? ProjectionAssumptions { get; set; }
    /// <summary>Issue #209: the fee references as JSON (%); null until the household saved some.</summary>
    public string? FeeReferences { get; set; }
    /// <summary>The admin switch of the nightly price collection (holding prices spec §2.1).</summary>
    public bool PricesEnabled { get; set; }
    public bool IrlEnabled { get; set; }
    /// <summary>Bank sync spec D14: the admin switch and the installation's Enable Banking application.</summary>
    public bool BankingEnabled { get; set; }
    public string? EnableBankingAppId { get; set; }
    /// <summary>Bank sync D3: how long unlinked and ignored transactions are kept, in months; null keeps them all.</summary>
    public int? BankRetentionMonths { get; set; }
}

public sealed class TaxProfileRow
{
    public int Year { get; set; }
    public string Data { get; set; } = "{}";
}

/// <summary>Tax assessments spec §3: a notice as a camelCase JSON document; <c>Year</c> repeats the document's for the declarants' unique index.</summary>
public sealed class TaxAssessmentRow
{
    public Guid Id { get; set; }
    public int Year { get; set; }
    public string Data { get; set; } = "{}";
    public uint Version { get; set; }
}

/// <summary>Tax assessments spec §3: who declared a notice, one row per declarant; unique per (year, person).</summary>
public sealed class TaxAssessmentDeclarantRow
{
    public Guid AssessmentId { get; set; }
    public Guid PersonId { get; set; }
    public int Year { get; set; }
    public int Rank { get; set; }
}

public sealed class ScenarioRow
{
    public Guid ItemId { get; set; }
    public string Kind { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Data { get; set; } = "{}";
}

public sealed class PersonRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string? OidcSubject { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public bool IsAdmin { get; set; }
    public int SessionEpoch { get; set; }
    public string Preferences { get; set; } = "{}";
    public LocalDate? BirthDate { get; set; }
    public int RetirementAge { get; set; } = 64;
    public Instant CreatedAt { get; set; }
    public uint Version { get; set; }
}

public sealed class ItemRow
{
    public Guid Id { get; set; }
    public string Module { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Visibility { get; set; } = "shared";
    public string? Currency { get; set; }
    public Instant? ArchivedAt { get; set; }
    public Instant CreatedAt { get; set; }
    public uint Version { get; set; }
    public List<OwnershipRow> Owners { get; set; } = [];
}

public sealed class OwnershipRow
{
    public Guid ItemId { get; set; }
    public Guid PersonId { get; set; }
    public int ShareBp { get; set; }
}

public sealed class FxRateRow
{
    public LocalDate OnDate { get; set; }
    public string Quote { get; set; } = "";
    public decimal Rate { get; set; }
    public string Source { get; set; } = "";
    public Instant FetchedAt { get; set; }
}

public sealed class FxSyncRunRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Instant StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public string Status { get; set; } = "";
    public int FilesRead { get; set; }
    public int RowsWritten { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string Rejected { get; set; } = "[]";
}

/// <summary>2A spec §3.1. Never deleted; only <see cref="RetiredAt"/> and, once its proposal is applied, <see cref="DocumentId"/> are ever updated.</summary>
public sealed class ValuationRow
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public LocalDate AsOf { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? Note { get; set; }
    public string Source { get; set; } = "";
    public string CreatedVia { get; set; } = "";
    public Guid CreatedByPersonId { get; set; }
    public Instant CreatedAt { get; set; }
    public Guid? SupersedesId { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public Instant? RetiredAt { get; set; }
    /// <summary>The source document (Documents module), no foreign key across schemas.</summary>
    public Guid? DocumentId { get; set; }
    public uint Version { get; set; }
}

/// <summary>2A spec §3.2. Never deleted; only <see cref="RetiredAt"/> and, once its proposal is applied, <see cref="DocumentId"/> are ever updated.</summary>
public sealed class FlowRow
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public LocalDate OccurredOn { get; set; }
    public string Kind { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string? Category { get; set; }
    public string? ExternalRef { get; set; }
    public string? Note { get; set; }
    public string Source { get; set; } = "";
    public string CreatedVia { get; set; } = "";
    public Guid CreatedByPersonId { get; set; }
    public Instant CreatedAt { get; set; }
    public Guid? SupersedesId { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public Instant? RetiredAt { get; set; }
    /// <summary>The source document (Documents module), no foreign key across schemas.</summary>
    public Guid? DocumentId { get; set; }
    public uint Version { get; set; }
}

public sealed class PersonalAccessTokenRow
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public Instant CreatedAt { get; set; }
    public Instant ExpiresAt { get; set; }
    public Instant? LastUsedAt { get; set; }
    public Instant? RevokedAt { get; set; }
}

public sealed class ProposalRow
{
    public Guid Id { get; set; }
    public string Module { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid? TargetItemId { get; set; }
    public Guid ForPersonId { get; set; }
    public string? SubjectKey { get; set; }
    public string Payload { get; set; } = "{}";
    public int PayloadVersion { get; set; }
    public string? EditedPayload { get; set; }
    public string Warnings { get; set; } = "[]";
    public string Status { get; set; } = "pending";
    public string Fingerprint { get; set; } = "";
    public Guid? SupersededById { get; set; }
    public string CreatedVia { get; set; } = "";
    public Guid? TokenId { get; set; }
    public Guid CreatedByPersonId { get; set; }
    public string? SourceLabel { get; set; }
    public Instant CreatedAt { get; set; }
    public Guid? DecidedByPersonId { get; set; }
    public Instant? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public Guid[] AppliedRefs { get; set; } = [];
    /// <summary>The source document (Documents module), no foreign key across schemas.</summary>
    public Guid? DocumentId { get; set; }
    public uint Version { get; set; }
}
