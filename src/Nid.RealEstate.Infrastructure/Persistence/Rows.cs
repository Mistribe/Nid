using NodaTime;

namespace Nid.RealEstate.Infrastructure.Persistence;

/// <summary>realestate.loan (spec 3A2 §2): 1:1 with its Core item; <c>Version</c> is <c>xmin</c>, the contract's version.</summary>
public sealed class LoanRow : IImportMarked
{
    public Guid ItemId { get; set; }
    public string? Lender { get; set; }
    public decimal Principal { get; set; }
    public LocalDate DisbursedOn { get; set; }
    public LocalDate FirstPaymentDate { get; set; }
    public int TotalPayments { get; set; }
    public string RoundingMode { get; set; } = "";
    public string FirstPeriodMode { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>Spec 3C1 §2: the property financed; no foreign key across items (a property is never deleted).</summary>
    public Guid? PropertyItemId { get; set; }

    /// <summary>Spec 3F2 §2: <c>computed</c> or <c>bank</c>, the schedule the loan's readers read.</summary>
    public string ScheduleSource { get; set; } = "computed";

    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

public sealed class LoanPhaseRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public int Seq { get; set; }
    public string Mode { get; set; } = "";
    public decimal AnnualRate { get; set; }
    public string RateType { get; set; } = "";
    public int? Payments { get; set; }
    public decimal? PaymentOverride { get; set; }
    public decimal? OpeningBalanceOverride { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
}

public sealed class LoanInsuranceRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public string Mode { get; set; } = "";
    public decimal? MonthlyAmount { get; set; }
    public decimal? AnnualRate { get; set; }
    public LocalDate StartsOn { get; set; }
    public LocalDate? EndsOn { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
}

public sealed class LoanFeeRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public string Kind { get; set; } = "";
    public decimal Amount { get; set; }
    public LocalDate OnDate { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>realestate.loan_event: an early repayment, the only event (no <c>kind</c> column); <c>FeeId</c> is its penalty.</summary>
public sealed class LoanEventRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public LocalDate OnDate { get; set; }
    public decimal Amount { get; set; }
    public string Effect { get; set; } = "";
    public Guid? FeeId { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>
/// realestate.loan_bank_schedule_row (spec 3F2 §2, loans.md §2.6): a row of the bank's table, unique by (loan, due date);
/// <c>Crd</c> is the remaining balance right after the payment, before an early repayment dated that day or later.
/// </summary>
public sealed class LoanBankScheduleRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public LocalDate DueDate { get; set; }
    public decimal Payment { get; set; }
    public decimal Interest { get; set; }
    public decimal Principal { get; set; }
    public decimal? Insurance { get; set; }
    public decimal Crd { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
}

/// <summary>realestate.property (spec 3C1 §2): 1:1 with its Core item; <c>Version</c> is <c>xmin</c>.</summary>
public sealed class PropertyRow : IImportMarked
{
    public Guid ItemId { get; set; }
    public string PropertyType { get; set; } = "";
    public LocalDate PurchaseDate { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal NotaryFees { get; set; }
    public decimal AgencyFees { get; set; }
    public decimal OtherAcquisitionCosts { get; set; }
    public decimal? SurfaceM2 { get; set; }
    public string? LocationLabel { get; set; }
    public string ValuationCadence { get; set; } = "";
    public int? StaleAfterDays { get; set; }
    public LocalDate? RentalStartsOn { get; set; }
    public string? Note { get; set; }
    public string? EnergyClass { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>realestate.property_recurring_cost (spec 3C2 §2): a planned cost of a property; <c>Version</c> is <c>xmin</c>.</summary>
public sealed class PropertyRecurringCostRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid PropertyItemId { get; set; }
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public int EveryCount { get; set; }
    public string EveryUnit { get; set; } = "";
    public LocalDate StartsOn { get; set; }
    public LocalDate? EndsOn { get; set; }
    public string? Note { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>realestate.property_tenancy (spec 3C2 §2): a tenancy of a property; <c>Version</c> is <c>xmin</c>.</summary>
public sealed class PropertyTenancyRow : IImportMarked
{
    public Guid Id { get; set; }
    public Guid PropertyItemId { get; set; }
    public string Label { get; set; } = "";
    public LocalDate StartsOn { get; set; }
    public LocalDate? EndsOn { get; set; }
    public decimal RentMonthly { get; set; }
    public decimal ChargesMonthly { get; set; }
    public decimal? Deposit { get; set; }
    public string? Note { get; set; }
    public bool Indexed { get; set; }
    public int? IrlReferenceQuarter { get; set; }

    /// <summary>The tenancy a rent revision continues (rental spec §3.2).</summary>
    public Guid? RevisedFrom { get; set; }
    public string? ImportRef { get; set; }
    public string? ImportHash { get; set; }
    public Guid? ImportedBy { get; set; }
    public uint Version { get; set; }
}

/// <summary>realestate.irl (rental spec §3): one IRL value per quarter, <c>2026-Q2</c>; <c>Source</c> is <c>insee</c> or <c>manual</c>.</summary>
public sealed class IrlRow
{
    public string Quarter { get; set; } = "";
    public decimal Value { get; set; }
    public LocalDate? PublishedOn { get; set; }
    public LocalDate FetchedOn { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>realestate.irl_run (rental spec §3): one collection of the series.</summary>
public sealed class IrlRunRow
{
    public Guid Id { get; set; }
    public Instant StartedAt { get; set; }
    public Instant? FinishedAt { get; set; }
    public string Status { get; set; } = "";
    public int Added { get; set; }
    public int Replaced { get; set; }
    public int Rejected { get; set; }
    public string? Message { get; set; }
}
