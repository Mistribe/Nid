using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class ContractCheckTests
{
    private static readonly LocalDate Start = D(2026, 9, 1);

    [Fact]
    public void Empty_schedule() => Assert.Contains(("schedule", "childcare.schedule_empty"), ContractCheck.Schedule([]));

    [Fact]
    public void Two_slots_on_monday()
        => Assert.Contains(("schedule[1].day", "childcare.schedule_duplicate_day"), ContractCheck.Schedule([Schedule[0], Schedule[0]]));

    [Fact]
    public void Departure_before_arrival()
        => Assert.Contains(("schedule[0].departure", "childcare.departure_before_arrival"),
            ContractCheck.Schedule([Schedule[0] with { Departure = new LocalTime(7, 0) }]));

    [Fact]
    public void Fixture_schedule_is_valid() => Assert.Empty(ContractCheck.Schedule(Schedule));

    [Fact]
    public void Gap_not_on_monday()
        => Assert.Contains(("gaps[0].monday", "childcare.gap_not_monday"), ContractCheck.Gaps(Start, 46, [new CareGap(D(2027, 7, 27), 1)]));

    [Fact]
    public void Gap_of_no_week()
        => Assert.Contains(("gaps[0].weeks", "childcare.gap_weeks_out_of_range"), ContractCheck.Gaps(Start, 46, [new CareGap(D(2027, 7, 26), 0)]));

    [Fact]
    public void Overlapping_gaps()
        => Assert.Contains(("gaps[1].monday", "childcare.gap_overlap"),
            ContractCheck.Gaps(Start, 46, [new CareGap(D(2027, 7, 26), 2), new CareGap(D(2027, 8, 2), 1)]));

    [Fact]
    public void Six_gap_weeks_fit_46() => Assert.Empty(ContractCheck.Gaps(Start, 46, Gaps));

    [Fact]
    public void Seven_gap_weeks_in_a_year_refused()
        => Assert.Contains(("gaps", "childcare.gaps_too_many"), ContractCheck.Gaps(Start, 46, [.. Gaps, new CareGap(D(2027, 6, 7), 1)]));

    [Fact]
    public void Huge_gap_is_refused_before_expanding()
        => Assert.Contains(("gaps[0].weeks", "childcare.gap_weeks_out_of_range"), ContractCheck.Gaps(Start, 46, [new CareGap(D(2027, 1, 4), 2_000_000_000)]));

    [Fact]
    public void Past_contract_years_are_not_rechecked()
    {
        IReadOnlyList<CareGap> gaps = [new(D(2027, 2, 15), 12)]; // year 1, placed under a 40-week contract
        Assert.Contains(("gaps", "childcare.gaps_too_many"), ContractCheck.Gaps(Start, 46, gaps));
        Assert.Empty(ContractCheck.Gaps(Start, 46, gaps, since: D(2027, 9, 1)));
    }

    [Fact]
    public void Gap_across_the_anniversary()
    {
        IReadOnlyList<CareGap> gaps = [new(D(2027, 2, 15), 2), new(D(2027, 8, 23), 5)];
        Assert.Empty(ContractCheck.Gaps(Start, 46, gaps));
        Assert.Equal(2, ContractCheck.GapWeeksToPlace(Start, 46, gaps, D(2027, 1, 10)));
        Assert.Equal(3, ContractCheck.GapWeeksToPlace(Start, 46, gaps, D(2027, 10, 1)));
        Assert.Contains(("gaps", "childcare.gaps_too_many"), ContractCheck.Gaps(Start, 46, [.. gaps, new CareGap(D(2027, 6, 7), 3)]));
    }
}

public class ChildcareCodesTests
{
    [Fact]
    public void Codes_match_the_contract()
    {
        string Value(Type type, string name) => (string)type.GetField(name)!.GetValue(null)!;
        foreach (var field in typeof(ContractCheck.Codes).GetFields())
            Assert.Equal(Value(typeof(ContractCheck.Codes), field.Name), Value(typeof(ChildcareCodes), field.Name));
        Assert.Equal("childcare", ChildcareModule.Key);
        Assert.Equal("childminder", ChildcareKinds.Childminder);
    }

    private static WeekType T(string name, int weeks = 23) => new(name, weeks, Schedule);

    [Fact]
    public void No_week_type() => Assert.Contains(("weekTypes", "childcare.week_types_empty"), ContractCheck.WeekTypes([]));

    [Fact]
    public void Week_type_weeks_below_one()
        => Assert.Contains(("weekTypes[1].weeks", "childcare.week_type_weeks_out_of_range"), ContractCheck.WeekTypes([T("A"), T("B", 0)]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789012345678901234567890123456789X")]
    public void Week_type_name_invalid(string name)
        => Assert.Contains(("weekTypes[0].name", "childcare.week_type_name_invalid"), ContractCheck.WeekTypes([T(name)]));

    [Fact]
    public void Week_type_name_twice_ignoring_case()
        => Assert.Contains(("weekTypes[1].name", "childcare.week_type_name_invalid"), ContractCheck.WeekTypes([T("Longue"), T(" longue ")]));

    [Fact]
    public void Week_type_schedule_is_prefixed()
        => Assert.Contains(("weekTypes[0].schedule", "childcare.schedule_empty"), ContractCheck.WeekTypes([new WeekType("A", 5, [])]));

    [Fact]
    public void Programmed_weeks_sum_out_of_range()
        => Assert.Contains(("weekTypes", "childcare.programmed_weeks_out_of_range"), ContractCheck.WeekTypes([T("A", 30), T("B", 17)]));

    [Fact]
    public void Household_week_types_are_valid() => Assert.Empty(ContractCheck.WeekTypes([T("Longue"), T("Courte", 22)]));

    private static readonly IReadOnlyList<(LocalDate, int)> OneRevision = [(D(2026, 9, 2), 2)];

    [Fact]
    public void Override_not_on_monday()
        => Assert.Contains(("weekOverrides[0].monday", "childcare.override_invalid_monday"),
            ContractCheck.Overrides([new WeekOverride(D(2026, 9, 15), 0)], D(2026, 9, 2), null, OneRevision));

    [Fact]
    public void Override_outside_the_contract()
        => Assert.Equal(2, ContractCheck.Overrides([new WeekOverride(D(2026, 8, 24), 0), new WeekOverride(D(2027, 1, 11), 0)], D(2026, 9, 2), D(2027, 1, 5), OneRevision).Count);

    [Fact]
    public void Override_to_a_rank_the_revision_lacks()
        => Assert.Contains(("weekOverrides[0].weekType", "childcare.override_unknown_week_type"),
            ContractCheck.Overrides([new WeekOverride(D(2026, 9, 14), 2)], D(2026, 9, 2), null, OneRevision));

    [Fact]
    public void Override_checks_the_revision_in_force_that_week()
    {
        IReadOnlyList<(LocalDate, int)> revisions = [(D(2026, 9, 2), 2), (D(2027, 1, 1), 1)];
        Assert.Empty(ContractCheck.Overrides([new WeekOverride(D(2026, 12, 21), 1)], D(2026, 9, 2), null, revisions));
        Assert.Contains(("weekOverrides[0].weekType", "childcare.override_unknown_week_type"),
            ContractCheck.Overrides([new WeekOverride(D(2027, 1, 4), 1)], D(2026, 9, 2), null, revisions));
    }

    [Fact]
    public void Override_on_a_monday_already_listed()
        => Assert.Equal([("weekOverrides[1].monday", "childcare.override_invalid_monday")],
            ContractCheck.Overrides([new WeekOverride(D(2026, 9, 14), 0), new WeekOverride(D(2026, 9, 14), 1)], D(2026, 9, 2), null, OneRevision));

    [Fact]
    public void Year_lists_the_contract_year_and_counts_per_rank()
    {
        var start = D(2026, 9, 2);
        var today = D(2026, 11, 3);
        var year = ContractCheck.Year(Household(start), start, [], today);
        // A Wednesday start: the year holds the Monday of its first week (2026-08-31) as well, 53 rows.
        Assert.Equal(53, year.Count);
        Assert.Equal(D(2026, 8, 31), year[0].Monday);
        Assert.Equal(D(2027, 8, 30), year[^1].Monday);
        Assert.Equal([27, 26], ContractCheck.Allocated(year, 2));

        Assert.Equal([26, 26], ContractCheck.Allocated(ContractCheck.Year(Household(start), start, [new CareGap(D(2026, 9, 14), 1)], today), 2));

        var plan = Household(start) with { Overrides = [new WeekOverride(D(2026, 9, 21), 0)] };
        var withBoth = ContractCheck.Year(plan, start, [new CareGap(D(2026, 9, 14), 1)], today);
        Assert.Equal([27, 25], ContractCheck.Allocated(withBoth, 2));
        Assert.True(withBoth.Single(w => w.Monday == D(2026, 9, 21)).Overridden);
    }
}
