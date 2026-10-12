using NodaTime;

namespace Nid.RealEstate.Infrastructure.Persistence;

/// <summary>realestate.loan (spec 3A2 §2): 1:1 with its Core item; <c>Version</c> is <c>xmin</c>, the contract's version.</summary>
public sealed class LoanRow
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
    public uint Version { get; set; }
}

public sealed class LoanPhaseRow
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
}

public sealed class LoanInsuranceRow
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public string Mode { get; set; } = "";
    public decimal? MonthlyAmount { get; set; }
    public decimal? AnnualRate { get; set; }
    public LocalDate StartsOn { get; set; }
    public LocalDate? EndsOn { get; set; }
    public string? Note { get; set; }
}

public sealed class LoanFeeRow
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public string Kind { get; set; } = "";
    public decimal Amount { get; set; }
    public LocalDate OnDate { get; set; }
    public string? Note { get; set; }
    public uint Version { get; set; }
}

/// <summary>realestate.loan_event: an early repayment, the only event (no <c>kind</c> column); <c>FeeId</c> is its penalty.</summary>
public sealed class LoanEventRow
{
    public Guid Id { get; set; }
    public Guid LoanItemId { get; set; }
    public LocalDate OnDate { get; set; }
    public decimal Amount { get; set; }
    public string Effect { get; set; } = "";
    public Guid? FeeId { get; set; }
    public string? Note { get; set; }
    public uint Version { get; set; }
}
