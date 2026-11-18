using NodaTime;

namespace Nid.Core.Domain.Projection;

/// <summary>Ranges spec §3: a point's 10th, 50th and 90th percentiles over the runs, nominal and in today's money.</summary>
public sealed record RangePoint(LocalDate On, decimal Low, decimal Median, decimal High, decimal LowReal, decimal MedianReal, decimal HighReal);

/// <summary>Ranges spec §3: the bands on the projection's grid and the share of runs (0 to 1) whose available savings last to the horizon.</summary>
public sealed record ProjectionRangesResult(int Runs, IReadOnlyList<RangePoint> Points, decimal Success);

/// <summary>
/// Ranges spec §3: the projection run <see cref="Runs"/> times with random yearly returns. Each calendar year of the grid draws one
/// standard normal z, shared by every class (one market factor); class c's year then grows by (1 + μ) × exp(σ z − σ² / 2), spread
/// evenly over its months, μ being the deterministic rate and σ the class's volatility. Inflation, incomes, loans, property values and
/// events stay as the deterministic projection has them. The draws come from a fixed seed: the same inputs give the same bands.
/// </summary>
public static class ProjectionRanges
{
    public const int Runs = 1000;
    public const int Seed = 9;

    public static ProjectionRangesResult Simulate(ProjectionInputs inputs, int runs = Runs, int seed = Seed)
    {
        var a = inputs.Assumptions;
        var rates = inputs.Rates
            ?? ProjectionRates.Resolve(a, null, inputs.Today, inputs.Months, inputs.Savings.Select(s => s.Class).Append(a.SavingsClass));
        var thisMonth = new YearMonth(inputs.Today.Year, inputs.Today.Month);
        var yearOf = Enumerable.Range(0, inputs.Months + 1).Select(k => thisMonth.PlusMonths(k).Year - thisMonth.Year).ToArray();

        // Drawn in order before the runs, so running them in parallel changes nothing.
        var random = new Random(seed);
        var shocks = new double[runs][];
        for (var run = 0; run < runs; run++)
        {
            shocks[run] = new double[yearOf[^1] + 1];
            for (var y = 0; y < shocks[run].Length; y++) shocks[run][y] = Normal(random);
        }

        // Only each point's two figures are kept per run, not the runs' whole projections (about 100 MB at 40 years).
        var count = inputs.Months + 1;
        var nominal = new decimal[count][];
        var real = new decimal[count][];
        for (var i = 0; i < count; i++) (nominal[i], real[i]) = (new decimal[runs], new decimal[runs]);
        var dates = new LocalDate[count];
        var lasted = 0;
        Parallel.For(0, runs, run =>
        {
            var classRates = rates.MonthlyClassRate.ToDictionary(c => c.Key, c =>
            {
                var sigma = (double)a.VolatilityOf(c.Key);
                if (sigma == 0) return c.Value;
                // A year's factor exp(σ z − σ² / 2) as twelve equal monthly factors.
                var monthly = shocks[run].Select(z => (decimal)Math.Exp((sigma * z - sigma * sigma / 2) / 12)).ToArray();
                var shocked = new decimal[c.Value.Count];
                for (var k = 1; k < shocked.Length; k++) shocked[k] = (1m + c.Value[k]) * monthly[yearOf[k]] - 1m;
                return (IReadOnlyList<decimal>)shocked;
            });
            var result = HouseholdProjection.Project(inputs with { Expenses0 = null, Rates = rates with { MonthlyClassRate = classRates } });
            for (var i = 0; i < count; i++)
            {
                var point = result.Points[i];
                (nominal[i][run], real[i][run]) = (point.Nominal, point.Real);
                if (run == 0) dates[i] = point.On;
            }
            if (result.SavingsExhaustedIn is null) Interlocked.Increment(ref lasted);
        });

        var points = Enumerable.Range(0, count).AsParallel().AsOrdered().Select(i =>
        {
            Array.Sort(nominal[i]);
            Array.Sort(real[i]);
            return new RangePoint(dates[i], Rank(nominal[i], 0.1), Rank(nominal[i], 0.5), Rank(nominal[i], 0.9),
                Rank(real[i], 0.1), Rank(real[i], 0.5), Rank(real[i], 0.9));
        }).ToList();
        return new ProjectionRangesResult(runs, points, (decimal)lasted / runs);
    }

    /// <summary>The nearest-rank percentile of sorted values: the ⌈p × n⌉-th.</summary>
    private static decimal Rank(decimal[] sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];

    /// <summary>A standard normal draw (Box–Muller).</summary>
    private static double Normal(Random random)
        => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
