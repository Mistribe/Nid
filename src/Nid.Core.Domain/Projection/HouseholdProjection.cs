using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Domain.Projection;

/// <summary>A dated event on the curve; kinds: loan_end, property_free, retirement, savings_exhausted.</summary>
public sealed record ProjectionMilestone(LocalDate On, string Kind, string Label, ItemId? Item);

/// <summary>
/// Projection spec §3.3: everything read and converted beforehand. <c>Income</c> and <c>RealEstate</c> have <c>Months + 1</c> entries
/// (index k is this month plus k; <c>RealEstate</c> empty means none); <c>MonthlySavings</c> is the resolved S₀.
/// </summary>
public sealed record ProjectionInputs(
    LocalDate Today, int Months, IReadOnlyList<SavingsSlice> Savings,
    IReadOnlyList<decimal> Income, IReadOnlyList<RealEstateMonth> RealEstate,
    decimal MonthlySavings, ProjectionAssumptions Assumptions, IReadOnlyList<ProjectionMilestone> Milestones);

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
    /// Point k (k ≥ 1) is the last day of this month plus k. Each slice grows at its class's monthly rate; the monthly savings
    /// S(k) — S₀, the income rise since today, the loan payments freed since today — go to a pot growing at the savings class's rate.
    /// </summary>
    public static HouseholdProjectionResult Project(ProjectionInputs inputs)
    {
        var a = inputs.Assumptions;
        var thisMonth = new YearMonth(inputs.Today.Year, inputs.Today.Month);
        var monthlyRate = inputs.Savings.Select(s => s.Class).Append(a.SavingsClass).Distinct().ToDictionary(c => c,
            c => (decimal)Math.Pow(1 + (double)a.ClassRates.GetValueOrDefault(c), 1.0 / 12) - 1m);

        RealEstateMonth At(int k) => inputs.RealEstate.Count == 0 ? new(thisMonth.PlusMonths(k), 0m, 0m, 0m) : inputs.RealEstate[k];

        var slices = inputs.Savings.ToList();
        var pot = 0m;
        YearMonth? exhausted = null;
        var points = new List<HouseholdPoint> { Point(inputs.Today, slices.Sum(s => s.Amount), At(0), 1m) };
        var milestones = new List<ProjectionMilestone>();

        for (var k = 1; k <= inputs.Months; k++)
        {
            var month = thisMonth.PlusMonths(k);
            var end = month.ToDateInterval().End;
            var re = At(k);

            slices = slices.Select(s => s with { Amount = s.Amount * (1m + monthlyRate[s.Class]) }).ToList();
            var monthlySavings = inputs.MonthlySavings + (inputs.Income[k] - inputs.Income[0])
                + (a.FreedPaymentsToSavings ? At(0).LoanPayments - re.LoanPayments : 0m);
            pot = pot * (1m + monthlyRate[a.SavingsClass]) + monthlySavings;

            if (exhausted is null && slices.Where(s => s.Liquidity == AllocationLiquidity.Available).Sum(s => s.Amount) + pot < 0m)
            {
                exhausted = month;
                milestones.Add(new(end, "savings_exhausted", "savings_exhausted", null));
            }

            points.Add(Point(end, slices.Sum(s => s.Amount) + pot, re, (decimal)Math.Pow(1 + (double)a.Inflation, k / 12.0)));
        }

        var last = points[^1].On;
        milestones.AddRange(inputs.Milestones.Where(m => m.On > inputs.Today && m.On <= last));
        return new(points, milestones.OrderBy(m => m.On).ThenBy(m => m.Kind, StringComparer.Ordinal).ToList(), exhausted);
    }
}
