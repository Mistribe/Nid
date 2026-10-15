using Nid.Core.Contracts;
using NodaTime;

namespace Nid.RealEstate.Contracts;

public enum PhaseMode { Annuity, DeferredPartial, DeferredTotal }

public enum RateType { Fixed, Variable }

public enum InterestRounding { HalfUp, Truncate }

public enum FirstPeriod { Full, ProrataActual365 }

public enum InsuranceMode { FixedMonthly, RateOnInitial, RateOnRemaining }

public enum FeeKind { File, Guarantee, EarlyRepaymentPenalty, Renegotiation, Other }

public enum RepaymentEffect { ReduceTerm, ReducePayment }

/// <summary>loans.md §2.2: one regime; phases follow each other by <c>Seq</c>. <c>AnnualRate</c> is a fraction (0.035 for 3.5 %).</summary>
public sealed record LoanPhase(
    int Seq, PhaseMode Mode, decimal AnnualRate, RateType RateType,
    int? Payments = null, decimal? PaymentOverride = null, decimal? OpeningBalanceOverride = null);

/// <summary>loans.md §2.3: due for payments dated in [<c>StartsOn</c>, <c>EndsOn</c>).</summary>
public sealed record LoanInsurance(InsuranceMode Mode, decimal? MonthlyAmount, decimal? AnnualRate, LocalDate StartsOn, LocalDate? EndsOn = null);

public sealed record LoanFee(FeeKind Kind, decimal Amount, LocalDate OnDate);

/// <summary>loans.md §2.5: an early repayment.</summary>
public sealed record EarlyRepayment(LocalDate OnDate, decimal Amount, RepaymentEffect Effect);

/// <summary>spec 3A1 §3: everything the engine needs; every amount is in <c>Currency</c>.</summary>
public sealed record LoanTerms(
    Currency Currency, decimal Principal, LocalDate DisbursedOn, LocalDate FirstPaymentDate, int TotalPayments,
    InterestRounding Rounding, FirstPeriod FirstPeriod, IReadOnlyList<LoanPhase> Phases,
    IReadOnlyList<LoanInsurance> Insurances, IReadOnlyList<LoanFee> Fees, IReadOnlyList<EarlyRepayment> Events);

/// <summary>
/// One payment. <c>Payment</c> is the loan payment without insurance; <c>Balance</c> is after the payment and after any early
/// repayment applied right after it; <c>FromBank</c>: the row's figures are the bank's table's (spec 3F2 §2).
/// </summary>
public sealed record ScheduleRow(
    int Number, LocalDate DueDate, int PhaseSeq, decimal AnnualRate, decimal Payment, decimal Interest, decimal Principal,
    decimal Insurance, decimal Balance, decimal? EarlyRepayment, bool FromBank = false);

/// <summary>A warning about row <c>Row</c> (0: before the first payment).</summary>
public sealed record ScheduleWarning(string Code, int Row);

public sealed record LoanSchedule(IReadOnlyList<ScheduleRow> Rows, IReadOnlyList<ScheduleWarning> Warnings);

/// <summary>Spec 3F2 §2: which schedule a loan's readers read.</summary>
public enum ScheduleSource { Computed, Bank }

/// <summary>
/// loans.md §2.6: one row of the bank's table. <c>Payment</c> is the loan payment without insurance; <c>Insurance</c> is null when
/// the table does not detail it; <c>Crd</c> is the remaining balance right after the payment, before an early repayment dated that
/// day or later (spec 3F2 precision 2).
/// </summary>
public sealed record BankRow(LocalDate DueDate, decimal Payment, decimal Interest, decimal Principal, decimal? Insurance, decimal Crd);

/// <summary>Spec 3F2 §2: a bank row against the computed row of its date, each gap the bank's figure minus the calculation's.</summary>
public sealed record ScheduleGap(LocalDate DueDate, decimal Payment, decimal Interest, decimal Principal, decimal Balance);

/// <summary>
/// Spec 3F2 §2: <c>MaxGap</c> the largest gap in absolute value, first reached on <c>MaxGapOn</c> (null when there is none);
/// <c>DivergesOn</c> the first date the remaining balance differs by more than 1; <c>InterestGap</c> the cumulated interest gap;
/// <c>RowsBeyondRounding</c> the rows with a gap above 0.05.
/// </summary>
public sealed record ScheduleDiffSummary(decimal MaxGap, LocalDate? MaxGapOn, LocalDate? DivergesOn, decimal InterestGap, int RowsBeyondRounding);

public sealed record ScheduleDiff(IReadOnlyList<ScheduleGap> Rows, ScheduleDiffSummary Summary);

/// <summary>loans.md §8: a loan at a date.</summary>
public sealed record LoanFigures(
    decimal Balance, decimal PrincipalRepaid, decimal InterestPaid, decimal InsurancePaid, decimal FeesPaid,
    int? CurrentPhaseSeq, decimal? CurrentRate, int PaymentsLeft, LocalDate? NextPaymentOn, decimal? NextPaymentTotal,
    LocalDate? EndDate, decimal TotalCost, decimal CostPaid, decimal CostRemaining);

/// <summary>loans.md §7: one effect of a simulated early repayment, before and after.</summary>
public sealed record RepaymentOutcome(
    RepaymentEffect Effect, decimal PaymentBefore, decimal PaymentAfter, LocalDate? EndBefore, LocalDate? EndAfter,
    int PaymentsBefore, int PaymentsAfter, decimal InterestBefore, decimal InterestAfter, decimal InterestSaved, decimal NetSaving);

public sealed record EarlyRepaymentSimulation(LocalDate On, decimal Amount, decimal Penalty, IReadOnlyList<RepaymentOutcome> Outcomes);

public static class LoanCodes
{
    public const string InvalidLoan = "realestate.invalid_loan";
    public const string PaymentBelowInterest = "realestate.payment_below_interest";
    public const string EventAboveBalance = "realestate.event_above_balance";
    public const string ScheduleTooLong = "realestate.schedule_too_long";

    // Spec 3A2 §4: the server's own.
    public const string LoanConflict = "realestate.loan_conflict";
    public const string FeeConflict = "realestate.fee_conflict";
    public const string RepaymentConflict = "realestate.repayment_conflict";
    public const string FeeLinked = "realestate.fee_linked";

    /// <summary>Spec 3F2 §3: a bank table that cannot be read, or that breaks a rule of its loan.</summary>
    public const string BankScheduleInvalid = "realestate.bank_schedule_invalid";

    /// <summary>Spec 3F2 §3: the bank chosen as the reference without a stored table (in a file, without its rows).</summary>
    public const string BankScheduleMissing = "realestate.bank_schedule_missing";
}

public static class LoanWarnings
{
    public const string ProjectionUsesLastRate = "projection_uses_last_rate";
    public const string EventOffPaymentDate = "event_off_payment_date";
    public const string ExceedsTotalPayments = "exceeds_total_payments";
    public const string OverrideIgnoredAfterEarlyRepayment = "override_ignored_after_early_repayment";

    /// <summary>Spec 3F2 §4: the calculation is the reference and drifts from the stored bank table by more than 1.</summary>
    public const string ScheduleMismatch = "schedule_mismatch";
}
