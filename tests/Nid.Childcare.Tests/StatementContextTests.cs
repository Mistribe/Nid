using Nid.Childcare.Domain;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

public class StatementContextTests
{
    private static YearMonth M(int y, int m) => new(y, m);

    /// <summary>November 16 to 30 is 15 days of 4 weeks: 6.54 months, not 7.</summary>
    [Fact]
    public void A_month_entered_midway_earns_its_share_of_leave()
    {
        var months = Enumerable.Range(0, 7).Select(i => (new YearMonth(2026, 11).PlusMonths(i), 100m)).ToList();
        var (input, missing) = StatementContext.June(new YearMonth(2027, 6), D(2026, 11, 16), null, months);

        Assert.Empty(missing);
        Assert.Equal(17, PaidLeave.Accrued(input!.MonthsWithCare));
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

    private static IReadOnlyList<(YearMonth, decimal)> SeptToMay(params YearMonth[] except)
        => [.. Enumerable.Range(0, 9).Select(i => M(2026, 9).PlusMonths(i)).Where(m => !except.Contains(m)).Select(m => (m, BasePay))];

    [Fact]
    public void June_reference_period()
    {
        var (input, missing) = StatementContext.June(M(2027, 6), D(2026, 9, 1), null, SeptToMay());
        Assert.Equal(new PaidLeaveInput(9, 5637.33m), input);
        Assert.Empty(missing);

        (input, missing) = StatementContext.June(M(2027, 6), D(2026, 9, 1), null, SeptToMay(M(2026, 12)));
        Assert.Null(input);
        Assert.Equal([M(2026, 12)], missing);
    }

    [Fact]
    public void June_before_the_rules_table()
    {
        // Months before the rules' first one (November 2024) cannot have a statement: they count as with care, without pay.
        IReadOnlyList<(YearMonth, decimal)> validated = [.. Enumerable.Range(0, 7).Select(i => (M(2024, 11).PlusMonths(i), BasePay))];
        var (input, missing) = StatementContext.June(M(2025, 6), D(2024, 9, 2), null, validated);
        Assert.Empty(missing);
        Assert.Equal(new PaidLeaveInput(9, 4384.59m), input);
    }

    [Fact]
    public void June_maintained_pay_wins()
    {
        var june = M(2027, 6);
        var (input, _) = StatementContext.June(june, D(2026, 9, 1), null, SeptToMay());
        var days = StatementPreparation.Prepare(june, Plan, [], Terms);
        var figures = ChildcareMonth.Compute(new MonthInput(june, Terms, Plan, [], days, [], 0, 0m, 0m, input));
        Assert.Equal(23, figures.Declaration.PaidLeaveDays);
        // 23 ÷ 6 × 38 h × 4,30 € beats the tenth of 5 637,33.
        Assert.Equal(626.37m, figures.PaidLeavePay);
    }

    [Fact]
    public void Leave_counter()
    {
        Assert.Equal(8, StatementContext.LeaveDaysAccrued(D(2026, 9, 1), null, D(2026, 11, 15)));
        Assert.Equal(3, StatementContext.LeaveDaysAccrued(D(2026, 9, 1), null, D(2027, 6, 2)));
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
