using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Subscriptions.Domain;

/// <summary>The cost window a read reports over; defaults to the current calendar month.</summary>
public static class CostWindow
{
    public static DateRange Resolve(LocalDate today, LocalDate? from, LocalDate? to)
    {
        var start = from ?? new LocalDate(today.Year, today.Month, 1);
        var end = to ?? start.PlusMonths(1);
        return DateRange.Create(start, end).TryGetValue(out var range)
            ? range
            : DateRange.Create(start, start.PlusDays(1)).TryGetValue(out var fallback) ? fallback : default;
    }

    /// <summary>The annual cost window: 12 months from the 1st of the current month.</summary>
    public static DateRange Year(LocalDate today)
        => Resolve(today, null, new LocalDate(today.Year, today.Month, 1).PlusMonths(12));
}
