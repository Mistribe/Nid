using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>2E2 spec §4: the monthly routine's date, which envelopes are due, and the contributions a month's gain nets out.</summary>
public static class RoutineMath
{
    /// <summary>The statements' date: the last day of the month before <paramref name="today"/>'s.</summary>
    public static LocalDate DefaultDate(LocalDate today) => new LocalDate(today.Year, today.Month, 1).PlusDays(-1);

    /// <summary>No valuation in the cadence's calendar period containing <paramref name="on"/>; a manual envelope is never due.</summary>
    public static bool IsDue(ValuationCadence cadence, IEnumerable<LocalDate> valuationDates, LocalDate on)
    {
        var start = cadence switch
        {
            ValuationCadence.Monthly => new LocalDate(on.Year, on.Month, 1),
            ValuationCadence.Quarterly => new LocalDate(on.Year, (on.Month - 1) / 3 * 3 + 1, 1),
            ValuationCadence.Yearly => new LocalDate(on.Year, 1, 1),
            _ => (LocalDate?)null,
        };
        if (start is not { } from) return false;
        var to = cadence switch
        {
            ValuationCadence.Monthly => from.PlusMonths(1),
            ValuationCadence.Quarterly => from.PlusMonths(3),
            _ => from.PlusYears(1),
        };
        return !valuationDates.Any(d => d >= from && d < to);
    }

    /// <summary>Opening and contributions minus withdrawals dated after <paramref name="after"/> (all when null) and on or before <paramref name="on"/>.</summary>
    public static decimal NetContributions(IEnumerable<Flow> flows, LocalDate? after, LocalDate on)
    {
        var window = flows.Where(f => (after is not { } a || f.OccurredOn > a) && f.OccurredOn <= on).ToList();
        return window.Where(f => f.Kind is FlowKind.Opening or FlowKind.Contribution).Sum(f => f.Amount.Amount)
            - window.Where(f => f.Kind == FlowKind.Withdrawal).Sum(f => f.Amount.Amount);
    }
}
