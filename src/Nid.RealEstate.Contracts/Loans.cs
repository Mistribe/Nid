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

/// <summary>One payment. <c>Payment</c> is the loan payment without insurance; <c>Balance</c> is after the payment and after any early repayment applied right after it.</summary>
public sealed record ScheduleRow(
    int Number, LocalDate DueDate, int PhaseSeq, decimal AnnualRate, decimal Payment, decimal Interest, decimal Principal,
    decimal Insurance, decimal Balance, decimal? EarlyRepayment);

/// <summary>A warning about row <c>Row</c> (0: before the first payment).</summary>
public sealed record ScheduleWarning(string Code, int Row);

public sealed record LoanSchedule(IReadOnlyList<ScheduleRow> Rows, IReadOnlyList<ScheduleWarning> Warnings);

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
}

public static class LoanWarnings
{
    public const string ProjectionUsesLastRate = "projection_uses_last_rate";
    public const string EventOffPaymentDate = "event_off_payment_date";
    public const string ExceedsTotalPayments = "exceeds_total_payments";
    public const string OverrideIgnoredAfterEarlyRepayment = "override_ignored_after_early_repayment";
}
