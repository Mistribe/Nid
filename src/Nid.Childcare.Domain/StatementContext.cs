using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>Childcare spec §1 decisions 12–16: what a month's statement draws from the arrangement and the other months. Pure.</summary>
public static class StatementContext
{
    /// <summary>The months a statement exists for, newest first: from the start (and the rules' first month) to today's month and the end's.</summary>
    public static IReadOnlyList<YearMonth> Months(LocalDate start, LocalDate? end, LocalDate today)
    {
        var first = Later(start.ToYearMonth(), ChildcareRules.FirstDay.ToYearMonth());
        var last = end is { } e && e.ToYearMonth().CompareTo(today.ToYearMonth()) < 0 ? e.ToYearMonth() : today.ToYearMonth();
        var months = new List<YearMonth>();
        for (var m = last; m.CompareTo(first) >= 0; m = m.PlusMonths(-1)) months.Add(m);
        return months;
    }

    /// <summary>Decision 12: the revision in force on the month's first day within the arrangement.</summary>
    public static int TermsIndexFor(YearMonth month, LocalDate start, IReadOnlyList<(LocalDate From, LocalDate? To)> terms)
    {
        var day = LocalDate.Max(month.OnDayOfMonth(1), start);
        var index = 0;
        for (var i = 0; i < terms.Count; i++)
            if (terms[i].From <= day) index = i;
        return index;
    }

    /// <summary>Decision 15: sick-child days deducted by the validated months of the same contract year, before this one.</summary>
    public static int ChildSickUsedBefore(LocalDate start, YearMonth month, IReadOnlyList<(YearMonth Month, int Deducted)> validated)
    {
        var year = ContractCheck.YearStart(start, month.OnDayOfMonth(1));
        return validated.Where(v => v.Month.CompareTo(month) < 0 && ContractCheck.YearStart(start, v.Month.OnDayOfMonth(1)) == year).Sum(v => v.Deducted);
    }

    /// <summary>
    /// Decision 16: the reference period June N−1 … May N within the arrangement's dates, with the validated months' pay
    /// (base − deductions + extra hours). No input while a month of it is not validated.
    /// </summary>
    public static (PaidLeaveInput? Input, IReadOnlyList<YearMonth> Missing) June(
        YearMonth june, LocalDate start, LocalDate? end, IReadOnlyList<(YearMonth Month, decimal ReferencePay)> validated)
    {
        var months = Enumerable.Range(1, 12).Select(i => june.PlusMonths(-13 + i)).Where(m => Overlaps(m, start, end)).ToList();
        // A month before the rules' first one has no statement: it counts as with care, without pay.
        var first = ChildcareRules.FirstDay.ToYearMonth();
        var missing = months.Where(m => m.CompareTo(first) >= 0 && validated.All(v => v.Month != m)).ToList();
        return missing.Count > 0
            ? (null, missing)
            : (new PaidLeaveInput(months.Count, validated.Where(v => months.Contains(v.Month)).Sum(v => v.ReferencePay)), missing);
    }

    /// <summary>Decision 16: days accrued in the current reference period, months within the dates up to today's.</summary>
    public static int LeaveDaysAccrued(LocalDate start, LocalDate? end, LocalDate today)
    {
        var from = new YearMonth(today.Month >= 6 ? today.Year : today.Year - 1, 6);
        var months = 0;
        for (var m = from; m.CompareTo(today.ToYearMonth()) <= 0; m = m.PlusMonths(1))
            if (Overlaps(m, start, end)) months++;
        return PaidLeave.Accrued(months);
    }

    /// <summary>
    /// Decision 19: the mean of the three latest validated months' CMG, to the cent; none until a validated month carries a
    /// CMG (#385: a 0 built from months without one would read « no CMG » rather than « not known yet »).
    /// </summary>
    public static decimal? CmgEstimate(IReadOnlyList<(YearMonth Month, decimal Cmg)> validated)
    {
        if (validated.All(v => v.Cmg == 0m))
            return null;
        return Mensualisation.Round(validated.OrderByDescending(v => v.Month).Take(3).Average(v => v.Cmg));
    }

    /// <summary>#30: the mean of the three latest validated months' contributions left to the household, to the cent; 0 without any.</summary>
    public static decimal ContributionsEstimate(IReadOnlyList<(YearMonth Month, decimal Contributions)> validated)
        => validated.Count == 0 ? 0m : Mensualisation.Round(validated.OrderByDescending(v => v.Month).Take(3).Average(v => v.Contributions));

    private static bool Overlaps(YearMonth month, LocalDate start, LocalDate? end)
        => month.OnDayOfMonth(1) <= (end ?? LocalDate.MaxIsoValue) && month.OnDayOfMonth(month.Calendar.GetDaysInMonth(month.Year, month.Month)) >= start;

    private static YearMonth Later(YearMonth a, YearMonth b) => a.CompareTo(b) >= 0 ? a : b;
}
