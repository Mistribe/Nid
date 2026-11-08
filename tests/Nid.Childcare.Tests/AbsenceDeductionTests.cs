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
            ? d with { Present = false, Arrival = null, Departure = null, Meal = false, Absence = absences.First(a => a.Day == d.Date.Day).Reason }
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
    public void Rtt_and_family_are_paid()
        => Assert.Equal((0m, 0, 0), AbsenceDeduction.Compute(BasePay, November((9, AbsenceReason.ParentRtt), (10, AbsenceReason.Family)), Plan, 0));

    [Fact]
    public void A_short_day_weighs_as_much_as_a_long_one()
    {
        var plan = Household(D(2026, 9, 1));
        var days = Absent(StatementPreparation.Prepare(new YearMonth(2026, 11), plan, []), (3, AbsenceReason.CarerAbsent));
        // Art. 111, 46 weeks or fewer: one day of November's 17 template days, whatever its hours.
        Assert.Equal((Mensualisation.Round(698.20m / 17m), 1, 0), AbsenceDeduction.Compute(698.20m, days, plan, 0));
    }

    [Fact]
    public void Gap_days_stay_in_the_denominator()
    {
        var days = Absent(StatementPreparation.Prepare(new YearMonth(2026, 12), Plan, [new CareGap(D(2026, 12, 21), 1)]), (3, AbsenceReason.CarerAbsent));
        Assert.Equal((34.80m, 1, 0), AbsenceDeduction.Compute(BasePay, days, Plan, 0));
    }
}
