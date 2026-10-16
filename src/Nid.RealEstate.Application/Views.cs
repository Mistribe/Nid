using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>
/// A row of <c>GET /loans</c> (spec 3A2 §4): the figures today; amounts and rates as invariant text (precision 1).
/// <c>PropertyName</c> is null when the reader cannot read the linked property, whose id stays (spec 3C1 §4): it is not exposed.
/// </summary>
public sealed record LoanSummary(
    ItemId Id, string Name, string? Lender, string Currency, string Visibility, string Principal, string RemainingBalance,
    LocalDate? NextPaymentOn, string? NextPaymentTotal, string? CurrentRate, LocalDate? EndDate, bool CanWrite, bool Ended,
    ItemId? PropertyId, string? PropertyName);

public sealed record LoanList(IReadOnlyList<LoanSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

public sealed record LoanPhaseView(
    int Seq, string Mode, string AnnualRate, string RateType, int? Payments, string? PaymentOverride, string? OpeningBalanceOverride, string? Note);

public sealed record LoanInsuranceView(string Mode, string? MonthlyAmount, string? AnnualRate, LocalDate StartsOn, LocalDate? EndsOn, string? Note);

/// <summary>The contract as its <c>PUT</c> sends it back, and the <c>version</c> it carries.</summary>
public sealed record LoanContractView(
    string Principal, LocalDate DisbursedOn, LocalDate FirstPaymentDate, int TotalPayments, string Rounding, string FirstPeriod,
    IReadOnlyList<LoanPhaseView> Phases, IReadOnlyList<LoanInsuranceView> Insurances, uint Version);

public sealed record LoanBorrowerView(Guid PersonId, int ShareBp);

/// <summary>A fee; a penalty names the repayment it belongs to (precision 9).</summary>
public sealed record LoanFeeView(Guid Id, string Kind, string Amount, LocalDate OnDate, string? Note, Guid? RepaymentId, uint Version);

/// <summary>An early repayment; <c>Penalty</c> is its penalty's amount.</summary>
public sealed record LoanRepaymentView(Guid Id, LocalDate OnDate, string Amount, string Effect, string? Penalty, string? Note, uint Version);

/// <summary>Every field of <see cref="LoanFigures"/> (loans.md §8), today.</summary>
public sealed record LoanFiguresView(
    string Balance, string PrincipalRepaid, string InterestPaid, string InsurancePaid, string FeesPaid, int? CurrentPhaseSeq,
    string? CurrentRate, int PaymentsLeft, LocalDate? NextPaymentOn, string? NextPaymentTotal, LocalDate? EndDate,
    string TotalCost, string CostPaid, string CostRemaining);

public sealed record LoanWarningView(string Code, int Row);

public sealed record LoanDetail(
    ItemId Id, string Name, string? Lender, string? Note, string Currency, string Visibility, IReadOnlyList<LoanBorrowerView> Borrowers,
    LoanContractView Contract, IReadOnlyList<LoanFeeView> Fees, IReadOnlyList<LoanRepaymentView> Repayments, LoanFiguresView Figures,
    IReadOnlyList<LoanWarningView> Warnings, bool CanWrite, bool Ended, ItemId? PropertyId, string? PropertyName,
    string ScheduleSource, BankScheduleView? BankSchedule, ScheduleComparisonView? Comparison) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

/// <summary>A row of the schedule; <c>FromBank</c>: its figures are the bank's table's (spec 3F2 §3), only with the bank as the reference.</summary>
public sealed record LoanScheduleRowView(
    int Number, LocalDate DueDate, int PhaseSeq, string AnnualRate, string Payment, string Interest, string Principal,
    string Insurance, string Balance, string? EarlyRepayment, bool FromBank);

/// <summary>A row of the bank's table on the wire (spec 3F2 §3); <c>Insurance</c> is null when the table does not detail it.</summary>
public sealed record BankRowView(LocalDate DueDate, string Payment, string Interest, string Principal, string? Insurance, string Crd);

/// <summary>The stored table: its rows by date and its period.</summary>
public sealed record BankScheduleView(IReadOnlyList<BankRowView> Rows, LocalDate FirstDueOn, LocalDate LastDueOn);

/// <summary>Spec 3F2 §2's summary of the comparison with the calculation; gaps are the bank's figure minus the calculation's.</summary>
public sealed record ScheduleComparisonView(string MaxGap, LocalDate? MaxGapOn, LocalDate? DivergesOn, string InterestGap, int RowsBeyondRounding);

/// <summary><c>POST …/bank-schedule/preview</c>: the rows read and their comparison; nothing is stored.</summary>
public sealed record BankSchedulePreview(IReadOnlyList<BankRowView> Rows, ScheduleComparisonView Comparison);

/// <summary>Spec 3B1 §4.1: one calendar year of the schedule: the sums of its rows, and the balance after its last one.</summary>
public sealed record LoanScheduleYearView(
    int Year, string Payment, string Interest, string Principal, string Insurance, string EarlyRepayment, string Balance);

/// <summary>The rows, their years, and the totals of interest and principal the chart's summary reads (3B1 precision 2).</summary>
public sealed record LoanScheduleView(
    IReadOnlyList<LoanScheduleRowView> Rows, IReadOnlyList<LoanScheduleYearView> Years, string TotalInterest,
    string TotalPrincipal, IReadOnlyList<LoanWarningView> Warnings);

public sealed record LoanOutcomeView(
    string Effect, string PaymentBefore, string PaymentAfter, LocalDate? EndBefore, LocalDate? EndAfter, int PaymentsBefore,
    int PaymentsAfter, string InterestBefore, string InterestAfter, string InterestSaved, string NetSaving);

public sealed record LoanSimulationView(LocalDate On, string Amount, string Penalty, IReadOnlyList<LoanOutcomeView> Outcomes);

/// <summary>Pure builders of the views, with the real engine (spec §5 tests them in Nid.RealEstate.Tests).</summary>
internal static class LoanViews
{
    public static LoanSummary Summary(ItemAccess item, StoredLoan loan, LocalDate today, bool canWrite, string? propertyName = null)
    {
        var terms = Terms(CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
        var schedule = LoanReading.Schedule(terms, loan);
        var figures = LoanMath.At(terms, schedule, today);
        return new LoanSummary(loan.ItemId, item.DisplayName, loan.Lender, terms.Currency.Code, Wire(item.Visibility), Amount(loan.Contract.Principal),
            Amount(figures.Balance), figures.NextPaymentOn, Amount(figures.NextPaymentTotal), Rate(figures.CurrentRate), figures.EndDate,
            canWrite, Ended(terms, schedule, today), loan.PropertyId, propertyName);
    }

    public static LoanDetail Detail(ItemAccess item, StoredLoan loan, LocalDate today, bool canWrite, string? propertyName = null)
    {
        var terms = Terms(CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
        var schedule = LoanReading.Schedule(terms, loan);
        // Spec 3F2 §3–§4: a stored table is compared with the calculation whatever the reference; the calculation as the reference
        // drifting from it is a warning, at the computed row of the date it diverges on.
        var bank = LoanReading.BankRows(loan);
        var computed = bank.Count == 0 ? null : loan.Source == ScheduleSource.Computed ? schedule : LoanReading.Computed(terms);
        var diff = computed is null ? null : BankSchedule.Diff(computed, bank);
        var warnings = WarningsOf(schedule);
        if (loan.Source == ScheduleSource.Computed && diff?.Summary.DivergesOn is { } on)
            warnings.Add(new LoanWarningView(LoanWarnings.ScheduleMismatch, computed!.Rows.First(r => r.DueDate == on).Number));
        if (loan.Source == ScheduleSource.Bank && bank.Count > 0 && !schedule.Rows.Any(r => r.FromBank))
            warnings.Add(new LoanWarningView(LoanWarnings.BankScheduleUnfit, 0));
        var fees = loan.Fees.OrderBy(f => f.Fee.OnDate).ThenBy(f => f.Id)
            .Select(f => new LoanFeeView(f.Id, LoanWire.Of(f.Fee.Kind), Amount(f.Fee.Amount), f.Fee.OnDate, f.Note,
                loan.Repayments.FirstOrDefault(r => r.FeeId == f.Id)?.Id, f.Version))
            .ToList();
        var repayments = loan.Repayments.OrderBy(r => r.Repayment.OnDate).ThenBy(r => r.Id)
            .Select(r => new LoanRepaymentView(r.Id, r.Repayment.OnDate, Amount(r.Repayment.Amount), LoanWire.Of(r.Repayment.Effect),
                Amount(loan.Fees.FirstOrDefault(f => f.Id == r.FeeId)?.Fee.Amount), r.Note, r.Version))
            .ToList();
        return new LoanDetail(loan.ItemId, item.DisplayName, loan.Lender, loan.Note, terms.Currency.Code, Wire(item.Visibility),
            item.Owners.Select(o => new LoanBorrowerView(o.PersonId.Value, o.ShareBp)).ToList(), ContractOf(loan), fees, repayments,
            FiguresOf(LoanMath.At(terms, schedule, today)), warnings, canWrite, Ended(terms, schedule, today),
            loan.PropertyId, propertyName, LoanWire.Of(loan.Source),
            bank.Count == 0 ? null : new BankScheduleView(bank.Select(BankRowOf).ToList(), bank[0].DueDate, bank[^1].DueDate),
            diff is null ? null : ComparisonOf(diff.Summary));
    }

    /// <summary>
    /// The rows and their years (spec 3B2 §3): a year sums the rows due in it and the repayments dated in it, and its balance
    /// is the one on 31 December, read as the figures read it (a repayment applied after a row but dated later is not yet
    /// made); the years listed are the rows' and the repayments'.
    /// </summary>
    public static LoanScheduleView ScheduleOf(LoanTerms terms, LoanSchedule schedule)
    {
        var rows = schedule.Rows;
        var years = rows.Select(r => r.DueDate.Year).Concat(terms.Events.Select(e => e.OnDate.Year)).Distinct().Order();
        return new(rows.Select(r => new LoanScheduleRowView(r.Number, r.DueDate, r.PhaseSeq, Rate(r.AnnualRate), Amount(r.Payment),
                Amount(r.Interest), Amount(r.Principal), Amount(r.Insurance), Amount(r.Balance), Amount(r.EarlyRepayment), r.FromBank)).ToList(),
            years.Select(y =>
            {
                var due = rows.Where(r => r.DueDate.Year == y).ToList();
                return new LoanScheduleYearView(y, Amount(due.Sum(r => r.Payment)), Amount(due.Sum(r => r.Interest)),
                    Amount(due.Sum(r => r.Principal)), Amount(due.Sum(r => r.Insurance)),
                    Amount(terms.Events.Where(e => e.OnDate.Year == y).Sum(e => e.Amount)),
                    Amount(LoanMath.At(terms, schedule, new LocalDate(y, 12, 31)).Balance));
            }).ToList(),
            Amount(rows.Sum(r => r.Interest)), Amount(rows.Sum(r => r.Principal)),
            WarningsOf(schedule));
    }

    public static LoanSimulationView SimulationOf(EarlyRepaymentSimulation simulation)
        => new(simulation.On, Amount(simulation.Amount), Amount(simulation.Penalty), simulation.Outcomes
            .Select(o => new LoanOutcomeView(LoanWire.Of(o.Effect), Amount(o.PaymentBefore), Amount(o.PaymentAfter), o.EndBefore,
                o.EndAfter, o.PaymentsBefore, o.PaymentsAfter, Amount(o.InterestBefore), Amount(o.InterestAfter),
                Amount(o.InterestSaved), Amount(o.NetSaving)))
            .ToList());

    public static BankRowView BankRowOf(BankRow r)
        => new(r.DueDate, Amount(r.Payment), Amount(r.Interest), Amount(r.Principal), Amount(r.Insurance), Amount(r.Crd));

    public static ScheduleComparisonView ComparisonOf(ScheduleDiffSummary s)
        => new(Amount(s.MaxGap), s.MaxGapOn, s.DivergesOn, Amount(s.InterestGap), s.RowsBeyondRounding);

    /// <summary>A loan's item always has a currency (it is created with one); its absence is a broken invariant.</summary>
    public static Currency CurrencyOf(ItemAccess item)
        => Currency.TryCreate(item.Currency ?? "", out var currency)
            ? currency
            : throw new InvalidOperationException($"Loan {item.Id} has no currency.");

    public static string Wire(Visibility visibility) => visibility == Visibility.Private ? "private" : "shared";

    private static LoanContractView ContractOf(StoredLoan loan)
    {
        var c = loan.Contract;
        return new LoanContractView(Amount(c.Principal), c.DisbursedOn, c.FirstPaymentDate, c.TotalPayments, LoanWire.Of(c.Rounding),
            LoanWire.Of(c.FirstPeriod),
            c.Phases.OrderBy(p => p.Phase.Seq).Select(p => new LoanPhaseView(p.Phase.Seq, LoanWire.Of(p.Phase.Mode), Rate(p.Phase.AnnualRate),
                LoanWire.Of(p.Phase.RateType), p.Phase.Payments, Amount(p.Phase.PaymentOverride), Amount(p.Phase.OpeningBalanceOverride),
                p.Note)).ToList(),
            c.Insurances.Select(i => new LoanInsuranceView(LoanWire.Of(i.Insurance.Mode), Amount(i.Insurance.MonthlyAmount),
                Rate(i.Insurance.AnnualRate), i.Insurance.StartsOn, i.Insurance.EndsOn, i.Note)).ToList(),
            loan.Version);
    }

    private static LoanFiguresView FiguresOf(LoanFigures f)
        => new(Amount(f.Balance), Amount(f.PrincipalRepaid), Amount(f.InterestPaid), Amount(f.InsurancePaid), Amount(f.FeesPaid),
            f.CurrentPhaseSeq, Rate(f.CurrentRate), f.PaymentsLeft, f.NextPaymentOn, Amount(f.NextPaymentTotal), f.EndDate,
            Amount(f.TotalCost), Amount(f.CostPaid), Amount(f.CostRemaining));

    private static List<LoanWarningView> WarningsOf(LoanSchedule schedule)
        => schedule.Warnings.Select(w => new LoanWarningView(w.Code, w.Row)).ToList();
}
