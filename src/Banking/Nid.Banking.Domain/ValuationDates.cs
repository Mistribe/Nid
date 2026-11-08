using NodaTime;

namespace Nid.Banking.Domain;

/// <summary>Spec §4.1: a bank account values its envelope once a month, plus today when the person syncs by hand.</summary>
public static class ValuationDates
{
    /// <summary>
    /// Month-ends in [<paramref name="associatedOn"/>, <paramref name="today"/>) with a balance stored in their month (the latest one of
    /// that month), after <paramref name="lastDone"/>; plus today when <paramref name="manual"/> and not done yet, with the latest balance.
    /// Oldest first.
    /// </summary>
    public static IReadOnlyList<(LocalDate On, LocalDate BalanceOn)> Due(
        LocalDate associatedOn, LocalDate? lastDone, IReadOnlyList<LocalDate> balanceDays, LocalDate today, bool manual)
    {
        var due = new List<(LocalDate, LocalDate)>();
        for (var monthEnd = associatedOn.With(DateAdjusters.EndOfMonth); monthEnd < today; monthEnd = monthEnd.PlusDays(1).With(DateAdjusters.EndOfMonth))
        {
            if (monthEnd <= lastDone)
                continue;
            var inMonth = balanceDays.Where(d => d.Year == monthEnd.Year && d.Month == monthEnd.Month && d <= monthEnd).ToList();
            if (inMonth.Count > 0)
                due.Add((monthEnd, inMonth.Max()));
        }

        // A second click the same day leaves today as the first left it (proposed, written, approved or kept as entered).
        if (manual && lastDone != today && balanceDays.Where(d => d <= today).ToList() is { Count: > 0 } known)
            due.Add((today, known.Max()));
        return due;
    }
}
