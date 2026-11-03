using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Domain;

/// <summary>Property return spec §4: the dated cash flows of a property from its purchase to a resale. Pure, in the property's currency.</summary>
public static class PropertyReturn
{
    /// <summary>Spec §4.2: a rental deficit offsets the rental income of the next 10 years.</summary>
    public const int DeficitYears = 10;

    /// <summary>Spec §4.1: the commitment ends on 1 January of its start year plus its length.</summary>
    public static LocalDate? PinelEnd(PinelOutlook? pinel) => pinel is null ? null : new LocalDate(pinel.StartYear + pinel.TotalYears, 1, 1);

    /// <summary>Spec §4.5: the sensitivity table runs from today to 15 years on.</summary>
    public const int SensitivityYears = 15;

    /// <summary>Spec §4.4: what the resale day itself holds, left out of the savings effort.</summary>
    private static readonly HashSet<string> ResaleDay = ["resale", "resale_fees", "loan_balance", "penalty", "capital_gains_tax", "pinel_clawback"];

    private static readonly string[] LineOrder =
        ["purchase", "loan_in", "loan_fees", "rent", "charges", "works", "loan_payment", "rental_tax", "pinel", .. ResaleDay];

    /// <summary>Spec §4.1–§4.6: the scenario, the sensitivity rows and the warnings this module can tell.</summary>
    public static PropertyReturnResult Compute(ReturnInputs i)
    {
        var a = i.Assumptions;
        var warnings = new List<string>();
        if (i.Pinel is null)
            warnings.Add("no_pinel");
        var end = PinelEnd(i.Pinel);
        var scenarioOn = (end ?? i.Today).PlusYears(a.YearsAfterPinel);
        if (scenarioOn < i.Today)
        {
            scenarioOn = i.Today;
            warnings.Add("resale_in_past");
        }
        var price = a.ResalePrice ?? Grown(i, scenarioOn);
        if (price is not { } scenarioPrice)
        {
            warnings.Add("no_valuation");
            return new PropertyReturnResult(null, [], end, warnings);
        }

        var flows = Flows(i, scenarioOn, scenarioPrice);
        var effort = -flows.Where(f => !ResaleDay.Contains(f.Line) && f.On <= scenarioOn).GroupBy(f => (f.On.Year, f.On.Month))
            .Sum(g => Math.Min(0m, g.Sum(f => f.Amount)));
        var breakdown = LineOrder.Where(l => flows.Any(f => f.Line == l)).Select(l => new ReturnBreakdownLine(l,
            flows.Where(f => f.Line == l && f.On <= i.Today).Sum(f => f.Amount), flows.Where(f => f.Line == l && f.On > i.Today).Sum(f => f.Amount))).ToList();
        var scenario = new ReturnScenario(Outcome(i, flows, scenarioOn, scenarioPrice, end), a.ResalePrice is null ? "growth" : "entered", effort, breakdown);

        var sensitivity = i.Latest is null ? [] : Enumerable.Range(0, SensitivityYears + 1).Select(k =>
        {
            var on = i.Today.PlusYears(k);
            var at = Grown(i, on)!.Value;
            return Outcome(i, Flows(i, on, at), on, at, end);
        }).ToList();
        return new PropertyReturnResult(scenario, sensitivity, end, warnings);
    }

    private static ReturnOutcome Outcome(ReturnInputs i, IReadOnlyList<CashFlow> flows, LocalDate on, decimal price, LocalDate? end)
        => new(on, price, Xirr.AnnualPercent(flows.Select(f => (f.On, f.Amount)), on), flows.Sum(f => f.Amount), on < end);

    /// <summary>Spec §4.2: the latest valuation grown to <paramref name="on"/>, null without one.</summary>
    private static decimal? Grown(ReturnInputs i, LocalDate on)
        => i.Latest is { } v
            ? Cents(v.Value.Amount * (decimal)Math.Pow(1 + (double)i.Assumptions.ValueGrowth, Period.DaysBetween(i.Today, on) / 365.0))
            : null;

    /// <summary>Spec §4.2–§4.4: every flow to <paramref name="resaleOn"/>, the resale day's included.</summary>
    public static IReadOnlyList<CashFlow> Flows(ReturnInputs i, LocalDate resaleOn, decimal resalePrice)
    {
        var p = i.Property;
        var a = i.Assumptions;
        var list = new List<CashFlow> { new(p.PurchaseDate, "purchase", -(p.PurchasePrice + p.NotaryFees + p.AgencyFees + p.OtherAcquisitionCosts)) };

        // Interest and insurance by year, for the rental income.
        var loanCosts = new Dictionary<int, decimal>();
        foreach (var loan in i.Loans)
        {
            list.Add(new(loan.Terms.DisbursedOn, "loan_in", Cents(loan.Terms.Principal * loan.Rate)));
            foreach (var fee in loan.Terms.Fees.Where(f => f.OnDate <= resaleOn))
            {
                list.Add(new(fee.OnDate, "loan_fees", -Cents(fee.Amount * loan.Rate)));
                Add(loanCosts, fee.OnDate.Year, fee.Amount * loan.Rate);
            }
            foreach (var row in loan.Schedule.Rows.Where(r => r.DueDate <= resaleOn))
            {
                list.Add(new(row.DueDate, "loan_payment", -Cents((row.Payment + row.Insurance + (row.EarlyRepayment ?? 0m)) * loan.Rate)));
                Add(loanCosts, row.DueDate.Year, (row.Interest + row.Insurance) * loan.Rate);
            }
        }

        foreach (var f in i.Flows.Where(f => f.OccurredOn <= i.Today && f.OccurredOn <= resaleOn))
        {
            if (f.Kind == FlowKind.Income && PropertyCategories.CashIn.Contains(f.Category!))
                list.Add(new(f.OccurredOn, "rent", f.Amount.Amount));
            else if (f.Kind == FlowKind.Expense)
                list.Add(new(f.OccurredOn, f.Category == PropertyCategories.Works ? "works" : "charges", -f.Amount.Amount));
        }

        var lease = i.Tenancies.FirstOrDefault(t => PropertyMath.Covers(t, i.Today))
                    ?? i.Tenancies.OrderByDescending(t => t.EndsOn ?? LocalDate.MaxIsoValue).FirstOrDefault();
        var first = i.Today.PlusMonths(-12).PlusDays(1);
        var charges = i.Flows.Where(f => f.Kind == FlowKind.Expense && f.Category != PropertyCategories.Works && f.OccurredOn >= first && f.OccurredOn <= i.Today)
            .Sum(f => f.Amount.Amount) / 12;
        var pinelEnd = PinelEnd(i.Pinel);
        for (var m = new LocalDate(i.Today.Year, i.Today.Month, 1).PlusMonths(1); m < resaleOn; m = m.PlusMonths(1))
        {
            var years = Period.Between(i.Today, m, PeriodUnits.Years).Years;
            if (lease is not null)
            {
                var rent = m >= pinelEnd && a.RentAfterPinel is { } after ? after : lease.RentMonthly;
                list.Add(new(m, "rent", Cents((rent + lease.ChargesMonthly) * Grown(a.RentGrowth, years) * (12 - a.VacancyMonths) / 12)));
            }
            if (charges != 0m)
                list.Add(new(m, "charges", -Cents(charges * Grown(a.ChargesGrowth, years))));
        }

        // Each year's tax and Pinel reduction, paid with the balance of the year after.
        var carried = new List<(int Year, decimal Amount)>();
        for (var year = p.PurchaseDate.Year; year <= resaleOn.Year; year++)
        {
            var net = list.Where(f => f.On.Year == year && f.Line is "rent" or "charges" or "works").Sum(f => f.Amount) - loanCosts.GetValueOrDefault(year);
            carried.RemoveAll(d => d.Year < year - DeficitYears);
            if (net < 0m)
                carried.Add((year, -net));
            else
                for (var k = 0; k < carried.Count && net > 0m; k++)
                {
                    var used = Math.Min(net, carried[k].Amount);
                    net -= used;
                    carried[k] = (carried[k].Year, carried[k].Amount - used);
                }
            var paidOn = new LocalDate(year + 1, 7, 31);
            list.Add(new(paidOn, "rental_tax", -Cents(Math.Max(0m, net) * (a.MarginalRate + i.RentalSocialRate))));
            if (year < resaleOn.Year && i.Pinel?.Reductions.GetValueOrDefault(year) is > 0m and var reduction)
                list.Add(new(paidOn, "pinel", reduction));
        }
        list.Add(new(resaleOn, "resale", resalePrice));
        var fees = Cents(resalePrice * a.ResaleFees);
        if (fees > 0m)
            list.Add(new(resaleOn, "resale_fees", -fees));
        foreach (var loan in i.Loans.Where(l => l.Terms.DisbursedOn <= resaleOn))
        {
            var last = loan.Schedule.Rows.LastOrDefault(r => r.DueDate <= resaleOn);
            var balance = last?.Balance ?? loan.Terms.Principal;
            if (balance <= 0m)
                continue;
            var rate = (last ?? loan.Schedule.Rows.FirstOrDefault())?.AnnualRate ?? 0m;
            // Spec §4.2: the legal cap, art. L313-48 of the consumer code.
            list.Add(new(resaleOn, "loan_balance", -Cents(balance * loan.Rate)));
            list.Add(new(resaleOn, "penalty", -Cents(Math.Min(balance * rate / 2, 0.03m * balance) * loan.Rate)));
        }
        var gainsTax = i.CapitalGainsTax(resalePrice - fees, resaleOn);
        if (gainsTax > 0m)
            list.Add(new(resaleOn, "capital_gains_tax", -Cents(gainsTax)));
        if (i.Pinel is { } pinel && resaleOn < pinelEnd)
        {
            var initialEnd = pinel.StartYear + pinel.InitialYears;
            var from = resaleOn.Year < initialEnd ? pinel.StartYear : initialEnd + (resaleOn.Year - initialEnd) / 3 * 3;
            var clawback = pinel.Reductions.Where(r => r.Key >= from && r.Key < resaleOn.Year).Sum(r => r.Value);
            if (clawback > 0m)
                list.Add(new(resaleOn, "pinel_clawback", -clawback));
        }
        return list.OrderBy(f => f.On).ToList();
    }

    private static decimal Grown(decimal rate, int years)
    {
        var factor = 1m;
        for (var k = 0; k < years; k++)
            factor *= 1 + rate;
        return factor;
    }

    private static void Add(Dictionary<int, decimal> byYear, int year, decimal amount) => byYear[year] = byYear.GetValueOrDefault(year) + amount;

    private static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
