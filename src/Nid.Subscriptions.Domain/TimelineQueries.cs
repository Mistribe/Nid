using NodaTime;
using Nid.Core.Contracts;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Domain;

/// <summary>
/// The questions a built timeline answers (design §3). Extension methods rather than members, so the
/// Contracts assembly stays free of logic.
/// </summary>
public static class TimelineQueries
{
    public static bool IsCoveredOn(this Timeline timeline, LocalDate date)
    {
        foreach (var period in timeline.Coverage)
            if (period.Contains(date))
                return true;

        return false;
    }

    /// <summary>End of the last coverage period: the first day no longer covered.</summary>
    public static LocalDate? ExpiresOn(this Timeline timeline)
    {
        LocalDate? last = null;
        foreach (var period in timeline.Coverage)
            if (last is not { } value || period.EndBefore > value)
                last = period.EndBefore;

        return last;
    }

    /// <summary>The next charge a running phase would emit. Purchases are payments, not forecasts.</summary>
    public static Charge? NextCharge(this Timeline timeline)
    {
        foreach (var charge in timeline.Charges)
            if (charge.IsProjected && charge.Origin is ChargeOrigin.FromPhase)
                return charge;

        return null;
    }

    /// <summary>Money that left the account inside the window, one total per currency.</summary>
    public static CostBreakdown CashCost(this Timeline timeline, DateRange window, bool includeProjected = true)
    {
        var totals = new Dictionary<Currency, decimal>();

        foreach (var charge in timeline.Charges)
        {
            if (!window.Contains(charge.Date))
                continue;

            if (charge.IsProjected && !includeProjected)
                continue;

            totals[charge.Price.Currency] = totals.GetValueOrDefault(charge.Price.Currency) + charge.Price.Amount;
        }

        return Breakdown(totals);
    }

    /// <summary>What the covered days inside the window actually cost, one total per currency.</summary>
    public static CostBreakdown AmortizedCost(this Timeline timeline, DateRange window, bool includeProjected = true)
    {
        var totals = new Dictionary<Currency, decimal>();

        foreach (var charge in timeline.Charges)
        {
            if (charge.IsProjected && !includeProjected)
                continue;

            if (charge.Allocation.Days == 0)
                continue;

            var currency = charge.Allocation.Basis.Currency;
            foreach (var period in charge.Coverage)
            {
                var amount = CostAllocator.AmountIn(charge, period, window.Start, window.EndBefore);
                if (amount != 0m)
                    totals[currency] = totals.GetValueOrDefault(currency) + amount;
            }
        }

        return Breakdown(totals);
    }

    /// <summary>Days covered by something other than a paid period: the value of promotions.</summary>
    public static int FreeDays(this Timeline timeline, DateRange window)
    {
        var days = 0;

        foreach (var period in timeline.Coverage)
        {
            if (!period.IsFree)
                continue;

            var start = period.Start > window.Start ? period.Start : window.Start;
            var end = window.EndBefore is { } stop && stop < period.EndBefore ? stop : period.EndBefore;
            var overlap = Period.DaysBetween(start, end);
            if (overlap > 0)
                days += overlap;
        }

        return days;
    }

    private static CostBreakdown Breakdown(Dictionary<Currency, decimal> totals)
    {
        if (totals.Count == 0)
            return CostBreakdown.Empty;

        var money = new List<Money>(totals.Count);
        foreach (var (currency, amount) in totals.OrderBy(t => t.Key.Code, StringComparer.Ordinal))
            if (Money.Create(decimal.Round(amount, Money.MaxDecimals), currency).TryGetValue(out var value))
                money.Add(value);

        return new CostBreakdown(money);
    }
}
