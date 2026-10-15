using System.Text.Json.Serialization;
using Nid.Core.Contracts;

namespace Nid.RealEstate.Contracts;

// Spec 3F1 §2: the loan sections of realestate/loans.md §10. Dates stay strings (the section parses them); amounts are
// { amount, currency } in the loan's currency; rates are decimal strings (a JSON number is read too, as a decimal).

public sealed record LoanRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Lender { get; init; }
    public required string Currency { get; init; }
    public required MoneyRecord Principal { get; init; }
    public required string DisbursedOn { get; init; }
    public required string FirstPaymentDate { get; init; }
    public required int TotalPayments { get; init; }
    public string? Property { get; init; }
    public string? ScheduleSource { get; init; }
    public string RoundingMode { get; init; } = "half_up";
    public string FirstPeriodMode { get; init; } = "full";
    public required IReadOnlyList<ShareRecord> Ownership { get; init; }
    public string Visibility { get; init; } = "shared";
    public string? Note { get; init; }
}

public sealed record LoanPhaseRecord
{
    public required string Id { get; init; }
    public required string Loan { get; init; }
    public required int Seq { get; init; }
    public required string Mode { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal AnnualRate { get; init; }

    public required string RateType { get; init; }
    public int? Payments { get; init; }
    public MoneyRecord? PaymentOverride { get; init; }
    public MoneyRecord? OpeningBalanceOverride { get; init; }
    public string? Note { get; init; }
}

public sealed record LoanInsuranceRecord
{
    public required string Id { get; init; }
    public required string Loan { get; init; }
    public required string Mode { get; init; }
    public MoneyRecord? MonthlyAmount { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? AnnualRate { get; init; }

    public required string StartsOn { get; init; }
    public string? EndsOn { get; init; }
    public string? Note { get; init; }
}

public sealed record LoanFeeRecord
{
    public required string Id { get; init; }
    public required string Loan { get; init; }
    public required string Kind { get; init; }
    public required MoneyRecord Amount { get; init; }
    public required string OnDate { get; init; }
    public string? Note { get; init; }
}

/// <summary>An early repayment. Its penalty is not a member: it is a <c>loan_fees</c> entry of kind <c>early_repayment_penalty</c> (precision 8).</summary>
public sealed record LoanEventRecord
{
    public required string Id { get; init; }
    public required string Loan { get; init; }
    public required string OnDate { get; init; }
    public required MoneyRecord Amount { get; init; }
    public required string Effect { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// Spec 3F2 §3 (loans.md §10): a row of a loan's bank table. Amounts are decimal strings in the loan's currency, written with two
/// decimals (a JSON number is read too); <c>Crd</c> is the remaining balance right after the payment.
/// </summary>
public sealed record LoanBankScheduleRecord
{
    public required string Id { get; init; }
    public required string Loan { get; init; }
    public required string DueDate { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Payment { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Interest { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Principal { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal? Insurance { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public required decimal Crd { get; init; }
}
