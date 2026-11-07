using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare decision 29: a past month entered as its care days and total hours, laid over the template's days. The hours follow
/// the template's proportions, so a month as planned comes out as the template. Pure.
/// </summary>
public static class StatementSummary
{
    private static readonly LocalTime DefaultArrival = new(8, 0);

    /// <summary>The first <paramref name="careDays"/> working days present, unplanned weekdays after the planned ones; the other planned days absent for <paramref name="absence"/>.</summary>
    public static (IReadOnlyList<StatementDay> Days, IReadOnlyList<(string Field, string Code)> Problems) Spread(
        IReadOnlyList<StatementDay> prepared, ContractTerms terms, int careDays, decimal hours, AbsenceReason? absence)
    {
        var minutes = (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero);
        if (careDays < 0 || hours < 0 || (careDays == 0) != (minutes == 0))
            return (prepared, [("summary.hours", ContractCheck.Codes.SummaryInvalid)]);

        var planned = prepared.Where(d => d.Present).ToList();
        var chosen = planned.Take(careDays).ToList();
        var holidays = prepared.Select(d => d.Date.Year).Distinct().SelectMany(FrenchHolidays.In).ToHashSet();
        chosen.AddRange(prepared
            .Where(d => !d.Planned && d.Date.DayOfWeek < IsoDayOfWeek.Saturday && !holidays.Contains(d.Date) && StatementPreparation.InContract(d.Date, terms))
            .Take(careDays - chosen.Count));
        if (chosen.Count < careDays)
            return (prepared, [("summary.careDays", ContractCheck.Codes.SummaryTooManyDays)]);
        if (planned.Count > careDays && absence is null)
            return (prepared, [("summary.absence", ContractCheck.Codes.AbsenceRequired)]);

        // An unplanned day weighs as an average planned one, and starts at the first planned arrival.
        var average = planned.Count > 0 ? planned.Sum(Length) / planned.Count : 1;
        var arrival = planned.FirstOrDefault()?.Arrival ?? DefaultArrival;
        var weights = chosen.Select(d => d.Present ? Length(d) : average).ToList();
        var total = weights.Sum(w => (long)w);
        var lengths = new Dictionary<LocalDate, int>();
        long weighed = 0;
        var given = 0;
        for (var i = 0; i < chosen.Count; i++)
        {
            weighed += weights[i];
            // Cumulative rounding: the days add up to the total to the minute.
            var upTo = (int)Math.Round((decimal)minutes * weighed / total, MidpointRounding.AwayFromZero);
            lengths[chosen[i].Date] = upTo - given;
            given = upTo;
        }

        var days = new List<StatementDay>();
        foreach (var day in prepared)
        {
            if (lengths.TryGetValue(day.Date, out var length))
            {
                var from = day.Arrival ?? arrival;
                if (length <= 0 || from.Hour * 60 + from.Minute + length >= 24 * 60)
                    return (prepared, [("summary.hours", ContractCheck.Codes.SummaryInvalid)]);
                days.Add(day with { Present = true, Arrival = from, Departure = from.PlusMinutes(length) });
            }
            else
                days.Add(day.Present ? day with { Present = false, Arrival = null, Departure = null, Meal = false, Absence = absence } : day);
        }
        return (days, []);
    }

    private static int Length(StatementDay day) => (int)Math.Round(Mensualisation.Hours(day.Arrival!.Value, day.Departure!.Value) * 60m);
}
