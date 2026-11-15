using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>
/// Whole households on invented figures, each checked against the official simulator « Calcul de l'impôt 2026 sur les revenus 2025,
/// modèle simplifié » (simulateur-ir-ifi.impots.gouv.fr, run on 14 November 2026). The simulator prints euros: Nid's figures are
/// compared once rounded to the euro. The boxes it was given are in each scenario's comment, so a scenario can be run again.
/// </summary>
public class TaxReferenceScenariosTests
{
    /// <summary>What the simulator prints. <c>Rate</c>: the household withholding rate (« taux foyer »), null when it prints only individual ones.</summary>
    public sealed record Expected(decimal Parts, decimal Taxable, decimal Reference, decimal GrossTax, decimal Decote, decimal NetTax, decimal? Rate);

    private static TaxDeclarant Salaried(string name, decimal taxable) => new(name, 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: taxable);

    private static TaxInputs Household(IReadOnlyList<TaxDeclarant> declarants, int dependents = 0, int under6 = 0) => new(
        2025, declarants, dependents, under6, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    public static TheoryData<string, TaxInputs, Expected> Scenarios => new()
    {
        // Married, children born 2018 and 2022; 1AJ 42 000, 1BJ 31 000, 7GA 4 000 (the child under 6), 7UF 600.
        // Simulator: 396 of donations, 1 750 of childcare credit (the 3 500 cap), « impôt sur le revenu net » 1 253, taux foyer 4.7 %.
        {
            "couple_two_children_childcare_donations",
            Household([Salaried("A", 42_000m), Salaried("B", 31_000m)], dependents: 2, under6: 1) with { ChildcareOutsideHome = 4_000m, Donations = 600m },
            new Expected(3m, 65_700m, 65_700m, 3_399m, 0m, 1_253m, 4.7m)
        },
        // Married, no child; 1AJ 38 000, 1BJ 34 000, 4BC 7 000: the deficit Nid's 2044 gives for rents 9 000, interest 2 000 and
        // other charges 14 000 (the interest within the rents, 7 000 of charges beyond them on the global income).
        {
            "couple_real_rental_deficit",
            Household([Salaried("A", 38_000m), Salaried("B", 34_000m)]) with
            {
                RentalGross = 9_000m, RentalInterest = 2_000m, RentalCharges = 14_000m, RentalRegime = RentalRegime.Real,
            },
            new Expected(2m, 57_800m, 57_800m, 3_806m, 0m, 3_806m, null)
        },
        // Single; 1AJ 30 000, 4BE 12 000 (micro-foncier), 2DC 2 000 and 2TR 800 at the flat tax, 2CG 2 800 (levied at source in 2025).
        // Simulator: « impôt proportionnel » 358, net tax 4 082, social levies on the rents 1 445, taux foyer 9.7 %.
        {
            "single_micro_rental_and_flat_tax",
            Household([Salaried("A", 30_000m)]) with { RentalGross = 12_000m, CapitalIncome = 2_800m },
            new Expected(1m, 35_400m, 38_200m, 3_724m, 0m, 4_082m, 9.7m)
        },
        // Married, children born 2012 and 2015; 1AJ 120 000, 1BJ 90 000: the family quotient capped (« droits simples * »).
        {
            "high_income_couple_family_cap",
            Household([Salaried("A", 120_000m), Salaried("B", 90_000m)], dependents: 2),
            new Expected(3m, 189_000m, 189_000m, 41_477m, 0m, 41_477m, null)
        },
        // Single; 1AJ 22 000: within the decote. Taux foyer 1.9 %.
        {
            "single_with_decote",
            Household([Salaried("A", 22_000m)]),
            new Expected(1m, 19_800m, 19_800m, 902m, 489m, 413m, 1.9m)
        },
        // Married, a child born 2016; 1AJ 28 000, 1BJ 20 000, 7DB 8 000, 7UD 1 000. Simulator: 750 of aid donations, 4 000 of
        // home employment credit, a refund of 3 964.
        {
            "couple_home_employment_refund",
            Household([Salaried("A", 28_000m), Salaried("B", 20_000m)], dependents: 1) with { HomeEmployment = 8_000m, DonationsAid = 1_000m },
            new Expected(2.5m, 43_200m, 43_200m, 1_562m, 776m, -3_964m, null)
        },
    };

    private static TaxDeclarant Pensioner(string name, decimal pensions) => new(name, 0m, EmploymentStatus.NonExecutive, 0m, 0m, Pensions: pensions);

    /// <summary>
    /// Issue #236: pensions and alimony, worked by hand on the brochure pratique IR 2026 (allowance 454 € to 4 439 €, an adult child's
    /// alimony within 6 855 €), with the boxes to give the simulator in each comment.
    /// </summary>
    public static TheoryData<string, TaxInputs, Expected> PensionScenarios => new()
    {
        // Married retirees; 1AS 30 000, 1BS 25 000: the allowance capped at 4 439 for the household. Taux foyer 2 889 / 55 000.
        {
            "retired_couple_allowance_capped",
            Household([Pensioner("A", 30_000m), Pensioner("B", 25_000m)]),
            new Expected(2m, 50_561m, 50_561m, 3_010m, 121m, 2_889m, 5.3m)
        },
        // Single retiree; 1AS 40 000, 6GI 8 000 to an adult child: 6 855 deducted. Within the decote.
        {
            "single_retiree_alimony_to_adult_child",
            Household([Pensioner("A", 40_000m)]) with { AlimonyAdultChildren = 8_000m, AlimonyAdultChildrenCount = 1 },
            new Expected(1m, 29_145m, 29_145m, 1_930m, 24m, 1_906m, 4.8m)
        },
        // Single; 1AJ 20 000, 1AO 6 000 of alimony received: each its own 10 % allowance.
        {
            "single_salary_and_alimony_received",
            Household([Salaried("A", 20_000m) with { Pensions = 6_000m }]),
            new Expected(1m, 23_400m, 23_400m, 1_298m, 310m, 988m, 3.8m)
        },
    };

    [Theory]
    [MemberData(nameof(PensionScenarios))]
    public void Pensions_and_alimony_match_the_brochures_rules(string scenario, TaxInputs inputs, Expected expected)
        => The_estimate_matches_the_official_simulator(scenario, inputs, expected);

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void The_estimate_matches_the_official_simulator(string scenario, TaxInputs inputs, Expected expected)
    {
        var result = TaxCalculator.Compute(inputs);

        Assert.True(expected == new Expected(
            result.Parts, result.NetTaxableIncome, result.ReferenceIncome, Euro(result.GrossTax), Euro(result.Decote),
            Euro(result.IncomeTax + result.CapitalFlatTax), expected.Rate is null ? null : result.Withholding.Rate),
            $"{scenario}: {result}");
    }

    [Fact]
    public void The_reductions_and_credits_match_the_official_simulator()
    {
        var couple = TaxCalculator.Compute(Scenario("couple_two_children_childcare_donations"));
        Assert.Equal((396m, 1_750m), (Euro(couple.DonationReduction), Euro(couple.ChildcareCredit)));

        var home = TaxCalculator.Compute(Scenario("couple_home_employment_refund"));
        Assert.Equal((750m, 4_000m), (Euro(home.DonationReduction), Euro(home.HomeEmploymentCredit)));
    }

    [Fact]
    public void The_rental_social_levies_match_the_official_simulator()
    {
        var result = TaxCalculator.Compute(Scenario("single_micro_rental_and_flat_tax"));

        Assert.Equal(358m, Euro(result.CapitalFlatTax));
        // Nid counts the 17.2 % the bank withheld on the capital income in 2025; the simulator, told so by 2CG, only the rents' 17.2 %.
        Assert.Equal(1_445m, Euro(result.SocialLevies - 0.172m * 2_800m));
    }

    private static TaxInputs Scenario(string name) => Scenarios.Select(s => s.Data).Single(d => d.Item1 == name).Item2;

    private static decimal Euro(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);
}
