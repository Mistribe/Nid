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

    /// <summary>
    /// #19: the months to prepare, newest first: those gone by (the current one from its last day, as decision 18's notice) not validated.
    /// </summary>
    public static IReadOnlyList<YearMonth> ToPrepare(LocalDate start, LocalDate? end, LocalDate today, IReadOnlySet<YearMonth> validated)
    {
        var due = today.PlusDays(1).Month != today.Month ? today : today.ToYearMonth().OnDayOfMonth(1).PlusDays(-1);
        return Months(start, end, due).Where(m => !validated.Contains(m)).ToList();
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

    /// <summary>Decision 15: sick-child days deducted by the validated months of the contract year containing <paramref name="today"/>.</summary>
    public static int ChildSickUsed(LocalDate start, LocalDate today, IReadOnlyList<(YearMonth Month, int Deducted)> validated)
    {
        var year = ContractCheck.YearStart(start, today);
        return validated.Where(v => ContractCheck.YearStart(start, v.Month.OnDayOfMonth(1)) == year).Sum(v => v.Deducted);
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
            : (new PaidLeaveInput(months.Sum(m => CareShare(m, start, end)), validated.Where(v => months.Contains(v.Month)).Sum(v => v.ReferencePay)), missing);
    }

    /// <summary>Decision 16: days accrued in the current reference period, months within the dates up to today's.</summary>
    public static int LeaveDaysAccrued(LocalDate start, LocalDate? end, LocalDate today)
    {
        var from = new YearMonth(today.Month >= 6 ? today.Year : today.Year - 1, 6);
        var months = 0m;
        for (var m = from; m.CompareTo(today.ToYearMonth()) <= 0; m = m.PlusMonths(1))
            months += CareShare(m, start, end);
        return PaidLeave.Accrued(months);
    }

    /// <summary>
    /// A full year: the leave days accrued from the contract's start to the end of the month before <paramref name="month"/>,
    /// 2,5 a month with care per reference period, 30 at most each (IDCC 3239 socle commun art. 48.1.1). Leave may be taken
    /// as soon as it is accrued (Code du travail L3141-12).
    /// </summary>
    public static int LeaveAccruedBefore(LocalDate start, LocalDate? end, YearMonth month)
    {
        var accrued = 0;
        for (var from = new YearMonth(start.Month >= 6 ? start.Year : start.Year - 1, 6); from.CompareTo(month) < 0; from = from.PlusMonths(12))
        {
            var months = 0m;
            for (var m = from; m.CompareTo(from.PlusMonths(12)) < 0 && m.CompareTo(month) < 0; m = m.PlusMonths(1))
                months += CareShare(m, start, end);
            accrued += PaidLeave.Accrued(months);
        }
        return accrued;
    }

    /// <summary>
    /// A full year: the leave days still accrued before <paramref name="month"/>, less those the validated months before it paid.
    /// shortcut: leave not taken by the end of the next period is not dropped (it should be, by 31 May); upgrade when a household reports it.
    /// </summary>
    public static int LeaveLeft(LocalDate start, LocalDate? end, YearMonth month, IReadOnlyList<(YearMonth Month, int Taken)> validated)
        => Math.Max(0, LeaveAccruedBefore(start, end, month) - validated.Where(v => v.Month.CompareTo(month) < 0).Sum(v => v.Taken));

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

    /// <summary>A month's weight in the leave count: 1 when the contract covers it, else its days over the 4 weeks that earn a month's leave, at most 1.</summary>
    private static decimal CareShare(YearMonth month, LocalDate start, LocalDate? end)
    {
        if (!Overlaps(month, start, end)) return 0m;
        var first = LocalDate.Max(month.OnDayOfMonth(1), start);
        var length = month.Calendar.GetDaysInMonth(month.Year, month.Month);
        var last = LocalDate.Min(month.OnDayOfMonth(length), end ?? LocalDate.MaxIsoValue);
        var days = Period.DaysBetween(first, last) + 1;
        return days == length ? 1m : Math.Min(1m, days / 28m);
    }

    private static bool Overlaps(YearMonth month, LocalDate start, LocalDate? end)
        => month.OnDayOfMonth(1) <= (end ?? LocalDate.MaxIsoValue) && month.OnDayOfMonth(month.Calendar.GetDaysInMonth(month.Year, month.Month)) >= start;

    private static YearMonth Later(YearMonth a, YearMonth b) => a.CompareTo(b) >= 0 ? a : b;
}
