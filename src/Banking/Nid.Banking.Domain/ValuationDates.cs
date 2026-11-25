using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>Spec §4.1: a bank account values its envelope once a month, plus today when the person syncs by hand.</summary>
public static class ValuationDates
{
    /// <summary>
    /// Month-ends in [<paramref name="associatedOn"/>, <paramref name="today"/>) with a balance stored in their month (the latest one of
    /// that month); plus today when <paramref name="manual"/>, with the latest balance. <c>On</c> is the date done (<c>last_valued_on</c>),
    /// <c>BalanceOn</c> the value's own date (#429: flows are counted up to it). A balance dated on or before <paramref name="lastDone"/>, or
    /// already due here, has nothing new to say: a second click the same day leaves today as the first left it. Oldest first.
    /// </summary>
    public static IReadOnlyList<(LocalDate On, LocalDate BalanceOn)> Due(
        LocalDate associatedOn, LocalDate? lastDone, IReadOnlyList<LocalDate> balanceDays, LocalDate today, bool manual)
    {
        var due = new List<(LocalDate, LocalDate)>();
        var done = lastDone;
        void Add(LocalDate on, IEnumerable<LocalDate> days)
        {
            if (days.Select(d => (LocalDate?)d).Max() is { } latest && (done is null || latest > done))
            {
                due.Add((on, latest));
                done = latest;
            }
        }

        for (var monthEnd = associatedOn.With(DateAdjusters.EndOfMonth); monthEnd < today; monthEnd = monthEnd.PlusDays(1).With(DateAdjusters.EndOfMonth))
            Add(monthEnd, balanceDays.Where(d => d.Year == monthEnd.Year && d.Month == monthEnd.Month && d <= monthEnd));
        if (manual)
            Add(today, balanceDays.Where(d => d <= today));
        return due;
    }
}
