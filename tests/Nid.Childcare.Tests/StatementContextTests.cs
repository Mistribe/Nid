using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class StatementContextTests
{
    private static YearMonth M(int y, int m) => new(y, m);

    /// <summary>The validated months' pay and template days.</summary>
    private static (YearMonth, decimal, IReadOnlyList<StatementDay>) Validated(YearMonth month, decimal pay, ContractTerms terms)
        => (month, pay, StatementPreparation.Prepare(month, Plan, [], terms));

    /// <summary>From Monday 16 November 2026: 28 weeks and the Monday 31 May, 17,66 days, so 18.</summary>
    [Fact]
    public void A_month_entered_midway_earns_its_weeks_of_leave()
    {
        var terms = Terms with { Start = D(2026, 11, 16) };
        var months = Enumerable.Range(0, 7).Select(i => Validated(M(2026, 11).PlusMonths(i), 100m, terms)).ToList();
        var (input, missing) = StatementContext.June(M(2027, 6), terms, Plan, [], months);

        Assert.Empty(missing);
        Assert.Equal(new PaidLeaveInput(18, 700m), input);
    }

    [Fact]
    public void Months_run_from_start_to_today()
    {
        Assert.Equal([M(2025, 11), M(2025, 10), M(2025, 9)], StatementContext.Months(D(2025, 9, 10), null, D(2025, 11, 3)));
        Assert.Equal([M(2025, 10), M(2025, 9)], StatementContext.Months(D(2025, 9, 10), D(2025, 10, 20), D(2025, 11, 3)));
        Assert.Equal(M(2024, 11), StatementContext.Months(D(2024, 1, 15), null, D(2025, 1, 3))[^1]);
    }

    [Fact]
    public void Months_to_prepare_are_the_ones_gone_by_not_validated()
    {
        HashSet<YearMonth> validated = [M(2025, 10)];
        Assert.Equal([M(2025, 11), M(2025, 9)], StatementContext.ToPrepare(D(2025, 9, 10), null, D(2025, 12, 3), validated));
        // The current month counts from its last day, as decision 18's notice.
        Assert.Equal([M(2025, 12), M(2025, 11), M(2025, 9)], StatementContext.ToPrepare(D(2025, 9, 10), null, D(2025, 12, 31), validated));
        Assert.Empty(StatementContext.ToPrepare(D(2025, 12, 1), null, D(2025, 12, 3), validated));
    }

    [Fact]
    public void Mid_month_revision_applies_next_month()
    {
        IReadOnlyList<(LocalDate, LocalDate?)> terms = [(D(2025, 9, 1), D(2026, 1, 14)), (D(2026, 1, 15), null)];
        Assert.Equal(0, StatementContext.TermsIndexFor(M(2026, 1), D(2025, 9, 1), terms));
        Assert.Equal(1, StatementContext.TermsIndexFor(M(2026, 2), D(2025, 9, 1), terms));
        Assert.Equal(0, StatementContext.TermsIndexFor(M(2025, 9), D(2025, 9, 10), [(D(2025, 9, 10), null)]));
    }

    [Fact]
    public void Sick_days_count_within_the_contract_year()
    {
        IReadOnlyList<(YearMonth, int)> validated = [(M(2025, 10), 2), (M(2026, 1), 3), (M(2026, 4), 1)];
        Assert.Equal(5, StatementContext.ChildSickUsedBefore(D(2025, 9, 1), M(2026, 3), validated));
        Assert.Equal(0, StatementContext.ChildSickUsedBefore(D(2025, 9, 1), M(2026, 9), validated));
    }

    [Fact]
    public void Sick_days_used_in_the_contract_year_of_today()
    {
        IReadOnlyList<(YearMonth, int)> validated = [(M(2025, 7), 4), (M(2025, 10), 2), (M(2026, 1), 1)];
        Assert.Equal(3, StatementContext.ChildSickUsed(D(2024, 9, 1), D(2026, 3, 10), validated));
        Assert.Equal(0, StatementContext.ChildSickUsed(D(2024, 9, 1), D(2026, 9, 10), validated));
    }

    private static IReadOnlyList<(YearMonth, decimal, IReadOnlyList<StatementDay>)> SeptToMay(params YearMonth[] except)
        => [.. Enumerable.Range(0, 9).Select(i => M(2026, 9).PlusMonths(i)).Where(m => !except.Contains(m)).Select(m => Validated(m, BasePay, Terms))];

    [Fact]
    public void June_reference_period()
    {
        // From Tuesday 1 September 2026: ¾ of a week, 38 weeks and the Monday 31 May, 24,38 days, so 25.
        var (input, missing) = StatementContext.June(M(2027, 6), Terms, Plan, [], SeptToMay());
        Assert.Equal(new PaidLeaveInput(25, 5637.33m), input);
        Assert.Empty(missing);

        (input, missing) = StatementContext.June(M(2027, 6), Terms, Plan, [], SeptToMay(M(2026, 12)));
        Assert.Null(input);
        Assert.Equal([M(2026, 12)], missing);
    }

    [Fact]
    public void June_before_the_rules_table()
    {
        // Months before the rules' first one (November 2024) cannot have a statement: their days follow the template, without pay.
        // From Monday 2 September 2024 to Saturday 31 May 2025: 39 weeks, 24,38 days, so 25.
        var terms = Terms with { Start = D(2024, 9, 2) };
        IReadOnlyList<(YearMonth, decimal, IReadOnlyList<StatementDay>)> validated = [.. Enumerable.Range(0, 7).Select(i => Validated(M(2024, 11).PlusMonths(i), BasePay, terms))];
        var (input, missing) = StatementContext.June(M(2025, 6), terms, Plan, [], validated);
        Assert.Empty(missing);
        Assert.Equal(new PaidLeaveInput(25, 4384.59m), input);
    }

    [Fact]
    public void June_maintained_pay_wins()
    {
        var june = M(2027, 6);
        var (input, _) = StatementContext.June(june, Terms, Plan, [], SeptToMay());
        var days = StatementPreparation.Prepare(june, Plan, [], Terms);
        var figures = ChildcareMonth.Compute(new MonthInput(june, Terms, Plan, [], days, [], 0, 0m, 0m, input));
        Assert.Equal(25, figures.Declaration.PaidLeaveDays);
        // 25 ÷ 6 × 38 h × 4,30 € beats the tenth of 5 637,33.
        Assert.Equal(680.83m, figures.PaidLeavePay);
    }

    [Fact]
    public void Leave_counter()
    {
        // ¾ of a week and 10 weeks to Sunday 15 November: 6,72 days, so 7.
        Assert.Equal(7, StatementContext.LeaveDaysAccrued(D(2026, 9, 1), null, D(2026, 11, 15), Plan, []));
        // A new period from 1 June: the Tuesday is ¼ of a week, so 1.
        Assert.Equal(1, StatementContext.LeaveDaysAccrued(D(2026, 9, 1), null, D(2027, 6, 2), Plan, []));
        // A week without care earns nothing.
        Assert.Equal(6, StatementContext.LeaveDaysAccrued(D(2026, 9, 1), null, D(2026, 11, 15), Plan, [new(D(2026, 10, 19), 2)]));
    }

    [Fact]
    public void Cmg_estimate_is_the_mean_of_the_last_three()
    {
        Assert.Equal(120.33m, StatementContext.CmgEstimate([(M(2025, 9), 100m), (M(2025, 10), 110m), (M(2025, 11), 120m), (M(2025, 12), 131m)]));
        Assert.Equal(100m, StatementContext.CmgEstimate([(M(2025, 9), 100m)]));
        Assert.Null(StatementContext.CmgEstimate([]));
    }

    [Fact]
    public void Cmg_estimate_waits_for_a_month_that_carries_a_cmg()
    {
        Assert.Null(StatementContext.CmgEstimate([(M(2025, 8), 0m)]));
        Assert.Null(StatementContext.CmgEstimate([(M(2025, 8), 0m), (M(2025, 9), 0m)]));
        // Once one did, a later month without CMG is a real 0 in the mean.
        Assert.Equal(50m, StatementContext.CmgEstimate([(M(2025, 8), 100m), (M(2025, 9), 0m)]));
    }

    [Fact]
    public void Contributions_estimate_is_the_mean_of_the_three_latest()
    {
        Assert.Equal(20m, StatementContext.ContributionsEstimate([(M(2025, 8), 90m), (M(2025, 9), 0m), (M(2025, 10), 30m), (M(2025, 11), 30m)]));
        Assert.Equal(0m, StatementContext.ContributionsEstimate([]));
    }

    [Fact]
    public void Rules_cover_2025() => Assert.Equal(4.22m, ChildcareRules.For(D(2025, 9, 1)).MinimumGuaranteed);
}
