using NodaTime;

namespace Nid.Banking.Domain;

public enum Cadence { Monthly, Quarterly, Yearly }

public sealed record RecurringGroup(Guid Account, string Stem, Cadence Cadence, IReadOnlyList<Line> Lines);

/// <summary>Spec §6.4: unlinked debits that come back at a steady rhythm and amount.</summary>
public static class Recurrence
{
    private const decimal AmountTolerance = 0.10m;
    private const int RunningMarginDays = 10;
    private const int Months = 13;

    private static readonly (Cadence Cadence, int Min, int Max, int Count)[] Cadences =
    [
        (Cadence.Monthly, 25, 36, 3),
        (Cadence.Quarterly, 83, 99, 3),
        (Cadence.Yearly, 355, 375, 2),
    ];

    public static IReadOnlyList<RecurringGroup> Find(IReadOnlyList<Line> debits, LocalDate today)
    {
        var since = today.PlusMonths(-Months);
        var groups = new List<RecurringGroup>();
        foreach (var g in debits
            .Where(l => l.Stem.Length > 0 && l.BookedOn >= since)
            .GroupBy(l => (l.Account, l.Stem))
            .OrderBy(g => g.Key.Account).ThenBy(g => g.Key.Stem, StringComparer.Ordinal))
        {
            var lines = g.OrderBy(l => l.BookedOn).ThenBy(l => l.Id).ToList();
            var gaps = lines.Zip(lines.Skip(1), (a, b) => Period.Between(a.BookedOn, b.BookedOn, PeriodUnits.Days).Days).ToList();
            var median = Median(lines.Select(l => Math.Abs(l.Amount)).ToList());
            if (lines.Any(l => Math.Abs(Math.Abs(l.Amount) - median) > median * AmountTolerance)) continue;

            foreach (var (cadence, min, max, count) in Cadences)
                if (lines.Count >= count && gaps.All(d => d >= min && d <= max))
                {
                    if (Period.Between(lines[^1].BookedOn, today, PeriodUnits.Days).Days <= max + RunningMarginDays)
                        groups.Add(new RecurringGroup(g.Key.Account, g.Key.Stem, cadence, lines));
                    break;
                }
        }
        return groups;
    }

    private static decimal Median(List<decimal> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
