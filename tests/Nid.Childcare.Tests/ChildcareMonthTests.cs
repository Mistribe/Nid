using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class ChildcareMonthWeekTypeTests
{
    private static readonly ContractTerms HouseholdTerms = Terms with { NetHourlyRate = 4.85m };

    private static MonthFigures Month(YearMonth month, WeekPlan plan, ContractTerms terms)
        => ChildcareMonth.Compute(new MonthInput(month, terms, plan, [], StatementPreparation.Prepare(month, plan, [], terms),
            [.. StatementPreparation.Prepare(month.PlusMonths(-1), plan, [], terms), .. StatementPreparation.Prepare(month.PlusMonths(1), plan, [], terms)],
            0, 0m, 0m, null));

    [Fact]
    public void A_partial_month_across_both_week_types()
    {
        var terms = HouseholdTerms with { Start = D(2026, 9, 16) };
        var figures = Month(new YearMonth(2026, 9), Household(terms.Start), terms);
        // From the 16th: Thu 17, Fri 18, Mon 21, Tue 22, Thu 24, Fri 25, Mon 28, Tue 29 = 8 of the month's 17 template days.
        Assert.Equal(Mensualisation.Round(698.20m * 8m / 17m), figures.BasePay);
        Assert.Equal(328.56m, figures.BasePay);
    }

    [Fact]
    public void The_household_month_declares_144_normal_hours()
    {
        var figures = Month(new YearMonth(2026, 11), Household(D(2026, 9, 1)), HouseholdTerms);
        Assert.Equal(144, figures.Declaration.NormalHours);
        Assert.Equal(698.20m, figures.BasePay);
    }
}

public class ChildcareMonthTests
{
    private static MonthInput Input(YearMonth month, Func<IReadOnlyList<StatementDay>, IReadOnlyList<StatementDay>>? edit = null, PaidLeaveInput? june = null)
    {
        var days = StatementPreparation.Prepare(month, Plan, Gaps);
        IReadOnlyList<StatementDay> neighbours = [.. StatementPreparation.Prepare(month.PlusMonths(-1), Plan, Gaps), .. StatementPreparation.Prepare(month.PlusMonths(1), Plan, Gaps)];
        return new MonthInput(month, Terms, Plan, Gaps, edit is null ? days : edit(days), neighbours, 0, 0m, 300m, june);
    }

    [Fact]
    public void November_all_present()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11)));
        Assert.Equal(new PajemploiDeclaration(146, 16, null, 0m, 0m, 626.37m, 70.21m, 54.40m, 0m), figures.Declaration);
        Assert.Equal(450.98m, figures.Cost);
        Assert.Equal(396.58m, figures.TaxBase);
    }

    /// <summary>#30, #31: the contributions the CMG leaves to the household (a month past the hourly ceiling) count in the cost and the tax base.</summary>
    [Fact]
    public void Contributions_left_to_the_household_count_in_the_cost()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11)) with { Contributions = 40m });
        Assert.Equal(626.37m, figures.Declaration.NetPay);
        Assert.Equal(40m, figures.Contributions);
        Assert.Equal(490.98m, figures.Cost);
        Assert.Equal(436.58m, figures.TaxBase);
    }

    [Fact]
    public void November_with_two_carer_absences()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11),
            days => AbsenceDeductionTests.Absent(days, (2, AbsenceReason.CarerAbsent), (3, AbsenceReason.CarerAbsent))));
        Assert.Equal(552.68m, figures.Declaration.NetPay);
        Assert.Equal(129, figures.Declaration.NormalHours); // 552.68 / 4.30
        Assert.Equal(16, figures.Declaration.ActivityDays);
        Assert.Equal(61.95m, figures.Declaration.Upkeep);
        Assert.Equal(48.00m, figures.Declaration.Meals);
    }

    [Fact]
    public void June_adds_paid_leave()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2027, 6), june: new PaidLeaveInput(12, 7516.44m)));
        Assert.Equal(30, figures.Declaration.PaidLeaveDays);
        Assert.Equal(817.00m, figures.PaidLeavePay);
        Assert.Equal(figures.BasePay + 817.00m, figures.Declaration.NetPay);
    }

    [Fact]
    public void Present_day_without_hours_is_refused()
    {
        var e = Assert.Throws<ArgumentException>(() => ChildcareMonth.Compute(Input(new YearMonth(2026, 11),
            days => [.. days.Select(d => d.Date.Day == 5 ? d with { Departure = null } : d)])));
        Assert.Contains("2026-11-05", e.Message);
    }

    [Fact]
    public void Absent_planned_day_without_reason_is_refused()
        => Assert.Throws<ArgumentException>(() => ChildcareMonth.Compute(Input(new YearMonth(2026, 11),
            days => [.. days.Select(d => d.Date.Day == 5 ? d with { Present = false, Arrival = null, Departure = null } : d)])));

    [Fact]
    public void Month_uses_contract_weeks()
    {
        var month = new YearMonth(2026, 11);
        var input = new MonthInput(month, Terms, Plan, [], StatementPreparation.Prepare(month, Plan, []), [], 0, 0m, 0m, null);
        Assert.Equal(BasePay, ChildcareMonth.Compute(input).BasePay);
    }

    [Fact]
    public void Missing_day_is_refused()
        => Assert.Throws<ArgumentException>(() => ChildcareMonth.Compute(Input(new YearMonth(2026, 11), days => [.. days.Skip(1)])));
}

public class ChildcareMonthValidationTests
{
    private static MonthInput Input(YearMonth month, IReadOnlyList<CareGap> gaps, Func<StatementDay, StatementDay> edit, Func<StatementDay, StatementDay>? editNeighbour = null, ContractTerms? terms = null)
    {
        terms ??= Terms;
        var days = StatementPreparation.Prepare(month, Plan, gaps, terms).Select(edit).ToList();
        var neighbours = StatementPreparation.Prepare(month.PlusMonths(1), Plan, gaps, terms).Select(editNeighbour ?? (d => d)).ToList();
        return new MonthInput(month, terms, Plan, gaps, days, neighbours, 0, 0m, 0m, null);
    }

    [Fact]
    public void Absence_on_an_unplanned_day_is_refused()
        => Assert.Throws<ArgumentException>(() => ChildcareMonth.Compute(Input(new YearMonth(2026, 12), [new CareGap(D(2026, 12, 21), 1)],
            d => d.Date.Day == 22 ? d with { Absence = AbsenceReason.CarerAbsent } : d)));

    [Fact]
    public void An_unpaid_reason_on_a_shortened_day_lowers_the_pay_and_keeps_the_activity_days()
    {
        var whole = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps, d => d));
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps,
            d => d.Date.Day == 5 ? d with { Departure = new LocalTime(12, 45), Absence = AbsenceReason.CarerAbsent } : d));
        Assert.Equal(18.42m, figures.Deduction);
        Assert.Equal(whole.Declaration.ActivityDays, figures.Declaration.ActivityDays);
    }

    [Fact]
    public void Planned_flag_must_match_the_template()
        => Assert.Throws<ArgumentException>(() => ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps,
            d => d.Date.Day == 11 ? d with { Planned = true } : d)));

    [Fact]
    public void Neighbour_day_without_hours_is_refused()
        => Assert.Throws<ArgumentException>(() => ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps, d => d,
            d => d.Date.Day == 3 ? d with { Departure = null } : d)));

    [Fact]
    public void Contract_starting_mid_month_is_prorated()
    {
        var terms = Terms with { Start = D(2026, 11, 16) };
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps, d => d, terms: terms));
        Assert.Equal(331.61m, figures.BasePay);
        Assert.Equal(9, figures.Declaration.ActivityDays);
        Assert.Equal(9 * 4.13m, figures.Declaration.Upkeep);
        Assert.Equal(77, figures.Declaration.NormalHours); // 331.61 / 4.30
    }

    [Fact]
    public void A_start_after_non_working_days_only_is_a_whole_month()
    {
        var whole = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps, d => d));
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps, d => d, terms: Terms with { Start = D(2026, 11, 2) }));
        Assert.Equal(whole.BasePay, figures.BasePay);
        Assert.Equal(whole.Declaration.NormalHours, figures.Declaration.NormalHours);
        Assert.Equal(whole.Declaration.ActivityDays, figures.Declaration.ActivityDays);
    }

    [Fact]
    public void A_gap_week_of_the_first_month_counts_in_its_days_as_in_its_pay()
    {
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), [new CareGap(D(2026, 11, 23), 1)], d => d, terms: Terms with { Start = D(2026, 11, 16) }));
        Assert.Equal(331.61m, figures.BasePay);
        Assert.Equal(9, figures.Declaration.ActivityDays);
    }

    [Fact]
    public void Contract_ending_mid_month_declares_the_hours_of_its_pay()
    {
        var terms = Terms with { End = D(2026, 11, 15) };
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps, d => d, terms: terms));
        Assert.Equal(294.76m, figures.BasePay);
        Assert.Equal(69, figures.Declaration.NormalHours); // 294.76 / 4.30
    }

    [Fact]
    public void A_deducted_day_of_a_partial_month_leaves_its_normal_hours()
    {
        var terms = Terms with { Start = D(2026, 11, 16) };
        var figures = ChildcareMonth.Compute(Input(new YearMonth(2026, 11), Gaps,
            d => d.Date.Day == 17 ? d with { Present = false, Arrival = null, Departure = null, Absence = AbsenceReason.CarerAbsent } : d, terms: terms));
        Assert.True(figures.Deduction > 0m);
        Assert.Equal((int)Math.Round((figures.BasePay - figures.Deduction) / 4.30m, MidpointRounding.AwayFromZero), figures.Declaration.NormalHours);
        Assert.True(figures.Declaration.NormalHours < 77);
    }

    [Fact]
    public void Days_before_the_contract_are_not_planned()
        => Assert.All(StatementPreparation.Prepare(new YearMonth(2026, 11), Plan, Gaps, Terms with { Start = D(2026, 11, 16) })
            .Where(d => d.Date.Day < 16), d => Assert.False(d.Planned));
}
