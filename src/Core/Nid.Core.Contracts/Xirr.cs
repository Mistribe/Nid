using NodaTime;

namespace Nid.Core.Contracts;

/// <summary><c>investments-data-model.md</c> §6: the money-weighted annual return (XIRR, Actual/365) shared by envelopes and holdings.</summary>
public static class Xirr
{
    /// <summary>
    /// In percent at 0.1, money paid in negative and taken out (the final value included) positive; null when not meaningful (under
    /// 12 months from the first cash flow to <paramref name="at"/>, or cash flows all of one sign).
    /// </summary>
    public static decimal? AnnualPercent(IEnumerable<(LocalDate On, decimal Amount)> flows, LocalDate at)
    {
        var cash = flows.Where(c => c.Amount != 0).ToList();
        if (cash.Count < 2 || cash.All(c => c.Amount > 0) || cash.All(c => c.Amount < 0)) return null;
        var first = cash.Min(c => c.On);
        if (first.PlusYears(1) > at) return null;

        var rate = Solve(cash, first);
        return rate is null ? null : Math.Round((decimal)(rate.Value * 100), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The return over <paramref name="from"/> to <paramref name="to"/> itself, not annualised, in percent at 0.1 (money in negative, money
    /// out positive, the end value included): <c>(1 + r)^(days / 365) − 1</c> with <c>r</c> the solved rate. No 12-month floor; null when
    /// the cash flows are all of one sign or the solver brackets nothing.
    /// </summary>
    public static decimal? PeriodPercent(IEnumerable<(LocalDate On, decimal Amount)> flows, LocalDate from, LocalDate to)
    {
        var cash = flows.Where(c => c.Amount != 0).ToList();
        if (cash.Count < 2 || cash.All(c => c.Amount > 0) || cash.All(c => c.Amount < 0)) return null;
        var rate = Solve(cash, cash.Min(c => c.On));
        if (rate is null) return null;
        var growth = Math.Pow(1 + rate.Value, Period.DaysBetween(from, to) / 365.0) - 1;
        return Math.Round((decimal)(growth * 100), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>The annual rate (0.1 = 10 %) that zeroes the net present value of the cash flows at <paramref name="first"/>; null when it brackets nothing.</summary>
    private static double? Solve(List<(LocalDate On, decimal Amount)> cash, LocalDate first)
    {
        // The one floating-point calculation (§6): a non-integer power. Amounts stay decimal up to here.
        var points = cash.Select(c => (Years: Period.DaysBetween(first, c.On) / 365.0, Amount: (double)c.Amount)).ToList();
        double Npv(double rate) => points.Sum(p => p.Amount / Math.Pow(1 + rate, p.Years));
        double low = -0.9999, high = 100;
        if (Math.Sign(Npv(low)) == Math.Sign(Npv(high))) return null;
        for (var i = 0; i < 100 && high - low > 1e-9; i++)
        {
            var mid = (low + high) / 2;
            if (Math.Sign(Npv(mid)) == Math.Sign(Npv(low))) low = mid; else high = mid;
        }
        return (low + high) / 2;
    }
}
