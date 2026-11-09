using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

/// <summary>Childcare decision 29: care days and total hours laid over the template's days.</summary>
public class StatementSummaryTests
{
    private static readonly ContractTerms Terms = new(D(2025, 9, 1), null, 4.30m, 0.10m, 3.80m, 3.20m);
    private static readonly IReadOnlyList<StatementDay> Prepared = Enumerable.Range(1, 30)
        .Select(i => D(2025, 9, i))
        .Select(d => d.DayOfWeek < IsoDayOfWeek.Friday
            ? new StatementDay(d, true, true, new LocalTime(8, 0), new LocalTime(d.DayOfWeek == IsoDayOfWeek.Monday ? 18 : 17, 0), true, null)
            : new StatementDay(d, false, false, null, null, false, null))
        .ToList();

    private static int Planned => Prepared.Count(d => d.Present);
    private static decimal PlannedHours => Prepared.Where(d => d.Present).Sum(d => Mensualisation.Hours(d.Arrival!.Value, d.Departure!.Value));
    private static decimal Total(IReadOnlyList<StatementDay> days) => days.Where(d => d.Present).Sum(d => Mensualisation.Hours(d.Arrival!.Value, d.Departure!.Value));

    [Fact]
    public void The_month_as_planned_comes_out_as_the_template()
    {
        var (days, problems) = StatementSummary.Spread(Prepared, Terms, Planned, PlannedHours, null);
        Assert.Empty(problems);
        Assert.Equal(Prepared, days);
    }

    [Fact]
    public void Fewer_days_leave_the_last_planned_ones_absent_and_the_total_falls_to_the_minute()
    {
        var (days, problems) = StatementSummary.Spread(Prepared, Terms, 10, 91.1m, AbsenceReason.Family);
        Assert.Empty(problems);
        Assert.Equal(10, days.Count(d => d.Present));
        Assert.Equal(91m + 6m / 60m, Total(days), 6);
        Assert.Equal(Planned - 10, days.Count(d => d.Absence == AbsenceReason.Family));
        Assert.True(days.Last(d => d.Present).Date < days.First(d => d.Absence is not null).Date);
    }

    [Fact]
    public void Leftover_planned_days_need_a_reason()
        => Assert.Equal(ContractCheck.Codes.AbsenceRequired, StatementSummary.Spread(Prepared, Terms, 10, 90m, null).Problems.Single().Code);

    [Fact]
    public void More_days_than_planned_take_unplanned_weekdays_without_a_meal()
    {
        var (days, problems) = StatementSummary.Spread(Prepared, Terms, Planned + 2, 200m, null);
        Assert.Empty(problems);
        var extra = days.Where(d => d.Present && !d.Planned).ToList();
        Assert.Equal(2, extra.Count);
        Assert.All(extra, d => { Assert.Equal(IsoDayOfWeek.Friday, d.Date.DayOfWeek); Assert.False(d.Lunch); });
        Assert.Equal(200m, Total(days), 6);
    }

    [Theory]
    [InlineData(40, 300, ContractCheck.Codes.SummaryTooManyDays)]
    [InlineData(0, 10, ContractCheck.Codes.SummaryInvalid)]
    [InlineData(5, 0, ContractCheck.Codes.SummaryInvalid)]
    [InlineData(2, 40, ContractCheck.Codes.SummaryInvalid)]
    public void Impossible_summaries_are_refused(int careDays, int hours, string code)
        => Assert.Equal(code, StatementSummary.Spread(Prepared, Terms, careDays, hours, AbsenceReason.Family).Problems.Single().Code);
}
