using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

/// <summary>A full year (année complète): the fixture's 38-hour week over 52 weeks, from 1 September 2026, without weeks off.</summary>
public class FullYearTests
{
    private static readonly ContractTerms FullTerms = Terms with { FullYear = true };

    private static readonly WeekPlan FullPlan = WeekPlan.For([new WeekType("Semaine type", 52, Schedule)], Terms.Start, []);

    private static MonthInput Input(YearMonth month, int? leaveLeft, PaidLeaveInput? june = null, params int[] leaveDays)
    {
        var days = AbsenceDeductionTests.Absent(StatementPreparation.Prepare(month, FullPlan, [], FullTerms), [.. leaveDays.Select(d => (d, AbsenceReason.PaidLeave))]);
        IReadOnlyList<StatementDay> neighbours = [.. StatementPreparation.Prepare(month.PlusMonths(-1), FullPlan, [], FullTerms), .. StatementPreparation.Prepare(month.PlusMonths(1), FullPlan, [], FullTerms)];
        return new MonthInput(month, FullTerms, FullPlan, [], days, neighbours, 0, 0m, 0m, june, LeaveLeft: leaveLeft);
    }

    /// <summary>IDCC 3239 art. 97: hourly rate × weekly hours × 52 ÷ 12 = 4,30 × 38 × 52 ÷ 12; Pajemploi: 164,67 h and 4 days × 52 ÷ 12, rounded up.</summary>
    [Fact]
    public void The_monthly_pay_spreads_52_weeks()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), 30));
        Assert.Equal(708.07m, figures.BasePay);
        Assert.Equal(new PajemploiDeclaration(165, 18, null, 0m, 0m, 708.07m, figures.Declaration.Upkeep, figures.Declaration.Meals, 0m), figures.Declaration);
    }

    /// <summary>Leave days within those accrued are paid in the monthly pay: nothing deducted, the month's days and hours declared whole.</summary>
    [Fact]
    public void Accrued_leave_is_not_deducted()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), 5, null, 2, 3));
        Assert.Equal(0m, figures.Deduction);
        Assert.Equal(708.07m, figures.Declaration.NetPay);
        Assert.Equal((165, 18), (figures.Declaration.NormalHours, figures.Declaration.ActivityDays));
        Assert.Equal(2, figures.PaidLeaveTaken);
        Assert.Null(figures.Declaration.PaidLeaveDays);
    }

    /// <summary>
    /// The first year: the leave beyond what is accrued is taken without pay. Two of three days past the one left: 708,07 × 19 h ÷ 161,5 h
    /// (17 template days of 9,5 h in November 2026).
    /// </summary>
    [Fact]
    public void Leave_beyond_the_accrued_days_is_deducted()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), 1, null, 2, 3, 5));
        Assert.Equal(83.30m, figures.Deduction);
        Assert.Equal(1, figures.PaidLeaveTaken);
        Assert.Equal(624.77m, figures.Declaration.NetPay);
    }

    /// <summary>The contract's unpaid reasons do not reach a full year's leave days, which only the accrued count limits.</summary>
    [Fact]
    public void Unpaid_reasons_do_not_deduct_accrued_leave()
    {
        var input = Input(new YearMonth(2026, 11), 5, null, 2);
        var figures = ChildcareMonth.Compute(input with { Terms = FullTerms with { UnpaidAbsences = [AbsenceReason.PaidLeave] } });
        Assert.Equal(0m, figures.Deduction);
    }

    /// <summary>June pays only the tenth's excess over the maintained pay already in the monthly pay, and declares no leave days.</summary>
    [Fact]
    public void June_adds_the_tenth_excess_only()
    {
        // 12 months: 30 days; maintained 30 × 38 × 4,30 ÷ 6 = 817,00; tenth of 8 400 = 840,00.
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2027, 6), 30, new PaidLeaveInput(12, 8400m)));
        Assert.Equal(23.00m, figures.PaidLeavePay);
        Assert.Equal(731.07m, figures.Declaration.NetPay);
        Assert.Null(figures.Declaration.PaidLeaveDays);
        Assert.Equal(0m, ChildcareMonth.Compute(Input(new YearMonth(2027, 6), 30, new PaidLeaveInput(12, 8000m))).PaidLeavePay);
    }

    [Fact]
    public void June_difference()
    {
        Assert.Equal(23.00m, PaidLeave.JuneDifference(8400m, 4.30m, 38m, 30));
        Assert.Equal(0m, PaidLeave.JuneDifference(6500m, 4.30m, 38m, 30));
    }

    /// <summary>A full year takes exactly 52 weeks, an incomplete one 46 at most; a full year has no weeks without care.</summary>
    [Fact]
    public void A_full_year_takes_52_weeks_and_no_gap()
    {
        IReadOnlyList<WeekType> Weeks(int n) => [new WeekType("Semaine type", n, Schedule)];
        Assert.Empty(ContractCheck.WeekTypes(Weeks(52), fullYear: true));
        Assert.Contains(("weekTypes", ContractCheck.Codes.WeeksOutOfRange), ContractCheck.WeekTypes(Weeks(46), fullYear: true));
        Assert.Contains(("weekTypes", ContractCheck.Codes.WeeksOutOfRange), ContractCheck.WeekTypes(Weeks(52)));
        Assert.Equal([("gaps", ContractCheck.Codes.GapsTooMany)], ContractCheck.Gaps(Terms.Start, 52, [new CareGap(D(2026, 12, 21), 1)]));
        Assert.Equal(0, ContractCheck.GapWeeksToPlace(Terms.Start, 52, [], D(2026, 11, 1)));
    }

    /// <summary>2,5 days a month with care, rounded up per reference period, up to the month before; the paid days come off.</summary>
    [Fact]
    public void Leave_accrues_month_by_month()
    {
        Assert.Equal(0, StatementContext.LeaveAccruedBefore(Terms.Start, null, new YearMonth(2026, 9)));
        Assert.Equal(8, StatementContext.LeaveAccruedBefore(Terms.Start, null, new YearMonth(2026, 12)));
        // September to May: 22,5 → 23; then June: 2,5 → 3.
        Assert.Equal(26, StatementContext.LeaveAccruedBefore(Terms.Start, null, new YearMonth(2027, 7)));
        Assert.Equal(5, StatementContext.LeaveLeft(Terms.Start, null, new YearMonth(2027, 1), [(new YearMonth(2026, 12), 5), (new YearMonth(2027, 1), 3)]));
        Assert.Equal(0, StatementContext.LeaveLeft(Terms.Start, null, new YearMonth(2026, 10), [(new YearMonth(2026, 9), 5)]));
    }
}
