using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §1.10 and §2: what a contract's schedule, programmed weeks and weeks without care must respect. Each
/// check returns its problems as (field path, code), empty when valid. Pure.
/// </summary>
public static class ContractCheck
{
    /// <summary>The same strings as <c>ChildcareCodes</c>; the Domain does not reference the codes' project.</summary>
    public static class Codes
    {
        public const string ScheduleEmpty = "childcare.schedule_empty";
        public const string ScheduleDuplicateDay = "childcare.schedule_duplicate_day";
        public const string DepartureBeforeArrival = "childcare.departure_before_arrival";
        public const string WeeksOutOfRange = "childcare.programmed_weeks_out_of_range";
        public const string GapNotMonday = "childcare.gap_not_monday";
        public const string GapWeeksOutOfRange = "childcare.gap_weeks_out_of_range";
        public const string GapOverlap = "childcare.gap_overlap";
        public const string GapsTooMany = "childcare.gaps_too_many";
        public const string DaysIncomplete = "childcare.days_incomplete";
        public const string AbsenceRequired = "childcare.absence_required";
        public const string AbsenceNotAllowed = "childcare.absence_not_allowed";
    }

    public static IReadOnlyList<(string Field, string Code)> Schedule(IReadOnlyList<WeekdaySlot> schedule)
    {
        if (schedule.Count == 0) return [("schedule", Codes.ScheduleEmpty)];
        var problems = new List<(string, string)>();
        for (var i = 0; i < schedule.Count; i++)
        {
            if (schedule.Take(i).Any(s => s.Day == schedule[i].Day)) problems.Add(($"schedule[{i}].day", Codes.ScheduleDuplicateDay));
            if (schedule[i].Departure <= schedule[i].Arrival) problems.Add(($"schedule[{i}].departure", Codes.DepartureBeforeArrival));
        }
        return problems;
    }

    public static IReadOnlyList<(string Field, string Code)> Weeks(int programmedWeeks)
        => programmedWeeks is >= 1 and <= ChildcareRules.MaxProgrammedWeeks ? [] : [("programmedWeeks", Codes.WeeksOutOfRange)];

    /// <summary>
    /// Per contract year (from each anniversary of <paramref name="start"/>), at most 52 − programmed weeks; a week counts in the
    /// year of its Monday. <paramref name="since"/>: only the years from the one containing it, the earlier ones were checked
    /// against the weeks then in force.
    /// </summary>
    public static IReadOnlyList<(string Field, string Code)> Gaps(LocalDate start, int programmedWeeks, IReadOnlyList<CareGap> gaps, LocalDate? since = null)
    {
        var problems = new List<(string, string)>();
        var seen = new HashSet<LocalDate>();
        for (var i = 0; i < gaps.Count; i++)
        {
            if (gaps[i].Monday.DayOfWeek != IsoDayOfWeek.Monday) problems.Add(($"gaps[{i}].monday", Codes.GapNotMonday));
            // Bounded before the weeks are expanded: a year holds 52 of them.
            if (gaps[i].Weeks is < 1 or > 52) problems.Add(($"gaps[{i}].weeks", Codes.GapWeeksOutOfRange));
            else if (!Mondays(gaps[i]).All(seen.Add)) problems.Add(($"gaps[{i}].monday", Codes.GapOverlap));
        }
        var from = since is { } s ? YearStart(start, s) : LocalDate.MinIsoValue;
        if (problems.Count == 0 && seen.GroupBy(m => YearStart(start, m)).Any(year => year.Key >= from && year.Count() > 52 - programmedWeeks))
            problems.Add(("gaps", Codes.GapsTooMany));
        return problems;
    }

    /// <summary>Weeks without care still to place in the contract year containing <paramref name="today"/>, never negative.</summary>
    public static int GapWeeksToPlace(LocalDate start, int programmedWeeks, IReadOnlyList<CareGap> gaps, LocalDate today)
    {
        var year = YearStart(start, today);
        var placed = gaps.Where(g => g.Weeks is > 0 and <= 52).SelectMany(Mondays).Count(m => YearStart(start, m) == year);
        return Math.Max(0, 52 - programmedWeeks - placed);
    }

    private static IEnumerable<LocalDate> Mondays(CareGap gap) => Enumerable.Range(0, gap.Weeks).Select(gap.Monday.PlusWeeks);

    /// <summary>The latest anniversary of <paramref name="start"/> on or before <paramref name="day"/>; 29 February falls on 28 February.</summary>
    public static LocalDate YearStart(LocalDate start, LocalDate day)
    {
        LocalDate On(int year) => new(year, start.Month, Math.Min(start.Day, CalendarSystem.Iso.GetDaysInMonth(year, start.Month)));
        return On(day.Year) <= day ? On(day.Year) : On(day.Year - 1);
    }
}
