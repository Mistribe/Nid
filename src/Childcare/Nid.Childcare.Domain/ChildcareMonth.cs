using Nid.Childcare.Contracts;
using NodaTime;

namespace Nid.Childcare.Domain;

/// <summary>June only: the leave days acquired over the reference period (<see cref="PaidLeave.Accrued"/>), and its net pay (base − deductions + extra pay).</summary>
public sealed record PaidLeaveInput(int AccruedDays, decimal ReferenceNetPay);

/// <summary>
/// The contract's last month: why and when it was notified; the leave period still unpaid (since the last 1 June) — the earlier
/// months' days within it and their pay; the earlier months' hours due at their rate (<see cref="EndOfContract.HoursDue"/>) and their
/// mensualised pay after deductions; the earlier months' net pay; the gross-to-net hourly ratio, null when the contract has no gross rate.
/// </summary>
public sealed record EndingInput(
    EndReason Reason, LocalDate Notified, IReadOnlyList<StatementDay> LeaveDaysBefore, decimal LeavePayBefore, decimal DueBefore, decimal PaidBefore, decimal NetPayBefore,
    decimal? GrossRatio);

/// <summary>
/// A month to compute. <c>Days</c>: every day of the month; <c>NeighbourDays</c>: the days of the adjacent months that
/// share a week with it; <c>Cmg</c>: read from the Pajemploi summary; <c>Declared</c>: decision 30, the fields entered by hand;
/// <c>Contributions</c>: #30, the contributions left to the household on the Pajemploi summary; <c>Exemption</c>: the contributions
/// Pajemploi waived on the extra hours, from its summary, estimated while null; <c>TermsOn</c>: the terms in force on a day,
/// for the upkeep and meals of a month a revision takes effect in (<c>Terms</c> on every day while null).
/// </summary>
public sealed record MonthInput(
    YearMonth Month, ContractTerms Terms, WeekPlan Plan, IReadOnlyList<CareGap> Gaps,
    IReadOnlyList<StatementDay> Days, IReadOnlyList<StatementDay> NeighbourDays, int ChildSickUsedBefore,
    decimal Mileage, decimal Cmg, PaidLeaveInput? June, DeclaredFigures? Declared = null, decimal Contributions = 0m, decimal? Exemption = null,
    Func<LocalDate, ContractTerms>? TermsOn = null, EndingInput? Ending = null);

/// <summary>Childcare spec §3 and §6: a month's Pajemploi declaration, its cost and its childcare tax base. Pure.</summary>
public static class ChildcareMonth
{
    public static MonthFigures Compute(MonthInput input)
    {
        Validate(input);
        var annualHours = Mensualisation.AnnualHours(input.Plan.Types);
        var fullPay = Mensualisation.BasePay(input.Terms.NetHourlyRate, annualHours);

        // A month the contract enters or leaves deducts its template hours outside the contract, as an absence would.
        var inContract = input.Days.Where(d => StatementPreparation.InContract(d.Date, input.Terms)).ToList();
        var templateDaysInContract = inContract.Count(d => input.Plan.HoursOn(d.Date) > 0m);
        var templateDays = input.Days.Count(d => input.Plan.HoursOn(d.Date) > 0m);
        // Only template days make a month partial: a contract starting on Monday the 2nd loses nothing to the Sunday before.
        var partial = templateDaysInContract < templateDays;
        var potentialHours = input.Days.Sum(d => input.Plan.HoursOn(d.Date));
        var outsideHours = potentialHours - inContract.Sum(d => input.Plan.HoursOn(d.Date));
        var basePay = partial ? fullPay - Mensualisation.Round(fullPay * outsideHours / potentialHours) : fullPay;

        var (deduction, deductedDays, childSick) = AbsenceDeduction.Compute(fullPay, inContract, input.Plan, input.ChildSickUsedBefore, input.Terms.UnpaidAbsences, potentialHours);
        var (computedComplementary, computedOvertime) = ExtraHours.Compute(input.Month, [.. input.Days, .. input.NeighbourDays], input.Plan, input.Terms);
        var complementary = input.Declared?.ComplementaryHours ?? ExtraHours.Whole(computedComplementary);
        var overtime = input.Declared?.OvertimeHours ?? ExtraHours.Whole(computedOvertime);
        var extraPay = ExtraHours.Pay(input.Declared?.ComplementaryHours ?? computedComplementary, input.Declared?.OvertimeHours ?? computedOvertime,
            input.Terms.NetHourlyRate, input.Terms.OvertimeRate) + ExtraHours.HolidayPremium(inContract, input.Terms.NetHourlyRate);
        var (computedUpkeep, computedMeals) = Upkeep.Month(input.Days, input.TermsOn ?? (_ => input.Terms));
        var upkeep = input.Declared?.Upkeep ?? computedUpkeep;
        var meals = input.Declared?.Meals ?? computedMeals;

        var leaveDays = input.Declared?.PaidLeaveDays ?? input.June?.AccruedDays;
        // The contract's average week over its template weeks (38,389 h for 23 × 39 h + 22 × 37,75 h).
        var weeklyHours = annualHours / input.Plan.ProgrammedWeeks;
        var leavePay = input.June is { } j ? PaidLeave.JuneIndemnity(j.ReferenceNetPay, input.Terms.NetHourlyRate, weeklyHours, leaveDays!.Value) : 0m;

        // The last month settles the incomplete year (art. 124) and the leave not yet paid, as the higher of the tenth and maintained pay (art. 125).
        var (regularisation, balanceDays, leaveIndemnity) = (0m, 0, 0m);
        if (input.Ending is { } end)
        {
            var due = end.DueBefore + Mensualisation.Round(input.Terms.NetHourlyRate * EndOfContract.HoursDue(inContract, input.Plan, input.ChildSickUsedBefore, input.Terms.UnpaidAbsences));
            regularisation = due - end.PaidBefore - (basePay - deduction);
            balanceDays = PaidLeave.Accrued([.. end.LeaveDaysBefore, .. inContract], input.Plan, input.Terms.UnpaidAbsences);
            if (balanceDays > 0)
                leaveIndemnity = PaidLeave.JuneIndemnity(end.LeavePayBefore + basePay - deduction + extraPay + regularisation, input.Terms.NetHourlyRate, weeklyHours, balanceDays);
        }

        var computedPay = basePay - deduction + extraPay + leavePay + regularisation;
        var netPay = input.Declared?.NetPay ?? computedPay;
        // Decision 30: the difference goes to the extra pay, so that the parts still add up and June's reference pay follows.
        extraPay += netPay - computedPay;
        // The mensualised days, not the days really worked (spec §9, Pajemploi), less the deducted ones in the deduction's
        // own ratio, rounded up as the mensualised days are: 15 × 17 ÷ 18 = 14.17, so 15. A partial month declares the
        // template days its pay stands for, gap weeks included as in the pay, less the deducted ones. A day worked outside the plan comes on top of either.
        var mensualisedDays = Mensualisation.ActivityDays(input.Plan.Types);
        var unplannedDays = inContract.Count(d => d.Present && !d.Planned);
        var activityDays = input.Declared?.ActivityDays
            ?? unplannedDays + (partial ? Math.Max(0, templateDaysInContract - deductedDays)
                : deductedDays == 0 ? mensualisedDays
                : (int)Math.Ceiling((decimal)mensualisedDays * (templateDays - deductedDays) / templateDays));
        // #26: the hours the pay at the normal rate stands for, mensualised less deducted, extra hours apart: the hour
        // the extra minutes round up to is not taken back out of them (142.5 h and 40 min extra declare 143 h + 1 h).
        var normalHours = input.Declared?.NormalHours
            ?? Math.Max(0, Mensualisation.NormalHours(basePay - deduction + leavePay + regularisation, input.Terms.NetHourlyRate));
        // Art. 121: the indemnity's base is every salary of the contract, this month's and the leave indemnity included.
        var rupture = input.Ending is { } ending && EndOfContract.IndemnityDue(ending.Reason, input.Terms.Start, ending.Notified)
            ? EndOfContract.RuptureIndemnity(ending.NetPayBefore + netPay + leaveIndemnity,
                ending.GrossRatio ?? throw new ArgumentException("The breach indemnity needs the contract's gross hourly rate.", nameof(input)))
            : 0m;
        // The last declaration counts the leave days it settles (Pajemploi).
        var declaredLeaveDays = input.Declared?.PaidLeaveDays ?? (balanceDays > 0 ? (leaveDays ?? 0) + balanceDays : leaveDays);
        var declaration = new PajemploiDeclaration(normalHours, activityDays, declaredLeaveDays,
            complementary, overtime, netPay, upkeep, meals, input.Mileage, leaveIndemnity, rupture);
        // The declared net is before the exemption: Pajemploi adds it to what it pays the carer, so the household pays it too.
        var exemption = input.Exemption ?? ExtraHours.ExemptionEstimate(complementary, overtime, input.Terms.NetHourlyRate, input.Terms.OvertimeRate);
        // #30: the CMG covers the contributions only up to an hourly ceiling; past it the household pays the rest, read from the summary.
        var paid = input.Contributions + exemption;
        // The breach indemnity is not a salary (art. 121): in the cost, not in the childcare tax base.
        return new MonthFigures(declaration, basePay, deduction, extraPay, leavePay, input.Cmg,
            netPay + leaveIndemnity + rupture + upkeep + meals + input.Mileage + paid - input.Cmg, netPay + leaveIndemnity + upkeep + paid - input.Cmg, childSick,
            input.Contributions, exemption, regularisation);
    }

    /// <summary>
    /// No declaration from an incomplete or inconsistent month (spec §0): every day once, the plan as the template, gaps
    /// and contract dates say, hours on present days, a reason on absent planned ones, on planned ones only; the same for the neighbours.
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
            if (day.Absence is null ? day.Planned && !day.Present : !day.Planned)
                throw new ArgumentException($"{day.Date:uuuu-MM-dd}: a reason goes on a planned day, and an absent one needs it.", nameof(input));
        }
    }

    private static bool InMonth(LocalDate d, YearMonth month) => d.Year == month.Year && d.Month == month.Month;
}
