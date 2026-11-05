using Nid.Documents.Contracts;
using Nid.Documents.Domain;
using NodaTime;

namespace Nid.Documents.Tests;

/// <summary>Issue #414: the periods a rule still asks for.</summary>
public class ExpectedDocumentRulesTests
{
    private static readonly ExpectedRule Statement = ExpectedDocumentRules.BySubject[ExpectedDocumentRules.LifeInsurance];
    private static readonly ExpectedRule Deed = ExpectedDocumentRules.BySubject[ExpectedDocumentRules.Property];
    private static readonly HashSet<int> None = [];

    private static IReadOnlyList<int> Missing(ExpectedRule rule, LocalDate today, HashSet<int>? covered = null, HashSet<int>? dismissed = null, LocalDate? since = null)
        => ExpectedDocumentRules.Missing(rule, since, today, covered ?? None, dismissed ?? None);

    [Fact]
    public void Every_subject_has_a_known_role()
        => Assert.All(ExpectedDocumentRules.BySubject.Values, r => Assert.Contains(r.Role, DocumentRoles.All));

    [Fact]
    public void A_one_off_document_is_missing_until_one_is_held_or_dismissed()
    {
        var today = new LocalDate(2026, 11, 5);
        Assert.Equal([ExpectedDocumentRules.Once], Missing(Deed, today));
        Assert.Empty(Missing(Deed, today, covered: [ExpectedDocumentRules.Once]));
        Assert.Empty(Missing(Deed, today, dismissed: [ExpectedDocumentRules.Once]));
    }

    [Fact]
    public void A_yearly_document_is_asked_for_from_its_due_day_on()
    {
        // The statement of year Y comes in Y + 1.
        Assert.Equal([2024], Missing(Statement, new LocalDate(2026, 6, 30)));
        Assert.Equal([2025], Missing(Statement, new LocalDate(2026, 7, 1)));
    }

    [Fact]
    public void Without_any_document_only_the_latest_year_is_asked_for()
        => Assert.Equal([2025], Missing(Statement, new LocalDate(2026, 11, 5)));

    [Fact]
    public void From_the_oldest_year_held_every_year_left_is_asked_for()
        => Assert.Equal([2022, 2024], Missing(Statement, new LocalDate(2026, 11, 5), covered: [2021, 2023, 2025]));

    [Fact]
    public void A_dismissed_year_is_no_longer_asked_for()
        => Assert.Equal([2025], Missing(Statement, new LocalDate(2026, 11, 5), covered: [2023], dismissed: [2024]));

    [Fact]
    public void No_year_due_before_the_subject_started()
    {
        Assert.Empty(Missing(Statement, new LocalDate(2026, 11, 5), since: new LocalDate(2026, 1, 15)));
        Assert.Equal([2025], Missing(Statement, new LocalDate(2026, 11, 5), since: new LocalDate(2025, 6, 30)));
    }

    [Fact]
    public void A_document_dated_this_year_before_its_due_day_asks_for_nothing_older()
        => Assert.Empty(Missing(Statement, new LocalDate(2026, 3, 1), covered: [2026]));

    [Fact]
    public void A_document_covers_its_date_s_year_or_undated_the_year_its_upload_was_due_in()
    {
        var uploaded = new LocalDate(2026, 3, 10);
        Assert.Equal(2024, Statement.Due!.PeriodOf(new LocalDate(2024, 12, 31), uploaded));
        // An undated statement uploaded in March 2026 is the one of 2025.
        Assert.Equal(2025, Statement.Due.PeriodOf(null, uploaded));
        Assert.Equal(2026, ExpectedDocumentRules.BySubject[ExpectedDocumentRules.TaxYear].Due!.PeriodOf(null, uploaded));
    }

    [Theory]
    [InlineData(2025, 12, 31, 2025)]
    // The statement of 2025, dated early 2026 (before the 1 July due day), is the one of 2025.
    [InlineData(2026, 2, 10, 2025)]
    [InlineData(2026, 8, 1, 2026)]
    public void A_lagged_document_dated_before_its_due_day_covers_the_year_before(int year, int month, int day, int period)
    {
        var on = new LocalDate(year, month, day);
        Assert.Equal(period, Statement.Due!.PeriodOf(on, new LocalDate(2026, 10, 1)));
        // Undated, its upload date follows the same rule.
        Assert.Equal(period, Statement.Due.PeriodOf(null, on));
    }

    [Fact]
    public void A_tax_notice_covers_its_date_s_year_whatever_the_day()
        => Assert.Equal(2026, ExpectedDocumentRules.BySubject[ExpectedDocumentRules.TaxYear].Due!.PeriodOf(new LocalDate(2026, 2, 10), new LocalDate(2026, 3, 1)));
}
