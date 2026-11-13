using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>
/// Childcare spec §3: hours worked beyond each day's planned hours, complementary while the week's planned hours and
/// these stay within 45 h, overtime beyond. A week
/// belongs to the month of its Thursday, or the contract's first or last month when that Thursday lies outside the contract. Pure.
/// </summary>
public static class ExtraHours
{
    /// <summary><paramref name="days"/> covers every ISO week touching <paramref name="month"/>.</summary>
    public static (decimal Complementary, decimal Overtime) Compute(YearMonth month, IReadOnlyList<StatementDay> days, WeekPlan plan, ContractTerms? terms = null)
    {
        var reasons = terms?.UnpaidAbsences ?? AbsenceCodes.DefaultUnpaid;
        decimal complementary = 0m, overtime = 0m;
        foreach (var week in days.GroupBy(d => d.Date.PlusDays(1 - (int)d.Date.DayOfWeek)))
        {
            if (Owner(week.Key.PlusDays(3), terms) != month) continue;
            // The week's own plan, not a template's fixed hours: a paid absence stays planned, an unpaid one (or its missing hours) and a gap week do not.
            // A reason deducted whatever the contract (AbsenceDeduction) is never planned either.
            var planned = week.Where(d => d.Planned).Sum(d => d.Absence is { } r && (reasons.Contains(r) || AbsenceCodes.AlwaysUnpaid.Contains(r))
                ? d.Present ? Math.Min(plan.HoursOn(d.Date), Mensualisation.Hours(d.Arrival!.Value, d.Departure!.Value)) : 0m
                : plan.HoursOn(d.Date));
            // Day by day: an hour short on one day (paid, so not made up) does not offset an hour past the plan on another.
            var extra = week.Where(d => d.Present).Sum(d => Math.Max(0m,
                Mensualisation.Hours(d.Arrival!.Value, d.Departure!.Value) - (d.Planned ? plan.HoursOn(d.Date) : 0m)));
            var under = Math.Min(extra, Math.Max(0m, ChildcareRules.OvertimeThresholdHours - planned));
            complementary += under;
            overtime += extra - under;
        }
        return (complementary, overtime);
    }

    /// <summary>
    /// The whole hours Pajemploi is told: every started hour of the month, 3.5 h declared as 4 h, while the pay keeps the
    /// hours really worked. Rounded to the minute first so sums of thirds do not tip over a whole hour.
    /// </summary>
    public static decimal Started(decimal hours) => Math.Ceiling(Math.Round(hours * 60m) / 60m);

    public static decimal Pay(decimal complementary, decimal overtime, decimal rate, decimal overtimeRate)
        => Mensualisation.Round(complementary * rate + overtime * rate * (1m + overtimeRate));

    /// <summary>
    /// The salary contributions waived on the extra hours (code de la sécurité sociale, art. D241-21: at most 11,31 % of their gross pay),
    /// as a share of their net pay at the contract's rate. Pajemploi computes the exact amount from the declaration; its summary gives it.
    /// </summary>
    // shortcut: 14,9 % of the net matches Pajemploi to about 0,10 € a month; the summary's figure replaces it once entered.
    public const decimal ExemptionShareOfNet = 0.149m;

    /// <summary>The exemption Pajemploi will add for these declared extra hours, to the cent.</summary>
    public static decimal ExemptionEstimate(decimal complementary, decimal overtime, decimal rate, decimal overtimeRate)
        => Mensualisation.Round((complementary * rate + overtime * rate * (1m + overtimeRate)) * ExemptionShareOfNet);

    /// <summary>
    /// IDCC 3239: the hours worked on a public holiday are paid 10 % more, on 1 May 100 % more, on top of their pay in the
    /// mensualisation or as extra hours.
    /// </summary>
    public static decimal HolidayPremium(IEnumerable<StatementDay> days, decimal rate)
        => Mensualisation.Round(days.Where(d => d.Present && FrenchHolidays.In(d.Date.Year).Contains(d.Date))
            .Sum(d => Mensualisation.Hours(d.Arrival!.Value, d.Departure!.Value) * rate * (d.Date is { Month: 5, Day: 1 } ? 1m : 0.10m)));

    private static YearMonth Owner(LocalDate thursday, ContractTerms? terms)
    {
        if (terms is not null && thursday < terms.Start) return YearMonth(terms.Start);
        return terms?.End is { } end && thursday > end ? YearMonth(end) : YearMonth(thursday);
    }

    private static YearMonth YearMonth(LocalDate d) => new(d.Year, d.Month);
}
