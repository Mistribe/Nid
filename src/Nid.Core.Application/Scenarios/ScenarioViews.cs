using System.Globalization;
using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Application.Scenarios;

public sealed record FundingLineView(ItemId Item, string Amount);

public sealed record ScenarioDeferralView(string Kind, int Months);

/// <summary><c>Value</c> is an annual rate in percent for <c>rate_on_initial</c>, a monthly amount for <c>fixed_monthly</c>.</summary>
public sealed record ScenarioInsuranceView(string Kind, string Value);

public sealed record ScenarioLoanDocView(
    string Name, string Amount, string Rate, int Months, ScenarioDeferralView? Deferral, ScenarioInsuranceView Insurance,
    string FileFees, string GuaranteeFees);

public sealed record ScenarioAssumptionsView(
    int HorizonYears, IReadOnlyDictionary<string, string> ClassRates, string? PropertyGrowth, string MonthlySavings, string SavingsClass);

/// <summary>Spec §6: the document on the wire, mirroring <see cref="PropertyPurchase"/>; amounts and rates as invariant text, rates in percent.</summary>
public sealed record PropertyPurchaseView(
    int Version, string Use, LocalDate PurchaseOn, string Price, string NotaryFees, string AgencyFees, string OtherCosts,
    string MonthlyCharges, string CurrentRent, string ExpectedRent, int VacancyMonths, string? RentalRegime,
    IReadOnlyList<FundingLineView> Funding, IReadOnlyList<ScenarioLoanDocView> Loans, ScenarioAssumptionsView Assumptions)
{
    public static PropertyPurchaseView From(PropertyPurchase d) => new(
        d.Version, d.Use == PurchaseUse.Rental ? "rental" : "primary", d.PurchaseOn, Amount(d.Price), Amount(d.NotaryFees),
        Amount(d.AgencyFees), Amount(d.OtherCosts), Amount(d.MonthlyCharges), Amount(d.CurrentRent), Amount(d.ExpectedRent),
        d.VacancyMonths, d.RentalRegime, d.Funding.Select(f => new FundingLineView(f.Item, Amount(f.Amount))).ToList(),
        d.Loans.Select(l => new ScenarioLoanDocView(
            l.Name, Amount(l.Amount), Rate(l.Rate), l.Months,
            l.Deferral is { } f ? new ScenarioDeferralView(f.Kind == DeferralKind.Total ? "total" : "partial", f.Months) : null,
            l.Insurance.Kind == ScenarioInsuranceKind.RateOnInitial
                ? new ScenarioInsuranceView("rate_on_initial", Rate(l.Insurance.Value))
                : new ScenarioInsuranceView("fixed_monthly", Amount(l.Insurance.Value)),
            Amount(l.FileFees), Amount(l.GuaranteeFees))).ToList(),
        new ScenarioAssumptionsView(
            d.Assumptions.HorizonYears, d.Assumptions.ClassRates.ToDictionary(p => p.Key, p => Rate(p.Value)),
            d.Assumptions.PropertyGrowth is { } g ? Rate(g) : null, Amount(d.Assumptions.MonthlySavings), d.Assumptions.SavingsClass));

    internal static string Amount(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A fraction as a percent string: <c>0.035</c> gives <c>"3.5"</c>.</summary>
    internal static string Rate(decimal fraction) => (fraction * 100m).ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Spec §6: ratios are percents rounded to 1 decimal (as the dashboard's), null without income.</summary>
public sealed record CapacityView(
    string RetainedMonth, DashboardMoneyView NewPayment, DashboardMoneyView LoansCost, LocalDate LastPayment,
    decimal? DebtRatioBefore, decimal? DebtRatioAfter, DashboardMoneyView LeftBefore, DashboardMoneyView LeftAfter,
    DashboardMoneyView MonthlyTaxIncrease, IReadOnlyDictionary<string, DashboardMoneyView> SavingsAfterByLiquidity)
{
    public static CapacityView From(CapacityResult c, Currency currency) => new(
        $"{c.RetainedMonth.Year:0000}-{c.RetainedMonth.Month:00}", Money(c.NewPayment, currency), Money(c.LoansCost, currency), c.LastPayment,
        Percent(c.DebtRatioBefore), Percent(c.DebtRatioAfter), Money(c.LeftBefore, currency), Money(c.LeftAfter, currency),
        Money(c.MonthlyTaxIncrease, currency), c.SavingsAfterByLiquidity.ToDictionary(p => p.Key, p => Money(p.Value, currency)));

    internal static DashboardMoneyView Money(decimal amount, Currency currency) => new(PropertyPurchaseView.Amount(amount), currency.Code);

    internal static decimal? Percent(decimal? ratio) => ratio is { } r ? Math.Round(r * 100m, 1, MidpointRounding.AwayFromZero) : null;
}

public sealed record ProjectionPointView(LocalDate On, DashboardMoneyView Without, DashboardMoneyView With)
{
    public static ProjectionPointView From(ProjectionPoint p, Currency currency) =>
        new(p.On, CapacityView.Money(p.Without, currency), CapacityView.Money(p.With, currency));
}

/// <summary>Named as <c>LoanScheduleRowView</c>'s fields, so the web's schedule table takes it; <c>EarlyRepayment</c> is always null.</summary>
public sealed record ScenarioScheduleRowView(
    int Number, LocalDate DueDate, string Payment, string Interest, string Principal, string Insurance, string? EarlyRepayment, string Balance);

public sealed record ScenarioScheduleYearView(
    int Year, string Payment, string Interest, string Principal, string Insurance, string EarlyRepayment, string Balance);

public sealed record ScenarioScheduleView(
    IReadOnlyList<ScenarioScheduleRowView> Rows, IReadOnlyList<ScenarioScheduleYearView> Years, string TotalInterest, string TotalPrincipal);

/// <summary>A loan of the scenario as the real engine computes it: its cost and last payment, and its schedule.</summary>
public sealed record ScenarioLoanView(
    string Name, DashboardMoneyView Interest, DashboardMoneyView Insurance, DashboardMoneyView Fees, LocalDate? LastPayment, ScenarioScheduleView Schedule)
{
    public static ScenarioLoanView From(SimulatedLoan loan, Currency currency)
    {
        static string A(decimal v) => PropertyPurchaseView.Amount(v);
        var rows = loan.Rows.Select(r => new ScenarioScheduleRowView(
            r.Number, r.DueDate, A(r.Payment), A(r.Interest), A(r.Principal), A(r.Insurance), null, A(r.Balance))).ToList();
        var years = loan.Rows.GroupBy(r => r.DueDate.Year).Select(g => new ScenarioScheduleYearView(
            g.Key, A(g.Sum(r => r.Payment)), A(g.Sum(r => r.Interest)), A(g.Sum(r => r.Principal)), A(g.Sum(r => r.Insurance)), A(0m),
            A(g.Last().Balance))).ToList();
        var interest = loan.Rows.Sum(r => r.Interest);
        return new ScenarioLoanView(
            loan.Name, CapacityView.Money(interest, currency), CapacityView.Money(loan.Rows.Sum(r => r.Insurance), currency),
            CapacityView.Money(loan.Fees, currency), loan.Rows.Count > 0 ? loan.Rows[^1].DueDate : null,
            new ScenarioScheduleView(rows, years, A(interest), A(loan.Rows.Sum(r => r.Principal))));
    }
}

/// <summary><c>Value</c>: the signed gap for <c>funding_gap</c>, the count for <c>funding_source_hidden</c>, <c>yyyy-MM</c> for <c>savings_exhausted</c>, the currency for <c>fx_missing</c>.</summary>
public sealed record ScenarioWarningView(string Code, string? Value);

/// <summary>Spec §4.2: the points, and at the horizon the net worth without, with the purchase, and their gap (so the browser subtracts nothing).</summary>
public sealed record ScenarioResultView(
    CapacityView Capacity, IReadOnlyList<ProjectionPointView> Points, DashboardMoneyView WithoutAtHorizon, DashboardMoneyView WithAtHorizon,
    DashboardMoneyView Gap, IReadOnlyList<ScenarioLoanView> Loans, IReadOnlyList<ScenarioWarningView> Warnings)
{
    public static ScenarioResultView From(
        CapacityResult capacity, ProjectionResult projection, IReadOnlyList<SimulatedLoan> loans, IReadOnlyList<ScenarioWarningView> warnings, Currency currency) => new(
        CapacityView.From(capacity, currency), projection.Points.Select(p => ProjectionPointView.From(p, currency)).ToList(),
        CapacityView.Money(projection.WithoutAtHorizon, currency), CapacityView.Money(projection.WithAtHorizon, currency),
        CapacityView.Money(projection.Gap, currency), loans.Select(l => ScenarioLoanView.From(l, currency)).ToList(), warnings);
}

public sealed record ScenarioListItemView(
    ItemId Id, string Name, string Use, DashboardMoneyView Price, DashboardMoneyView NewPayment, decimal? DebtRatioAfter,
    DashboardMoneyView Gap, IReadOnlyList<OwnerShare> Owners, string Visibility);

public sealed record ScenarioView(
    ItemId Id, string Name, string Visibility, string Scope, PropertyPurchaseView Document, ScenarioResultView Result, bool CanWrite)
    : IExposesItems
{
    // The funding envelopes the reader may see, which the document names.
    public IEnumerable<ItemId> ExposedItems() => [Id, .. Document.Funding.Select(f => f.Item)];
}

/// <summary>The reader's active scenarios in the scope, each computed on its own scope.</summary>
public sealed record ScenarioList(IReadOnlyList<ScenarioListItemView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

/// <summary>A holding the down payment may come from; <c>Liquidity</c> is its most locked slice (a <c>locked</c> one is refused).</summary>
public sealed record FundingCandidateView(ItemId Id, string Name, DashboardMoneyView Value, string Liquidity);

/// <summary>Spec §6: the pre-filled class rates (percent), today's net income (null without any) and commitments, the usable holdings.</summary>
public sealed record ScenarioDefaultsView(
    IReadOnlyDictionary<string, string> ClassRates, DashboardMoneyView? MonthlyIncome, DashboardMoneyView Commitments,
    IReadOnlyList<FundingCandidateView> Holdings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Holdings.Select(h => h.Id);
}
