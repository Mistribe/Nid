using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Domain;

/// <summary>
/// Spreads a charge's cost basis over the days it covers, in whole units of <see cref="Unit"/>, so the
/// days tile the basis exactly (design §5). The extra units go to the first <c>HighDays</c> covered days,
/// taken in chronological order across all of the charge's periods.
/// </summary>
internal static class CostAllocator
{
    /// <summary>The allocation unit: Money's own scale, not the currency's minor unit.</summary>
    internal const decimal Unit = 0.0001m;

    public static CostAllocation Allocate(Money basis, IReadOnlyList<CoveragePeriod> coverage)
    {
        var days = 0;
        foreach (var period in coverage)
            days += period.Days;

        if (days <= 0 || basis.Amount == 0m)
            return new CostAllocation(basis, days, 0m, 0);

        var totalUnits = (long)decimal.Round(basis.Amount / Unit, 0);
        var perDayUnits = totalUnits / days;
        var highDays = (int)(totalUnits - perDayUnits * days);

        // C# truncates toward zero, so a negative basis can leave a negative remainder. The allocation model
        // needs R in [0, days): floor the quotient instead, which is identical for a non-negative basis.
        if (highDays < 0)
        {
            perDayUnits -= 1;
            highDays += days;
        }

        return new CostAllocation(basis, days, perDayUnits * Unit, highDays);
    }

    /// <summary>
    /// What the coverage period at <paramref name="coverageIndex"/> contributes to <paramref name="window"/>
    /// for its charge. Constant time in the window: the covered days are counted, and so are those among
    /// them that carry the extra unit. The caller passes the index rather than the period itself, because
    /// two periods of one charge may share their dates — an explicit grant is allowed to overlap another —
    /// and matching by value would then credit the second period with the first one's extra units. An index
    /// also survives a timeline that was cached and rehydrated, where reference identity is gone.
    /// </summary>
    public static decimal AmountIn(Charge charge, int coverageIndex, LocalDate windowStart, LocalDate? windowEnd)
    {
        var period = charge.Coverage[coverageIndex];
        var start = period.Start > windowStart ? period.Start : windowStart;
        var end = windowEnd is { } stop && stop < period.EndBefore ? stop : period.EndBefore;
        var days = Period.DaysBetween(start, end);
        if (days <= 0)
            return 0m;

        var allocation = charge.Allocation;
        var offset = OffsetOf(charge, coverageIndex) + Period.DaysBetween(period.Start, start);
        var high = Math.Clamp(allocation.HighDays - offset, 0, days);

        return days * allocation.PerDay + high * Unit;
    }

    /// <summary>Days of this charge's coverage that precede the period at <paramref name="coverageIndex"/>.</summary>
    private static int OffsetOf(Charge charge, int coverageIndex)
    {
        var offset = 0;
        for (var i = 0; i < coverageIndex; i++)
            offset += charge.Coverage[i].Days;

        return offset;
    }
}
