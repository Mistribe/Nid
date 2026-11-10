using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Scenarios;

/// <summary>A holding already converted, split by class; <c>Locked</c> per slice from its liquidity.</summary>
public sealed record SavingsSlice(ItemId Item, string Class, string Liquidity, decimal Amount, int? UnlocksAt = null);

public sealed record ScenarioFacts(
    LocalDate Today, decimal? MonthlyIncome,          // null: no income declared
    decimal Subscriptions, decimal PropertyCosts,     // this month, cash
    IReadOnlyList<SavingsSlice> Savings, IReadOnlyList<RealEstateMonth> RealEstate,
    IReadOnlyList<SimulatedLoan> Loans, decimal MonthlyTaxIncrease);   // 0 for a primary home

public sealed record CapacityResult(
    YearMonth RetainedMonth, decimal NewPayment, decimal LoansCost, LocalDate LastPayment,
    decimal? DebtRatioBefore, decimal? DebtRatioAfter, decimal LeftBefore, decimal LeftAfter,
    decimal MonthlyTaxIncrease, IReadOnlyDictionary<string, decimal> SavingsAfterByLiquidity, IReadOnlyList<string> Warnings);

/// <summary>A month's last day (the first point is today) and the net worth of each series there.</summary>
public sealed record ProjectionPoint(LocalDate On, decimal Without, decimal With);

public sealed record ProjectionResult(
    IReadOnlyList<ProjectionPoint> Points, decimal WithoutAtHorizon, decimal WithAtHorizon, decimal Gap,
    YearMonth? SavingsExhaustedIn, IReadOnlyList<string> Warnings);

/// <summary>Property purchase scenario spec §4: pure, everything is read and converted beforehand.</summary>
public static partial class ScenarioCalculator
{
    public const decimal HcsfRatio = DebtRatio.Ceiling;
    public const decimal RentInRatio = 0.7m;

    private static YearMonth Of(LocalDate d) => new(d.Year, d.Month);

    private static decimal Cent(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>The rent the purchase brings in each month: the net expected rent of a rental, the rent no longer paid on a primary home.</summary>
    private static decimal Rent(PropertyPurchase doc) =>
        doc.Use == PurchaseUse.Rental ? doc.ExpectedRent * (12 - doc.VacancyMonths) / 12 : doc.CurrentRent;

    /// <summary>The down payment leaves each envelope's slices pro rata to their amounts.</summary>
    private static IEnumerable<SavingsSlice> AfterFunding(PropertyPurchase doc, IReadOnlyList<SavingsSlice> savings)
    {
        var taken = doc.Funding.ToDictionary(f => f.Item, f => f.Amount);
        var totals = savings.GroupBy(s => s.Item).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
        return savings.Select(s => taken.TryGetValue(s.Item, out var t) && totals[s.Item] != 0m
            ? s with { Amount = s.Amount * (1m - t / totals[s.Item]) }
            : s);
    }

    /// <summary>Spec §4.1, at the month of the highest total payment of the scenario's loans (the first one on a tie).</summary>
    public static CapacityResult Capacity(PropertyPurchase doc, ScenarioFacts facts)
    {
        var rows = facts.Loans.SelectMany(l => l.Rows).ToList();
        var byMonth = rows
            .GroupBy(r => Of(r.DueDate))
            .Select(g => (Month: g.Key, Total: g.Sum(r => r.Payment + r.Insurance)))
            .OrderBy(m => m.Month)
            .ToList();
        var retained = byMonth.Count == 0 ? Of(facts.Today) : byMonth.MaxBy(m => m.Total).Month;
        var newPayment = byMonth.Count == 0 ? 0m : byMonth.Max(m => m.Total);
        var loansCost = rows.Sum(r => r.Interest + r.Insurance) + facts.Loans.Sum(l => l.Fees);

        var loansToday = facts.RealEstate.Count == 0 ? 0m : facts.RealEstate[0].LoanPayments;
        var loansRetained = facts.RealEstate.FirstOrDefault(m => m.Month == retained)?.LoanPayments ?? 0m;

        var rental = doc.Use == PurchaseUse.Rental;
        var income = facts.MonthlyIncome ?? 0m;
        var hasIncome = income > 0m;
        var ratioBase = income + (rental ? RentInRatio * doc.ExpectedRent : 0m);
        var before = DebtRatio.Of(loansToday, income);
        var after = hasIncome ? DebtRatio.Of(loansRetained + newPayment, ratioBase) : null;

        var leftBefore = income - facts.Subscriptions - loansToday - facts.PropertyCosts;
        var leftAfter = leftBefore - newPayment - doc.MonthlyCharges + Rent(doc) - facts.MonthlyTaxIncrease + (loansToday - loansRetained);

        var savingsAfter = AfterFunding(doc, facts.Savings)
            .GroupBy(s => s.Liquidity)
            .ToDictionary(g => g.Key, g => Cent(g.Sum(s => s.Amount)));

        var warnings = new List<string>();
        if (!hasIncome) warnings.Add("no_income");
        else if (after > HcsfRatio) warnings.Add("debt_ratio_above_hcsf");

        return new(
            retained, Cent(newPayment), Cent(loansCost), rows.Count == 0 ? facts.Today : rows.Max(r => r.DueDate),
            before, after, Cent(leftBefore), Cent(leftAfter), Cent(facts.MonthlyTaxIncrease), savingsAfter, warnings);
    }

    /// <summary>
    /// Spec §4.2: month k (1 to horizon × 12) is this month plus k − 1; a purchase dated before today is bought today.
    /// The monthly savings go to an <c>available</c> part of <c>SavingsClass</c>; the scenario's loans pair with <c>doc.Loans</c> by index.
    /// </summary>
    public static ProjectionResult Project(PropertyPurchase doc, ScenarioFacts facts, CapacityResult capacity)
    {
        var a = doc.Assumptions;
        var warnings = new List<string>();
        var purchaseOn = doc.PurchaseOn;
        if (purchaseOn < facts.Today)
        {
            purchaseOn = facts.Today;
            warnings.Add("purchase_in_past");
        }
        var purchaseMonth = Of(purchaseOn);
        var monthlyRate = facts.Savings.Select(s => s.Class).Append(a.SavingsClass).Distinct().ToDictionary(c => c,
            c => (decimal)Math.Pow(1 + (double)a.ClassRates.GetValueOrDefault(c), 1.0 / 12) - 1m);
        var growth = (double)(a.PropertyGrowth ?? a.ClassRates.GetValueOrDefault(AllocationClasses.RealEstate));
        var loans = doc.Loans.Zip(facts.Loans, (l, s) => (l.Amount, s.Rows)).ToList();
        var monthlyCost = doc.MonthlyCharges - Rent(doc) + facts.MonthlyTaxIncrease;

        decimal Existing(YearMonth m) =>
            facts.RealEstate.LastOrDefault(r => r.Month <= m) is { } r ? r.PropertyValue - r.Debt : 0m;
        List<SavingsSlice> Grow(IEnumerable<SavingsSlice> parts) =>
            parts.Select(p => p with { Amount = p.Amount * (1m + monthlyRate[p.Class]) }).ToList();

        var without = facts.Savings.ToList();
        var with = without;
        decimal potWithout = 0m, potWith = 0m;   // the monthly savings, in SavingsClass
        var held = 0;                            // months since the purchase month
        YearMonth? exhausted = null;
        var start = without.Sum(p => p.Amount) + Existing(Of(facts.Today));
        var points = new List<ProjectionPoint> { new(facts.Today, Cent(start), Cent(start)) };

        for (var k = 1; k <= a.HorizonYears * 12; k++)
        {
            var month = Of(facts.Today).PlusMonths(k - 1);
            var end = month.ToDateInterval().End;
            var bought = month >= purchaseMonth;
            if (month == purchaseMonth) with = AfterFunding(doc, with).ToList();

            without = Grow(without);
            with = Grow(with);
            potWithout = potWithout * (1m + monthlyRate[a.SavingsClass]) + a.MonthlySavings;
            potWith = potWith * (1m + monthlyRate[a.SavingsClass]) + a.MonthlySavings;
            if (bought)
                potWith -= loans.Sum(l => l.Rows.Where(r => Of(r.DueDate) == month).Sum(r => r.Payment + r.Insurance)) + monthlyCost;

            if (exhausted is null && with.Where(p => p.Liquidity == AllocationLiquidity.Available).Sum(p => p.Amount) + potWith < 0m)
                exhausted = month;

            // shortcut: the existing real estate at point k is the row of month k − 1's growth, one month behind the savings; align the grids if a month's lag shows.
            var existing = Existing(month);
            var netWithout = without.Sum(p => p.Amount) + potWithout + existing;
            var netWith = with.Sum(p => p.Amount) + potWith + existing;
            if (bought)
            {
                var debt = loans.Sum(l => l.Rows.LastOrDefault(r => r.DueDate <= end) is { } r ? r.Balance : l.Amount);
                netWith += doc.Price * (decimal)Math.Pow(1 + growth, held++ / 12.0) - debt;
            }
            points.Add(new(end, Cent(netWithout), Cent(netWith)));
        }

        if (exhausted is not null) warnings.Add("savings_exhausted");
        var last = points[^1];
        return new(points, last.Without, last.With, last.With - last.Without, exhausted, warnings);
    }
}
