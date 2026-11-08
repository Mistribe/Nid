using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Domain.Projection;

/// <summary>
/// Inflation spec C16, D6: <c>Inflation[k]</c> is the cumulated inflation i(k) and <c>Income[k]</c> the income growth g(k), for
/// k = 0..months (both 1 at k = 0); <c>MonthlyClassRate[c][k]</c> is class c's rate over month k, for k = 1..months (index 0 unused).
/// </summary>
public sealed record ResolvedRates(
    IReadOnlyList<decimal> Inflation, IReadOnlyList<decimal> Income, IReadOnlyDictionary<string, IReadOnlyList<decimal>> MonthlyClassRate);

/// <summary>Inflation spec C16: the assumptions' yearly and long-term rates as monthly factors over the projection's grid.</summary>
public static class ProjectionRates
{
    /// <summary>
    /// Month k (this month plus k) takes its calendar year's rate: the yearly table's when set, else, for inflation in the current
    /// year, <paramref name="observedLast12"/> when given (D5; the caller passes it only for a EUR household), else the long-term rate.
    /// A past year never falls on the grid, so a stale entry is ignored. A monthly factor is (1 + r)^(1/12);
    /// i(k) = i(k − 1) × (1 + r)^(1/12), g(k) = i(k) × (1 + x)^(k/12) with x the income growth.
    /// </summary>
    public static ResolvedRates Resolve(
        ProjectionAssumptions a, decimal? observedLast12, LocalDate today, int months, IEnumerable<string> classes)
    {
        var thisMonth = new YearMonth(today.Year, today.Month);
        int Year(int k) => thisMonth.PlusMonths(k).Year;
        static decimal Monthly(decimal annual) => (decimal)Math.Pow(1 + (double)annual, 1.0 / 12);

        decimal InflationIn(int year) => a.InflationByYear is { } table && table.TryGetValue(year, out var typed) ? typed
            : year == today.Year && observedLast12 is { } observed ? observed : a.Inflation;

        var inflation = new decimal[months + 1];
        var income = new decimal[months + 1];
        inflation[0] = 1m;
        for (var k = 1; k <= months; k++) inflation[k] = inflation[k - 1] * Monthly(InflationIn(Year(k)));
        for (var k = 0; k <= months; k++) income[k] = inflation[k] * (decimal)Math.Pow(1 + (double)a.IncomeGrowth, k / 12.0);

        var classRates = classes.Distinct().ToDictionary(c => c, IReadOnlyList<decimal> (c) =>
        {
            var table = a.ClassRatesByYear?.GetValueOrDefault(c);
            var rates = new decimal[months + 1];
            for (var k = 1; k <= months; k++)
                rates[k] = Monthly(table is not null && table.TryGetValue(Year(k), out var typed) ? typed : a.ClassRates.GetValueOrDefault(c)) - 1m;
            return rates;
        });
        return new ResolvedRates(inflation, income, classRates);
    }
}
