using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Issue #410: this year's estimate against last year's, line by line, worked by hand (2026 carries the 2025 scale).</summary>
public class TaxChangesTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static TaxResult Estimate(int year, decimal salary, int dependents = 0, decimal childcare = 0m) => TaxCalculator.Compute(new TaxInputs(
        year, [new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: salary)], dependents, dependents, 0m, 0m, 0m, null, 0m, 0m, 0m,
        childcare, 0m, 0m, 0m));

    private static TaxAssessment Notice(decimal? netSalaries, decimal? netTax, params Guid[] people) => new(
        2025, 2026, [.. people.Select((p, i) => new TaxAssessmentDeclarant(new PersonId(p), i + 1, null, null, null, netSalaries, null, null, null, null, null, null))],
        Shares: null, ReferenceIncome: null, AverageRate: null, MarginalRate: null, GrossIncome: null, TaxableIncome: null, CapitalIncomeDeclared: null,
        CapitalIncomeTaxable: null, RentalIncomeNet: null, FlatRateIncome: null, ExemptIncomeDeclared: null, ExemptIncomeNet: null, ScaleTax: null,
        ProportionalTax: null, TaxBeforeCredits: null, CreditsDeclared: null, CreditsRetained: null, NetTax: netTax, Withholding: null, Balance: null,
        RentalDeficitCarried: null, DocumentId: null);

    [Fact]
    public void A_raise_goes_through_this_years_scale()
    {
        // 36 000 → 45 000 after the allowance: 3 903.99 → 6 603.99 on the scale, so +2 700; the tax follows to the euro.
        var r = TaxChanges.Of(Estimate(2026, 50_000m), Estimate(2025, 40_000m), [], [A])!;

        Assert.Equal(new TaxChangeLine("salaries", A, 36_000m, 45_000m, 2_700m, false), Assert.Single(r.Lines));
        Assert.Equal((3_904m, false, 6_604m, 0m), (r.TaxBefore, r.TaxBeforeFromNotice, r.TaxNow, r.Remainder));
    }

    [Fact]
    public void Last_years_notice_wins_over_its_estimate()
    {
        var r = TaxChanges.Of(Estimate(2026, 50_000m), Estimate(2025, 40_000m), [Notice(30_000m, 2_500m, A)], [A])!;

        // 2 103.99 on 30 000: +4 500; the rest of the 4 104 of change is what the line does not explain.
        Assert.Equal(new TaxChangeLine("salaries", A, 30_000m, 45_000m, 4_500m, true), Assert.Single(r.Lines));
        Assert.Equal((2_500m, true, -396m), (r.TaxBefore, r.TaxBeforeFromNotice, r.Remainder));
    }

    [Fact]
    public void A_child_and_a_credit_lower_the_tax_sorted_by_effect()
    {
        // Half a share, capped by the family quotient at 1 807; the childcare credit 50 % of 2 000.
        var r = TaxChanges.Of(Estimate(2026, 50_000m, dependents: 1, childcare: 2_000m), Estimate(2025, 50_000m), [], [A])!;

        Assert.Equal(
            [new TaxChangeLine("shares", null, 1m, 1.5m, -1_807m, false), new TaxChangeLine("childcare", null, 0m, 1_000m, -1_000m, false)],
            r.Lines);
        Assert.Equal(0m, r.Remainder);
    }

    [Fact]
    public void Nothing_to_compare_without_an_estimate_or_a_notice_of_these_adults()
    {
        Assert.Null(TaxChanges.Of(Estimate(2025, 1_000m), null, [], [A]));
        Assert.Null(TaxChanges.Of(Estimate(2025, 1_000m), null, [Notice(1m, 1m, A, B)], [A]));
    }

    [Fact]
    public void A_notice_alone_gives_only_the_lines_it_prints()
    {
        var r = TaxChanges.Of(Estimate(2025, 50_000m), null, [Notice(30_000m, null, A)], [A])!;

        Assert.Equal("salaries", Assert.Single(r.Lines).Code);
        Assert.Null(r.TaxBefore);
        Assert.Null(r.Remainder);
    }
}
