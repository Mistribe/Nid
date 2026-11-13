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
        public const string SummaryInvalid = "childcare.summary_invalid";
        public const string SummaryTooManyDays = "childcare.summary_too_many_days";
        public const string ScheduleDuplicateDay = "childcare.schedule_duplicate_day";
        public const string DepartureBeforeArrival = "childcare.departure_before_arrival";
        public const string WeeksOutOfRange = "childcare.programmed_weeks_out_of_range";
        public const string WeekTypesEmpty = "childcare.week_types_empty";
        public const string WeekTypeWeeksOutOfRange = "childcare.week_type_weeks_out_of_range";
        public const string WeekTypeNameInvalid = "childcare.week_type_name_invalid";
        public const string OverrideInvalidMonday = "childcare.override_invalid_monday";
        public const string OverrideUnknownWeekType = "childcare.override_unknown_week_type";
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

    /// <summary>The template weeks: a name of 1 to 40 characters, unique ignoring case, at least one week, a valid schedule each, 1 to 46 programmed weeks in all.</summary>
    public static IReadOnlyList<(string Field, string Code)> WeekTypes(IReadOnlyList<WeekType> types)
    {
        if (types.Count == 0) return [("weekTypes", Codes.WeekTypesEmpty)];
        var problems = new List<(string, string)>();
        for (var i = 0; i < types.Count; i++)
        {
            var name = types[i].Name?.Trim() ?? "";
            if (name.Length is 0 or > 40 || types.Take(i).Any(t => string.Equals(t.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                problems.Add(($"weekTypes[{i}].name", Codes.WeekTypeNameInvalid));
            if (types[i].Weeks < 1) problems.Add(($"weekTypes[{i}].weeks", Codes.WeekTypeWeeksOutOfRange));
            problems.AddRange(Schedule(types[i].Schedule).Select(p => ($"weekTypes[{i}].{p.Field}", p.Code)));
        }
        // long: a huge Weeks must not overflow the sum.
        if (types.Sum(t => (long)t.Weeks) is < 1 or > ChildcareRules.MaxProgrammedWeeks) problems.Add(("weekTypes", Codes.WeeksOutOfRange));
        return problems;
    }

    /// <summary>
    /// The exceptions: Mondays inside the contract, pointing at a rank the revision in force that week has (the last one starting
    /// by the Sunday of that week, else the first); a Monday listed twice is flagged at its later entry.
    /// </summary>
    public static IReadOnlyList<(string Field, string Code)> Overrides(IReadOnlyList<WeekOverride> overrides, LocalDate start, LocalDate? end,
        IReadOnlyList<(LocalDate From, int Types)> revisions)
    {
        var problems = new List<(string, string)>();
        for (var i = 0; i < overrides.Count; i++)
        {
            var monday = overrides[i].Monday;
            if (monday.DayOfWeek != IsoDayOfWeek.Monday || monday < WeekPlan.MondayOf(start) || (end is { } e && monday > e) || overrides.Take(i).Any(o => o.Monday == monday))
            {
                problems.Add(($"weekOverrides[{i}].monday", Codes.OverrideInvalidMonday));
                continue;
            }
            var types = revisions.Where(r => r.From <= monday.PlusDays(6)).Select(r => r.Types).DefaultIfEmpty(revisions[0].Types).Last();
            if (overrides[i].WeekType < 0 || overrides[i].WeekType >= types) problems.Add(($"weekOverrides[{i}].weekType", Codes.OverrideUnknownWeekType));
        }
        return problems;
    }

    /// <summary>The Mondays of the contract year containing <paramref name="today"/> (the first year's includes the Monday of the start week), with their week type, whether an exception sets it, and whether care is off.</summary>
    public static IReadOnlyList<(LocalDate Monday, int WeekType, bool Overridden, bool Gap)> Year(WeekPlan plan, LocalDate start,
        IReadOnlyList<CareGap> gaps, LocalDate today)
    {
        var year = YearStart(start, today);
        return [.. Enumerable.Range(0, 54).Select(WeekPlan.MondayOf(year).PlusWeeks).Where(m => YearStart(start, LocalDate.Max(m, start)) == year)
            .Select(m => (m, plan.TypeOf(m), plan.Overrides.Any(o => o.Monday == m), StatementPreparation.InGap(m, gaps)))];
    }

    /// <summary>The weeks with care per week type rank, one entry per rank.</summary>
    public static IReadOnlyList<int> Allocated(IReadOnlyList<(LocalDate Monday, int WeekType, bool Overridden, bool Gap)> year, int types)
        => [.. Enumerable.Range(0, types).Select(r => year.Count(w => w.WeekType == r && !w.Gap))];

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

    /// <summary>
    /// The contract years from <paramref name="start"/> to the one holding <paramref name="today"/> or the last gap, cut at <paramref name="end"/>:
    /// each with its weeks without care allowed (52 − programmed weeks under the terms of its first day) and placed (counted by Monday).
    /// </summary>
    public static IReadOnlyList<(LocalDate From, LocalDate To, int Allowed, int Placed)> GapYears(
        LocalDate start, LocalDate? end, Func<LocalDate, int> programmedWeeks, IReadOnlyList<CareGap> gaps, LocalDate today)
    {
        var mondays = gaps.Where(g => g.Weeks is > 0 and <= 52).SelectMany(Mondays).ToList();
        var last = mondays.Append(today).Max();
        if (end is { } e && e < last) last = e;
        var years = new List<(LocalDate, LocalDate, int, int)>();
        for (var k = 0; Anniversary(start, start.Year + k) <= last || k == 0; k++)
        {
            var (from, next) = (Anniversary(start, start.Year + k), Anniversary(start, start.Year + k + 1));
            var to = end is { } x && x < next ? x : next.PlusDays(-1);
            years.Add((from, to, Math.Max(0, 52 - programmedWeeks(from)), mondays.Count(m => m >= from && m < next)));
        }
        return years;
    }

    private static IEnumerable<LocalDate> Mondays(CareGap gap) => Enumerable.Range(0, gap.Weeks).Select(gap.Monday.PlusWeeks);

    /// <summary>The latest anniversary of <paramref name="start"/> on or before <paramref name="day"/>; 29 February falls on 28 February.</summary>
    public static LocalDate YearStart(LocalDate start, LocalDate day)
        => Anniversary(start, day.Year) <= day ? Anniversary(start, day.Year) : Anniversary(start, day.Year - 1);

    private static LocalDate Anniversary(LocalDate start, int year)
        => new(year, start.Month, Math.Min(start.Day, CalendarSystem.Iso.GetDaysInMonth(year, start.Month)));
}
