using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>June only: months of the reference period with care, and its net pay (base − deductions + extra pay).</summary>
public sealed record PaidLeaveInput(int MonthsWithCare, decimal ReferenceNetPay);

/// <summary>
/// A month to compute. <c>Days</c>: every day of the month; <c>NeighbourDays</c>: the days of the adjacent months that
/// share a week with it; <c>Cmg</c>: read from the Pajemploi summary; <c>Declared</c>: decision 30, the fields entered by hand;
/// <c>Contributions</c>: #30, the contributions left to the household on the Pajemploi summary.
/// </summary>
public sealed record MonthInput(
    YearMonth Month, ContractTerms Terms, WeekPlan Plan, IReadOnlyList<CareGap> Gaps,
    IReadOnlyList<StatementDay> Days, IReadOnlyList<StatementDay> NeighbourDays, int ChildSickUsedBefore,
    decimal Mileage, decimal Cmg, PaidLeaveInput? June, DeclaredFigures? Declared = null, decimal Contributions = 0m);

/// <summary>Childcare spec §3 and §6: a month's Pajemploi declaration, its cost and its childcare tax base. Pure.</summary>
public static class ChildcareMonth
{
    public static MonthFigures Compute(MonthInput input)
    {
        Validate(input);
        var annualHours = Mensualisation.AnnualHours(input.Plan.Types);
        var fullPay = Mensualisation.BasePay(input.Terms.NetHourlyRate, annualHours);

        // A month the contract enters or leaves pays its share of the template hours (the deduction's own ratio, art. 111).
        var inContract = input.Days.Where(d => StatementPreparation.InContract(d.Date, input.Terms)).ToList();
        var partial = inContract.Count < input.Days.Count;
        var basePay = partial ? Mensualisation.Round(fullPay * inContract.Sum(d => input.Plan.HoursOn(d.Date)) / input.Days.Sum(d => input.Plan.HoursOn(d.Date))) : fullPay;

        var (deduction, deductedDays, childSick) = AbsenceDeduction.Compute(basePay, inContract, input.Plan, input.ChildSickUsedBefore);
        var (computedComplementary, computedOvertime) = ExtraHours.Compute(input.Month, [.. input.Days, .. input.NeighbourDays], input.Plan);
        var complementary = input.Declared?.ComplementaryHours ?? computedComplementary;
        var overtime = input.Declared?.OvertimeHours ?? computedOvertime;
        var extraPay = ExtraHours.Pay(complementary, overtime, input.Terms.NetHourlyRate, input.Terms.OvertimeRate);
        var (computedUpkeep, computedMeals) = Upkeep.Month(input.Days, input.Terms);
        var upkeep = input.Declared?.Upkeep ?? computedUpkeep;
        var meals = input.Declared?.Meals ?? computedMeals;

        var leaveDays = input.Declared?.PaidLeaveDays ?? (input.June is { } june ? PaidLeave.Accrued(june.MonthsWithCare) : null);
        // The contract's average week over its template weeks (38,389 h for 23 × 39 h + 22 × 37,75 h).
        var weeklyHours = annualHours / input.Plan.ProgrammedWeeks;
        var leavePay = input.June is { } j ? PaidLeave.JuneIndemnity(j.ReferenceNetPay, input.Terms.NetHourlyRate, weeklyHours, leaveDays!.Value) : 0m;

        var computedPay = basePay - deduction + extraPay + leavePay;
        var netPay = input.Declared?.NetPay ?? computedPay;
        // Decision 30: the difference goes to the extra pay, so that the parts still add up and June's reference pay follows.
        extraPay += netPay - computedPay;
        // The mensualised days, absences or not, as the household declares them (spec §9). A partial month declares its
        // planned days less the deducted ones instead.
        var activityDays = input.Declared?.ActivityDays
            ?? (partial ? Math.Max(0, input.Days.Count(d => d.Planned) - deductedDays) : Mensualisation.ActivityDays(input.Plan.Types));
        var declaration = new PajemploiDeclaration(input.Declared?.NormalHours ?? Mensualisation.NormalHours(annualHours), activityDays, leaveDays,
            complementary, overtime, netPay, upkeep, meals, input.Mileage);
        // #30: the CMG covers the contributions only up to an hourly ceiling; past it the household pays the rest, read from the summary.
        return new MonthFigures(declaration, basePay, deduction, extraPay, leavePay, input.Cmg,
            netPay + upkeep + meals + input.Mileage + input.Contributions - input.Cmg, netPay + upkeep + input.Contributions - input.Cmg, childSick, input.Contributions);
    }

    /// <summary>
    /// No declaration from an incomplete or inconsistent month (spec §0): every day once, the plan as the template, gaps
    /// and contract dates say, hours on present days, a reason exactly on absent planned ones; the same for the neighbours.
    /// </summary>
    private static void Validate(MonthInput input)
    {
        var expected = input.Month.Calendar.GetDaysInMonth(input.Month.Year, input.Month.Month);
        var dates = input.Days.Select(d => d.Date).ToHashSet();
        if (input.Days.Count != expected || dates.Count != expected || dates.Any(d => !InMonth(d, input.Month)))
            throw new ArgumentException($"The statement must list each day of {input.Month:uuuu-MM} once.", nameof(input));
        if (input.NeighbourDays.Any(d => InMonth(d.Date, input.Month)) || input.NeighbourDays.Select(d => d.Date).Distinct().Count() != input.NeighbourDays.Count)
            throw new ArgumentException($"Neighbour days lie outside {input.Month:uuuu-MM}, each once.", nameof(input));
        foreach (var day in input.Days.Concat(input.NeighbourDays))
        {
            if (day.Present && (day.Arrival is not { } a || day.Departure is not { } d || d <= a))
                throw new ArgumentException($"{day.Date:uuuu-MM-dd}: a present day needs an arrival before its departure.", nameof(input));
            if (day.Planned != StatementPreparation.IsPlanned(day.Date, input.Plan, input.Gaps, input.Terms))
                throw new ArgumentException($"{day.Date:uuuu-MM-dd}: planned does not match the template, the gaps and the contract dates.", nameof(input));
            if ((day.Absence is not null) != (day.Planned && !day.Present))
                throw new ArgumentException($"{day.Date:uuuu-MM-dd}: a reason goes on an absent planned day, and only there.", nameof(input));
        }
    }

    private static bool InMonth(LocalDate d, YearMonth month) => d.Year == month.Year && d.Month == month.Month;
}
