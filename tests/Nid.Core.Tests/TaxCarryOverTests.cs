using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Tax assessments spec §5: what a notice hands to the next year's estimate.</summary>
public class TaxCarryOverTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private static TaxAssessmentDeclarant Declarant(Guid p, int rank, decimal? next = null, decimal? oldest = null, decimal? middle = null, decimal? latest = null)
        => new(new PersonId(p), rank, null, null, null, null, null, oldest, middle, latest, null, next);

    private static TaxAssessment Notice(int year, decimal? deficit = null, params TaxAssessmentDeclarant[] declarants) => new(
        year, year + 1, declarants,
        Shares: null, ReferenceIncome: null, AverageRate: null, MarginalRate: null, GrossIncome: null, TaxableIncome: null, CapitalIncomeDeclared: null,
        CapitalIncomeTaxable: null, RentalIncomeNet: null, FlatRateIncome: null, ExemptIncomeDeclared: null, ExemptIncomeNet: null, ScaleTax: null,
        ProportionalTax: null, TaxBeforeCredits: null, CreditsDeclared: null, CreditsRetained: null, NetTax: null, Withholding: null, Balance: null,
        RentalDeficitCarried: deficit, DocumentId: null);

    [Fact]
    public void Maps_a_joint_notice_to_the_next_years_profile()
    {
        var notice = Notice(2024, 1500m, Declarant(A, 1, next: 9000m, oldest: 100m, middle: 200m, latest: 300m), Declarant(B, 2, next: 7000m, oldest: 10m, middle: 20m, latest: 30m));

        var carry = TaxCarryOver.From(2025, [notice], [A, B])!;

        Assert.Equal(9000m, carry.PerCeilings[A]);
        Assert.Equal(7000m, carry.PerCeilings[B]);
        Assert.Equal(100m, carry.PerCeilingsExpiring[A]);
        Assert.Equal(10m, carry.PerCeilingsExpiring[B]);
        Assert.Equal(600m, carry.PerUnusedCeilings[A]);
        Assert.Equal(60m, carry.PerUnusedCeilings[B]);
        Assert.Equal(1500m, carry.RentalDeficitCarried);
    }

    [Fact]
    public void The_rental_deficit_sums_over_the_notices()
    {
        var carry = TaxCarryOver.From(2025, [Notice(2024, 1000m, Declarant(A, 1)), Notice(2024, 500m, Declarant(B, 1))], [A, B])!;

        Assert.Equal(1500m, carry.RentalDeficitCarried);
    }

    [Fact]
    public void Ignores_notices_of_other_years() =>
        Assert.Null(TaxCarryOver.From(2025, [Notice(2023, 1500m, Declarant(A, 1, next: 9000m)), Notice(2025, 1m, Declarant(A, 1, next: 1m))], [A]));

    [Fact]
    public void A_figure_the_notice_does_not_print_keeps_the_profile_value()
    {
        var carry = TaxCarryOver.From(2025, [Notice(2024, null, Declarant(A, 1, next: 9000m), Declarant(B, 2))], [A, B])!;
        var profile = TaxProfile.Empty with
        {
            PerCeilings = new Dictionary<Guid, decimal> { [A] = 1m, [B] = 5000m }, RentalDeficitCarried = 42m,
        };

        var applied = carry.Apply(profile);

        Assert.Equal(9000m, applied.PerCeilings![A]);
        Assert.Equal(5000m, applied.PerCeilings[B]);
        Assert.Equal(42m, applied.RentalDeficitCarried);
        Assert.Equal(1m, profile.PerCeilings![A]);
    }

    [Fact]
    public void The_rental_deficit_is_a_suggestion_the_profile_keeps_its_own()
    {
        var carry = TaxCarryOver.From(2025, [Notice(2024, 1500m, Declarant(A, 1), Declarant(B, 2))], [A, B])!;

        Assert.Equal(1500m, carry.RentalDeficitCarried);
        Assert.Equal(42m, carry.Apply(TaxProfile.Empty with { RentalDeficitCarried = 42m }).RentalDeficitCarried);
        Assert.Equal(0m, carry.Apply(TaxProfile.Empty).RentalDeficitCarried);
    }

    [Fact]
    public void Unused_ceilings_sum_only_what_is_printed()
    {
        var carry = TaxCarryOver.From(2025, [Notice(2024, null, Declarant(A, 1, middle: 200m), Declarant(B, 2))], [A, B])!;

        Assert.Equal(200m, carry.PerUnusedCeilings[A]);
        Assert.False(carry.PerUnusedCeilings.ContainsKey(B));
        Assert.Empty(carry.PerCeilingsExpiring);
    }

    [Fact]
    public void Declarants_outside_the_adults_are_ignored()
    {
        var carry = TaxCarryOver.From(2025, [Notice(2024, null, Declarant(A, 1, next: 9000m), Declarant(B, 2, next: 7000m))], [A])!;

        Assert.Equal([A], carry.PerCeilings.Keys);
    }

    [Fact]
    public void Nothing_to_carry_is_null() =>
        Assert.Null(TaxCarryOver.From(2025, [Notice(2024, null, Declarant(A, 1))], [A]));
}
