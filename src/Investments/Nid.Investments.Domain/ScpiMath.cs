using NodaTime;

namespace Nid.Investments.Domain;

/// <summary>Issue #180: an SCPI held directly. Its shares earn nothing during the entitlement delay (délai de jouissance).</summary>
public static class ScpiMath
{
    /// <summary>
    /// The day shares subscribed on <paramref name="subscribedOn"/> start earning: the first day of the month that follows the
    /// subscription's by <paramref name="delayMonths"/> full months (15 January, 3 months → 1 May), as the managers' bulletins say it.
    /// </summary>
    public static LocalDate EntitledFrom(LocalDate subscribedOn, int delayMonths)
        => new LocalDate(subscribedOn.Year, subscribedOn.Month, 1).PlusMonths(delayMonths + 1);

    /// <summary>Of several purchases, the next date to come on or after <paramref name="today"/>, else the last one passed; null without any.</summary>
    public static LocalDate? EntitledFrom(IReadOnlyList<LocalDate> purchases, int delayMonths, LocalDate today)
    {
        var dates = purchases.Select(p => EntitledFrom(p, delayMonths)).OrderBy(d => d).ToList();
        return dates.Count == 0 ? null : dates.FirstOrDefault(d => d >= today, dates[^1]);
    }
}
