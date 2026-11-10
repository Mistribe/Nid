using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Domain.Projection;

/// <summary>A dated event on the curve; kinds: loan_end, property_free, retirement, savings_exhausted.</summary>
public sealed record ProjectionMilestone(LocalDate On, string Kind, string Label, ItemId? Item);

/// <summary>
/// An amount leaving an item's slices pro rata at the start of <paramref name="Month"/>, before growth; month 0 is today's point.
/// </summary>
public sealed record SliceWithdrawal(int Month, ItemId Item, decimal Amount);

/// <summary>
/// A scenario's monthly change over months [<paramref name="From"/>, <paramref name="To"/>), in money of month <c>From</c> (inflation spec
/// C17): an income change follows the income growth from there, g(k) ÷ g(From), a savings change the inflation, i(k) ÷ i(From).
/// </summary>
public sealed record ScheduledChange(int From, int To, decimal Amount, bool Income);

/// <summary>
/// Projection spec §3.3: everything read and converted beforehand. <c>Income</c> and <c>RealEstate</c> have <c>Months + 1</c> entries
/// (index k is this month plus k; <c>RealEstate</c> empty means none); <c>MonthlySavings</c> is the resolved S₀.
/// The optional event inputs default to none: <c>MonthlyAdjustments</c> and <c>CashFlows</c> have <c>Months + 1</c> entries;
/// <c>MonthlyAdjustments[k]</c> is added to S(k), <c>CashFlows[k]</c> (signed) enters the pot in month k after S(k); a withdrawal
/// beyond its item's slices empties them and the rest leaves the pot. Month 0 is today's point: its cash flows and withdrawals (an
/// event of this month, as RealEstate applies it to month 0) are in that point; <c>MonthlyAdjustments[0]</c> is not read, no monthly
/// savings being added at today's point. <c>Income</c> is already indexed (inflation spec C17). <c>Rates</c> are the resolved monthly
/// rates; null resolves them from <c>Assumptions</c> with no observed inflation. <c>Changes</c> are added to S(k), indexed.
/// </summary>
public sealed record ProjectionInputs(
    LocalDate Today, int Months, IReadOnlyList<SavingsSlice> Savings,
    IReadOnlyList<decimal> Income, IReadOnlyList<RealEstateMonth> RealEstate,
    decimal MonthlySavings, ProjectionAssumptions Assumptions, IReadOnlyList<ProjectionMilestone> Milestones,
    IReadOnlyList<decimal>? MonthlyAdjustments = null, IReadOnlyList<decimal>? CashFlows = null,
    IReadOnlyList<SliceWithdrawal>? Withdrawals = null, ResolvedRates? Rates = null, IReadOnlyList<ScheduledChange>? Changes = null);

/// <summary>
/// A month's last day (the first point is today), nominal and real net worth with its parts, nominal then real (each part divided
/// by the same cumulated inflation as <c>Real</c>).
/// </summary>
public sealed record HouseholdPoint(
    LocalDate On, decimal Nominal, decimal Real, decimal Savings, decimal Property, decimal Debt,
    decimal SavingsReal, decimal PropertyReal, decimal DebtReal);

public sealed record HouseholdProjectionResult(
    IReadOnlyList<HouseholdPoint> Points, IReadOnlyList<ProjectionMilestone> Milestones, YearMonth? SavingsExhaustedIn);

/// <summary>Projection spec §3.3: the household net worth month by month, pure.</summary>
public static class HouseholdProjection
{
    private static decimal Cent(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>A point from its exact parts; <paramref name="deflator"/> is the cumulated inflation, rounding only here.</summary>
    private static HouseholdPoint Point(LocalDate on, decimal savings, RealEstateMonth re, decimal deflator)
    {
        var nominal = savings + re.PropertyValue - re.Debt;
        return new(on, Cent(nominal), Cent(nominal / deflator), Cent(savings), Cent(re.PropertyValue), Cent(re.Debt),
            Cent(savings / deflator), Cent(re.PropertyValue / deflator), Cent(re.Debt / deflator));
    }

    /// <summary>
    /// Point k (k ≥ 1) is the last day of this month plus k. Each slice grows at its class's rate for the month; the monthly savings
    /// S(k) = S₀ × i(k) + Income[k] − Income[0] × i(k) (spending follows inflation, C17), plus the loan payments freed since today and
    /// the scheduled changes, go to a pot growing at the savings class's rate. Real figures divide by i(k).
    /// </summary>
    public static HouseholdProjectionResult Project(ProjectionInputs inputs)
    {
        var a = inputs.Assumptions;
        var thisMonth = new YearMonth(inputs.Today.Year, inputs.Today.Month);
        var rates = inputs.Rates
            ?? ProjectionRates.Resolve(a, null, inputs.Today, inputs.Months, inputs.Savings.Select(s => s.Class).Append(a.SavingsClass));
        var monthlyRate = rates.MonthlyClassRate;
        var i = rates.Inflation;
        var g = rates.Income;

        RealEstateMonth At(int k) => inputs.RealEstate.Count == 0 ? new(thisMonth.PlusMonths(k), 0m, 0m, 0m) : inputs.RealEstate[k];

        // The slices as arrays: the ranges (ProjectionRanges) run this a thousand times, so no allocation per month.
        var slices = inputs.Savings;
        var amounts = slices.Select(s => s.Amount).ToArray();
        var sliceRates = slices.Select(s => monthlyRate[s.Class]).ToArray();
        var savingsRate = monthlyRate[a.SavingsClass];
        var available = slices.Select(s => s.Liquidity == AllocationLiquidity.Available).ToArray();
        // Retirement spec §2: a PER slice counts as available from the month its holder leaves work.
        var unlocksAt = slices.Select(s => s.UnlocksAt ?? int.MaxValue).ToArray();

        // The month's withdrawals leave their items' slices; what the slices cannot give is returned, to leave the pot.
        decimal Withdraw(int k)
        {
            var leftOver = 0m;
            foreach (var withdrawal in inputs.Withdrawals?.Where(w => w.Month == k) ?? [])
            {
                var held = 0m;
                for (var j = 0; j < amounts.Length; j++)
                    if (slices[j].Item == withdrawal.Item) held += amounts[j];
                var taken = Math.Min(withdrawal.Amount, Math.Max(held, 0m));
                if (held > 0m)
                    for (var j = 0; j < amounts.Length; j++)
                        if (slices[j].Item == withdrawal.Item) amounts[j] *= 1m - taken / held;
                leftOver += withdrawal.Amount - taken;
            }
            return leftOver;
        }

        decimal Held()
        {
            var sum = 0m;
            foreach (var amount in amounts) sum += amount;
            return sum;
        }

        var pot = -Withdraw(0) + (inputs.CashFlows?[0] ?? 0m);
        YearMonth? exhausted = null;
        var points = new List<HouseholdPoint>(inputs.Months + 1) { Point(inputs.Today, Held() + pot, At(0), 1m) };
        var milestones = new List<ProjectionMilestone>();

        for (var k = 1; k <= inputs.Months; k++)
        {
            var month = thisMonth.PlusMonths(k);
            var end = month.ToDateInterval().End;
            var re = At(k);

            var leftOver = Withdraw(k);

            for (var j = 0; j < amounts.Length; j++) amounts[j] *= 1m + sliceRates[j][k];
            var monthlySavings = inputs.MonthlySavings * i[k] + inputs.Income[k] - inputs.Income[0] * i[k]
                + (a.FreedPaymentsToSavings ? At(0).LoanPayments - re.LoanPayments : 0m)
                + (inputs.MonthlyAdjustments?[k] ?? 0m)
                + Changes(k);
            pot = (pot - leftOver) * (1m + savingsRate[k]) + monthlySavings + (inputs.CashFlows?[k] ?? 0m);

            if (exhausted is null && Available(k) + pot < 0m)
            {
                exhausted = month;
                milestones.Add(new(end, "savings_exhausted", "savings_exhausted", null));
            }

            points.Add(Point(end, Held() + pot, re, i[k]));
        }

        var last = points[^1].On;
        milestones.AddRange(inputs.Milestones.Where(m => m.On > inputs.Today && m.On <= last));
        return new(points, milestones.OrderBy(m => m.On).ThenBy(m => m.Kind, StringComparer.Ordinal).ToList(), exhausted);

        decimal Available(int k)
        {
            var sum = 0m;
            for (var j = 0; j < amounts.Length; j++)
                if (available[j] || unlocksAt[j] <= k) sum += amounts[j];
            return sum;
        }

        decimal Changes(int k)
        {
            var sum = 0m;
            foreach (var c in inputs.Changes ?? [])
                if (c.From <= k && k < c.To) sum += c.Income ? c.Amount * g[k] / g[c.From] : c.Amount * i[k] / i[c.From];
            return sum;
        }
    }
}
