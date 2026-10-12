using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;
using static Nid.RealEstate.Application.LoanReading;

namespace Nid.RealEstate.Application;

/// <summary>A row of <c>GET /loans</c> (spec 3A2 §4): the figures today; amounts and rates as invariant text (precision 1).</summary>
public sealed record LoanSummary(
    ItemId Id, string Name, string? Lender, string Currency, string Principal, string RemainingBalance,
    LocalDate? NextPaymentOn, string? NextPaymentTotal, string? CurrentRate, LocalDate? EndDate, bool CanWrite, bool Ended);

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
    IReadOnlyList<LoanWarningView> Warnings, bool CanWrite, bool Ended) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

public sealed record LoanScheduleRowView(
    int Number, LocalDate DueDate, int PhaseSeq, string AnnualRate, string Payment, string Interest, string Principal,
    string Insurance, string Balance, string? EarlyRepayment);

public sealed record LoanScheduleView(IReadOnlyList<LoanScheduleRowView> Rows, IReadOnlyList<LoanWarningView> Warnings);

public sealed record LoanOutcomeView(
    string Effect, string PaymentBefore, string PaymentAfter, LocalDate? EndBefore, LocalDate? EndAfter, int PaymentsBefore,
    int PaymentsAfter, string InterestBefore, string InterestAfter, string InterestSaved, string NetSaving);

public sealed record LoanSimulationView(LocalDate On, string Amount, string Penalty, IReadOnlyList<LoanOutcomeView> Outcomes);

/// <summary>Pure builders of the views, with the real engine (spec §5 tests them in Nid.RealEstate.Tests).</summary>
internal static class LoanViews
{
    public static LoanSummary Summary(ItemAccess item, StoredLoan loan, LocalDate today, bool canWrite)
    {
        var terms = Terms(CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
        var schedule = LoanReading.Schedule(terms);
        var figures = LoanMath.At(terms, schedule, today);
        return new LoanSummary(loan.ItemId, item.DisplayName, loan.Lender, terms.Currency.Code, Amount(loan.Contract.Principal),
            Amount(figures.Balance), figures.NextPaymentOn, Amount(figures.NextPaymentTotal), Rate(figures.CurrentRate), figures.EndDate,
            canWrite, Ended(terms, schedule, today));
    }

    public static LoanDetail Detail(ItemAccess item, StoredLoan loan, LocalDate today, bool canWrite)
    {
        var terms = Terms(CurrencyOf(item), loan.Contract, loan.Fees, loan.Repayments);
        var schedule = LoanReading.Schedule(terms);
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
            FiguresOf(LoanMath.At(terms, schedule, today)), WarningsOf(schedule), canWrite, Ended(terms, schedule, today));
    }

    public static LoanScheduleView ScheduleOf(LoanSchedule schedule)
        => new(schedule.Rows.Select(r => new LoanScheduleRowView(r.Number, r.DueDate, r.PhaseSeq, Rate(r.AnnualRate), Amount(r.Payment),
                Amount(r.Interest), Amount(r.Principal), Amount(r.Insurance), Amount(r.Balance), Amount(r.EarlyRepayment))).ToList(),
            WarningsOf(schedule));

    public static LoanSimulationView SimulationOf(EarlyRepaymentSimulation simulation)
        => new(simulation.On, Amount(simulation.Amount), Amount(simulation.Penalty), simulation.Outcomes
            .Select(o => new LoanOutcomeView(LoanWire.Of(o.Effect), Amount(o.PaymentBefore), Amount(o.PaymentAfter), o.EndBefore,
                o.EndAfter, o.PaymentsBefore, o.PaymentsAfter, Amount(o.InterestBefore), Amount(o.InterestAfter),
                Amount(o.InterestSaved), Amount(o.NetSaving)))
            .ToList());

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
