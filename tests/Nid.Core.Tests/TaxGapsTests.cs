using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Tax assessments spec §6: a notice against the estimate of its year.</summary>
public class TaxGapsTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static TaxAssessment Notice(
        decimal? netTax = null, decimal? taxable = null, decimal? reference = null, decimal? withholding = null, decimal? balance = null,
        decimal?[]? netSalaries = null, params Guid[] people) => new(
        2025, 2026, [.. people.Select((p, i) => new TaxAssessmentDeclarant(new PersonId(p), i + 1, null, null, null, netSalaries?[i], null, null, null, null, null, null))],
        Shares: null, ReferenceIncome: reference, AverageRate: null, MarginalRate: null, GrossIncome: null, TaxableIncome: taxable, CapitalIncomeDeclared: null,
        CapitalIncomeTaxable: null, RentalIncomeNet: null, FlatRateIncome: null, ExemptIncomeDeclared: null, ExemptIncomeNet: null, ScaleTax: null,
        ProportionalTax: null, TaxBeforeCredits: null, CreditsDeclared: null, CreditsRetained: null, NetTax: netTax, Withholding: withholding, Balance: balance,
        RentalDeficitCarried: null, DocumentId: null);

    private static TaxResult Result() => TaxCalculator.Compute(new TaxInputs(
        2025, [new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 50_000m)], 0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m));

    [Fact]
    public void Compares_net_tax_with_income_tax_plus_flat_tax()
    {
        var result = Result() with { IncomeTax = 6000m, CapitalFlatTax = 300m };

        var line = Assert.Single(TaxGaps.Of(Notice(netTax: 6412m, people: A), result, [A])!.Lines);

        Assert.Equal(new TaxGapLine("net_tax", 6412m, 6300m, 112m), line);
    }

    [Fact]
    public void Leaves_out_lines_the_notice_does_not_print() =>
        Assert.Empty(TaxGaps.Of(Notice(people: A), Result(), [A])!.Lines);

    [Fact]
    public void A_notice_with_other_declarants_has_no_gaps()
    {
        Assert.Null(TaxGaps.Of(Notice(netTax: 1m, people: [A, B]), Result(), [A]));
        Assert.Null(TaxGaps.Of(Notice(netTax: 1m, people: B), Result(), [A]));
    }

    [Fact]
    public void Compares_the_other_figures_line_by_line()
    {
        var result = Result();

        var lines = TaxGaps.Of(Notice(taxable: 45_010m, reference: 44_990m, withholding: 100m, balance: -5m, people: A), result, [A])!.Lines;

        Assert.Equal(
            [
                new TaxGapLine("taxable_income", 45_010m, result.NetTaxableIncome, 45_010m - result.NetTaxableIncome),
                new TaxGapLine("reference_income", 44_990m, result.ReferenceIncome, 44_990m - result.ReferenceIncome),
                new TaxGapLine("withholding", 100m, result.Withholding.Annual, 100m - result.Withholding.Annual),
                new TaxGapLine("balance", -5m, result.Withholding.Balance, -5m - result.Withholding.Balance),
            ], lines);
    }

    [Fact]
    public void Compares_each_declarants_net_salaries()
    {
        var result = TaxCalculator.Compute(new TaxInputs(
            2025, [new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 50_000m), new("B", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 30_000m)],
            0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m));

        var gaps = TaxGaps.Of(Notice(netSalaries: [45_100m, 27_000m], people: [A, B]), result, [A, B])!.Declarants;

        // 50 000 and 30 000 after the 10 % allowance; each notice figure meets its own declarant.
        Assert.Equal([new TaxDeclarantGap(A, 45_100m, 45_000m, 100m), new TaxDeclarantGap(B, 27_000m, 27_000m, 0m)], gaps);
    }

    [Fact]
    public void Unemployment_benefit_is_part_of_the_net_salaries()
    {
        var result = TaxCalculator.Compute(new TaxInputs(
            2025, [new("A", 0m, EmploymentStatus.NonExecutive, 4_000m, 0m, SalaryTaxable: 30_000m)], 0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m));

        // (30 000 + 4 000) less its 10 % allowance: the notice prints both together.
        var gap = Assert.Single(TaxGaps.Of(Notice(netSalaries: [30_600m], people: A), result, [A])!.Declarants);

        Assert.Equal(0m, gap.Gap);
    }
}
