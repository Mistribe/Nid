using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class AbsenceDeductionTests
{
    private static IReadOnlyList<StatementDay> November(params (int Day, AbsenceReason Reason)[] absences)
        => Absent(StatementPreparation.Prepare(new YearMonth(2026, 11), Plan, Gaps), absences);

    internal static IReadOnlyList<StatementDay> Absent(IReadOnlyList<StatementDay> days, params (int Day, AbsenceReason Reason)[] absences)
        => [.. days.Select(d => absences.Any(a => a.Day == d.Date.Day)
            ? d with { Present = false, Arrival = null, Departure = null, Lunch = false, Absence = absences.First(a => a.Day == d.Date.Day).Reason }
            : d)];

    [Fact]
    public void No_absence() => Assert.Equal((0m, 0, 0), AbsenceDeduction.Compute(BasePay, November(), Plan, 0));

    [Fact]
    public void Two_carer_absences()
        => Assert.Equal((73.69m, 2, 0), AbsenceDeduction.Compute(BasePay, November((2, AbsenceReason.CarerAbsent), (3, AbsenceReason.CarerAbsent)), Plan, 0));

    [Fact]
    public void Child_sick_over_the_quota()
        => Assert.Equal((36.85m, 1, 1), AbsenceDeduction.Compute(BasePay, November((5, AbsenceReason.ChildSick), (6, AbsenceReason.ChildSick)), Plan, 4));

    [Fact]
    public void Paid_leave_is_paid_by_default()
        => Assert.Equal((0m, 0, 0), AbsenceDeduction.Compute(BasePay, November((2, AbsenceReason.PaidLeave)), Plan, 0));

    [Fact]
    public void The_contract_decides_which_reasons_are_unpaid()
        => Assert.Equal((36.85m, 1, 0), AbsenceDeduction.Compute(BasePay, November((2, AbsenceReason.Family), (5, AbsenceReason.ChildSick)), Plan, 0, [AbsenceReason.Family]));

    private static IReadOnlyList<StatementDay> ShortMonday(AbsenceReason reason)
        => [.. November().Select(d => d.Date.Day == 2 ? d with { Departure = new LocalTime(12, 45), Absence = reason } : d)];

    [Fact]
    public void A_shortened_day_deducts_its_missing_share()
        => Assert.Equal((18.42m, 0, 0), AbsenceDeduction.Compute(BasePay, ShortMonday(AbsenceReason.CarerAbsent), Plan, 0));

    [Fact]
    public void A_shortened_sick_day_takes_a_day_of_the_quota()
        => Assert.Equal((18.42m, 0, 1), AbsenceDeduction.Compute(BasePay, ShortMonday(AbsenceReason.ChildSick), Plan, 0));

    [Fact]
    public void A_shortened_day_for_a_paid_reason_deducts_nothing()
        => Assert.Equal((0m, 0, 0), AbsenceDeduction.Compute(BasePay, ShortMonday(AbsenceReason.Family), Plan, 0));

    [Fact]
    public void Rtt_and_family_are_paid()
        => Assert.Equal((0m, 0, 0), AbsenceDeduction.Compute(BasePay, November((9, AbsenceReason.ParentRtt), (10, AbsenceReason.Family)), Plan, 0));

    [Fact]
    public void A_short_day_weighs_its_own_hours()
    {
        var plan = Household(D(2026, 9, 1));
        var days = Absent(StatementPreparation.Prepare(new YearMonth(2026, 11), plan, []), (3, AbsenceReason.CarerAbsent));
        // The hours of that day over November's template hours, not one day of 17.
        var potential = days.Sum(d => plan.HoursOn(d.Date));
        Assert.Equal((Mensualisation.Round(698.20m * plan.HoursOn(D(2026, 11, 3)) / potential), 1, 0), AbsenceDeduction.Compute(698.20m, days, plan, 0));
        Assert.Equal(36.35m, AbsenceDeduction.Compute(698.20m, days, plan, 0).Deduction);
    }

    [Fact]
    public void Gap_days_stay_in_the_denominator()
    {
        var days = Absent(StatementPreparation.Prepare(new YearMonth(2026, 12), Plan, [new CareGap(D(2026, 12, 21), 1)]), (3, AbsenceReason.CarerAbsent));
        Assert.Equal((34.80m, 1, 0), AbsenceDeduction.Compute(BasePay, days, Plan, 0));
    }
}
