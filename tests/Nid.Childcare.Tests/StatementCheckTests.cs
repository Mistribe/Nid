using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class StatementCheckTests
{
    private static readonly YearMonth Month = new(2026, 9);

    private static List<StatementDay> Prepared() => [.. StatementPreparation.Prepare(Month, Plan, [], Terms)];

    [Fact]
    public void Complete_prepared_month_has_no_problem() => Assert.Empty(StatementCheck.Days(Month, Prepared()));

    [Fact]
    public void Missing_day()
    {
        var days = Prepared();
        days.RemoveAt(3);
        Assert.Contains(("days", "childcare.days_incomplete"), StatementCheck.Days(Month, days));
    }

    [Fact]
    public void Departure_before_arrival()
    {
        var days = Prepared();
        days[0] = days[0] with { Departure = new LocalTime(7, 0) }; // Tuesday 1 September, planned
        Assert.Contains(("days[0].departure", "childcare.departure_before_arrival"), StatementCheck.Days(Month, days));
    }

    [Fact]
    public void Absent_planned_day_needs_a_reason()
    {
        var days = Prepared();
        days[0] = days[0] with { Present = false, Arrival = null, Departure = null };
        Assert.Contains(("days[0].absence", "childcare.absence_required"), StatementCheck.Days(Month, days));
    }

    [Fact]
    public void Reason_on_a_worked_planned_day_is_allowed()
    {
        var days = Prepared();
        days[0] = days[0] with { Absence = AbsenceReason.Family };
        Assert.Empty(StatementCheck.Days(Month, days));
    }

    [Fact]
    public void Reason_on_unplanned_day_refused()
    {
        var days = Prepared();
        days[1] = days[1] with { Absence = AbsenceReason.Family }; // Wednesday 2 September, not planned
        Assert.Contains(("days[1].absence", "childcare.absence_not_allowed"), StatementCheck.Days(Month, days));
    }
}
