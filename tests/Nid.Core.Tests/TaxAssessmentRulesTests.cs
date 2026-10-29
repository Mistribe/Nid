using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Tax assessments spec §4: the rules a notice satisfies before it is stored.</summary>
public class TaxAssessmentRulesTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> Adults = new HashSet<Guid> { A, B };

    private static TaxAssessmentDeclarant Declarant(Guid person, int rank, decimal? gross = null, decimal? deductionAmount = null, SalaryDeduction? deduction = null)
        => new(new PersonId(person), rank, gross, deduction, deductionAmount, null, null, null, null, null, null, null);

    private static TaxAssessment Notice(
        int year = 2023, int? issued = null, TaxAssessmentDeclarant[]? declarants = null, decimal? netTax = null, decimal? balance = null,
        decimal? averageRate = null)
        => new(year, issued ?? year + 1, declarants ?? [Declarant(A, 1)], null, null, averageRate, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, netTax, null, balance, null, null);

    private static string[] Paths(TaxAssessment a) => [.. TaxAssessmentRules.Check(a, 2026, Adults).Select(e => e.Path)];

    [Fact]
    public void A_minimal_notice_passes() => Assert.Empty(Paths(Notice()));

    [Theory]
    [InlineData(1999)]
    [InlineData(2026)]
    public void Year_outside_2000_to_last_year_is_refused(int year) => Assert.Contains("year", Paths(Notice(year)));

    [Fact]
    public void Issued_year_before_the_year_after_is_refused() => Assert.Equal(["issuedYear"], Paths(Notice(2023, 2023)));

    [Fact]
    public void Issued_year_after_this_year_is_refused()
    {
        Assert.Empty(Paths(Notice(2025, 2026)));
        Assert.Equal(["issuedYear"], Paths(Notice(2025, 2027)));
    }

    [Fact]
    public void Declarants_are_one_or_two_adults_with_distinct_ranks()
    {
        Assert.Equal(["declarants"], Paths(Notice(declarants: [])));
        Assert.Contains("declarants", Paths(Notice(declarants: [Declarant(A, 1), Declarant(B, 2), Declarant(A, 1)])));
        Assert.Equal(["declarants[1].rank"], Paths(Notice(declarants: [Declarant(A, 1), Declarant(B, 1)])));
        Assert.Equal(["declarants[1].personId"], Paths(Notice(declarants: [Declarant(A, 1), Declarant(A, 2)])));
        Assert.Equal(["declarants[0].personId"], Paths(Notice(declarants: [Declarant(Guid.NewGuid(), 1)])));
    }

    [Fact]
    public void Amounts_are_not_negative_except_the_balance()
    {
        Assert.Equal(["netTax"], Paths(Notice(netTax: -1m)));
        Assert.Equal(["declarants[0].grossSalaries"], Paths(Notice(declarants: [Declarant(A, 1, gross: -1m)])));
        Assert.Empty(Paths(Notice(balance: -312.00m)));
    }

    [Fact]
    public void The_detail_lines_are_not_negative()
    {
        var declarant = Declarant(A, 1) with { OvertimeExempt = -1m, Withholding = -1m, CreditsAdvance = -1m };
        var notice = Notice(declarants: [declarant]) with
        {
            DividendsEligible = -1m, CapitalIncomeAlreadyLevied = -1m, FlatRatePrepaidCredit = -1m, RentalDeficitOnRentalIncome = -1m,
            RentalDeficitOnGlobalIncome = -1m, PinelReduction = -1m, ChildcareExpenses = -1m,
        };
        Assert.Equal(
            ["declarants[0].overtimeExempt", "declarants[0].withholding", "declarants[0].creditsAdvance", "dividendsEligible", "capitalIncomeAlreadyLevied",
                "flatRatePrepaidCredit", "rentalDeficitOnRentalIncome", "rentalDeficitOnGlobalIncome", "pinelReduction", "childcareExpenses"],
            Paths(notice));
    }

    [Fact]
    public void Amounts_have_two_decimals_at_most() => Assert.Equal(["netTax"], Paths(Notice(netTax: 10.001m)));

    [Fact]
    public void Rates_are_percentages() => Assert.Equal(["averageRate"], Paths(Notice(averageRate: 101m)));

    [Fact]
    public void A_deduction_amount_needs_its_kind()
        => Assert.Equal(["declarants[0].deduction"], Paths(Notice(declarants: [Declarant(A, 1, deductionAmount: 4100m)])));
}
