using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Tax estimate spec §4: examples worked by hand on the 2025 revenue rules.</summary>
public class TaxCalculatorTests
{
    private static TaxInputs Inputs(IReadOnlyList<TaxDeclarant> declarants, int dependents = 0, int year = 2025) => new(
        year, declarants, dependents, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    private static TaxDeclarant Other(decimal taxable) => new("A", 0m, EmploymentStatus.NonExecutive, taxable, 0m);

    [Fact]
    public void Couple_with_two_young_children_and_childcare_pays_after_the_decote_and_the_credit()
    {
        var inputs = Inputs([
            new("A", 50_000m, EmploymentStatus.NonExecutive, 0m, 0m),
            new("B", 40_000m, EmploymentStatus.Executive, 0m, 0m)], dependents: 2) with { ChildrenUnder6 = 2, ChildcareOutsideHome = 6_000m };

        var result = TaxCalculator.Compute(inputs);

        // A: 39 000 net + 1 424.63 CSG − 4 042.46 ; B: 30 000 + 1 139.70 − 3 113.97.
        Assert.Equal(36_382.16m, result.Declarants[0].Taxable);
        Assert.Equal(28_025.73m, result.Declarants[1].Taxable);
        Assert.Equal(64_407m, result.NetTaxableIncome);
        Assert.Equal(3m, result.Parts);
        Assert.Equal(3_256.77m, result.GrossTax); // 3 × 11 % × (21 469 − 11 600)
        Assert.False(result.FamilyCapApplied);
        Assert.Equal(9.31m, result.Decote); // 1 483 − 45.25 % × 3 256.77
        Assert.Equal(3_000m, result.ChildcareCredit);
        Assert.Equal(247m, result.IncomeTax);
        Assert.Equal(0.11m, result.MarginalRate);
        Assert.Equal(69_000m, result.NetBeforeTax);
        Assert.Equal(68_753m, result.NetAfterTax);
        Assert.Equal(5_729.42m, result.NetAfterTaxMonthly);

        // Withholding: 3 247 (3 256.77 − 9.31) over 71 564.33 of salaries before the allowance.
        var pas = result.Withholding;
        Assert.Equal(3_247m, pas.TaxBase);
        Assert.Equal(71_564.33m, pas.Income);
        Assert.Equal(4.5m, pas.Rate);
        Assert.Equal(40_424.63m, pas.Declarants[0].Salaries);
        Assert.Equal(151.59m, pas.Declarants[0].Monthly);
        Assert.Equal(116.77m, pas.Declarants[1].Monthly);
        Assert.Equal(3_220.39m, pas.Annual);
        Assert.Equal(3_000m, pas.LeftOut);
        Assert.Equal(-2_973.39m, pas.Balance);
    }

    [Fact]
    public void Rental_income_is_withheld_with_its_social_levies()
    {
        var result = TaxCalculator.Compute(Inputs([Other(40_000m), Other(0m)]) with { RentalGross = 10_000m });

        // 36 000 + 7 000 taxable over 2 shares: 2 × (11 % × 9 900) = 2 178, less the decote 497.45 ; 40 000 + 7 000 within the withholding.
        Assert.Equal(1_681m, result.Withholding.TaxBase);
        Assert.Equal(3.6m, result.Withholding.Rate);
        Assert.Equal(7_000m, result.Withholding.RentalDeclared);
        Assert.Equal(121.33m, result.Withholding.RentalMonthly); // (3.6 % + 17.2 %) × 7 000 ÷ 12
    }

    [Fact]
    public void The_payslips_contribution_rate_and_actual_expenses_replace_the_flat_ones()
    {
        var declarant = new TaxDeclarant("A", 50_000m, EmploymentStatus.NonExecutive, 0m, 0m, ContributionRate: 0.20m, ActualExpenses: 6_000m);

        var result = TaxCalculator.Compute(Inputs([declarant, Other(0m)])).Declarants[0];

        Assert.Equal(40_000m, result.NetSalary); // 80 % of the gross, not 78 %
        Assert.Equal(41_424.63m, result.NetTaxableIncome);
        Assert.Equal(6_000m, result.Allowance); // instead of 4 142.46
        Assert.Equal(35_424.63m, result.Taxable);
    }

    [Fact]
    public void Family_quotient_advantage_is_capped_per_half_share()
    {
        // 164 555 taxable less the 14 555 cap of the allowance: 150 000.
        var result = TaxCalculator.Compute(Inputs([Other(164_555m), Other(0m)], dependents: 3));

        Assert.Equal(150_000m, result.NetTaxableIncome);
        Assert.Equal(4m, result.Parts);
        Assert.True(result.FamilyCapApplied);
        Assert.Equal(23_979.98m, result.GrossTax); // 2 × scale(75 000) − 4 × 1 807
        Assert.Equal(0m, result.Decote);
        Assert.Equal(23_980m, result.IncomeTax);
        Assert.Equal(0.30m, result.MarginalRate);
    }

    [Fact]
    public void Credits_are_paid_back_and_the_niche_cap_takes_back_the_excess()
    {
        var inputs = Inputs([Other(20_000m), Other(0m)], dependents: 2) with
        {
            ChildrenUnder6 = 2, ChildcareOutsideHome = 7_000m, HomeEmployment = 20_000m, PinelReduction = 9_000m,
        };

        var result = TaxCalculator.Compute(inputs);

        Assert.Equal(0m, result.GrossTax); // 18 000 over 3 shares
        Assert.Equal(0m, result.PinelReduction); // nothing to reduce
        Assert.Equal(3_500m, result.ChildcareCredit);
        Assert.Equal(7_500m, result.HomeEmploymentCredit); // 50 % of 15 000
        Assert.Equal(1_000m, result.NicheCapExcess);
        Assert.Equal(-10_000m, result.IncomeTax);
    }

    [Fact]
    public void Micro_rental_keeps_70_percent_and_pays_social_levies()
    {
        var result = TaxCalculator.Compute(Inputs([Other(0m), Other(0m)]) with { RentalGross = 10_000m });

        Assert.Equal(RentalRegime.Micro, result.RentalRegime);
        Assert.Equal(7_000m, result.RentalTaxable);
        Assert.Equal(1_204m, result.SocialLevies);
    }

    [Fact]
    public void Micro_above_the_ceiling_falls_back_to_the_real_regime()
    {
        var result = TaxCalculator.Compute(Inputs([Other(0m), Other(0m)]) with
        {
            RentalGross = 16_000m, RentalCharges = 2_000m, RentalInterest = 4_000m, RentalRegime = RentalRegime.Micro,
        });

        Assert.Equal(RentalRegime.Real, result.RentalRegime);
        Assert.Equal(10_000m, result.RentalTaxable);
        Assert.Contains(TaxWarnings.MicroOverCeiling, result.Warnings);
    }

    [Fact]
    public void A_pinel_property_shuts_the_micro_regime()
    {
        var chosen = TaxCalculator.Compute(Inputs([Other(0m), Other(0m)]) with
        {
            RentalGross = 10_000m, RentalCharges = 2_000m, RentalInterest = 1_000m, PinelReduction = 3_000m, RentalRegime = RentalRegime.Micro,
        });
        var defaulted = TaxCalculator.Compute(Inputs([Other(0m), Other(0m)]) with
        {
            RentalGross = 10_000m, RentalCharges = 2_000m, RentalInterest = 1_000m, PinelReduction = 3_000m,
        });

        Assert.Equal(RentalRegime.Real, chosen.RentalRegime);
        Assert.Equal(7_000m, chosen.RentalTaxable);
        Assert.Contains(TaxWarnings.PinelRequiresReal, chosen.Warnings);
        Assert.Equal(RentalRegime.Real, defaulted.RentalRegime);
        Assert.DoesNotContain(TaxWarnings.PinelRequiresReal, defaulted.Warnings);
    }

    [Theory]
    [InlineData(12_000, 14_000, 3_000, 3_000)] // interest beyond the rents is carried, the charges go on the global income
    [InlineData(5_000, 0, 20_000, 10_700)] // capped
    [InlineData(10_000, 2_000, 5_000, 0)] // a profit
    public void Real_regime_deficit_leaves_interest_out(int gross, int interest, int charges, int deficit)
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m), Other(0m)]) with
        {
            RentalGross = gross, RentalInterest = interest, RentalCharges = charges, RentalRegime = RentalRegime.Real,
        });

        Assert.Equal(deficit, result.RentalDeficit);
    }

    [Fact]
    public void Per_contributions_are_deducted_within_the_ceiling()
    {
        // 50 000 taxable after allowance: ceiling 5 000.
        var declarant = new TaxDeclarant("A", 0m, EmploymentStatus.NonExecutive, 55_555.56m, 8_000m);

        var result = TaxCalculator.Compute(Inputs([declarant, Other(0m)]));

        Assert.Equal(5_000m, result.Declarants[0].PerDeducted);
        Assert.Equal(45_000m, result.NetTaxableIncome);
    }

    [Fact]
    public void Unused_per_ceilings_raise_the_year_ceiling()
    {
        var declarant = new TaxDeclarant("A", 0m, EmploymentStatus.NonExecutive, 55_555.56m, 8_000m, PerUnusedCeiling: 2_000m);

        var result = TaxCalculator.Compute(Inputs([declarant, Other(0m)]));

        Assert.Equal(7_000m, result.Declarants[0].PerDeducted);
    }

    [Theory]
    [InlineData(null, 10_000, 4_000, 3_000)] // micro: 7 000 net, 4 000 absorbed
    [InlineData(null, 10_000, 9_000, 0)] // only up to the net: the rest stays carried, never on the global income
    [InlineData(RentalRegime.Real, 10_000, 4_000, 4_000)] // real: 10 000 − 2 000 charges = 8 000
    public void Carried_rental_deficit_offsets_the_rental_income_only(RentalRegime? regime, int gross, int carried, int taxable)
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m), Other(0m)]) with
        {
            RentalGross = gross, RentalCharges = regime is null ? 0m : 2_000m, RentalRegime = regime, RentalDeficitCarried = carried,
        });

        Assert.Equal(taxable, result.RentalTaxable);
        Assert.Equal(Math.Min(carried, gross - (regime is null ? 3_000 : 2_000)), result.RentalDeficitCarriedUsed);
        Assert.Equal(27_000m + taxable, result.NetTaxableIncome); // 30 000 − 10 % allowance
    }

    [Fact]
    public void Capital_income_pays_the_flat_tax_and_the_2026_levies()
    {
        var result = TaxCalculator.Compute(Inputs([Other(0m), Other(0m)], year: 2026) with { CapitalIncome = 1_000m });

        Assert.Equal(128m, result.CapitalFlatTax);
        Assert.Equal(186m, result.SocialLevies);
        Assert.Contains(TaxWarnings.RulesCarriedForward, result.Warnings);
    }

    [Fact]
    public void Donations_reduce_at_75_then_66_percent()
    {
        var result = TaxCalculator.Compute(Inputs([Other(60_000m), Other(0m)]) with { DonationsAid = 1_200m, Donations = 300m });

        Assert.Equal(1_080m, result.DonationReduction); // 75 % × 1 000 + 66 % × 500
    }

    [Theory]
    [InlineData(2025, 0)]
    [InlineData(2026, 3_333.33)]
    [InlineData(2034, 3_333.33)]
    [InlineData(2035, 1_666.67)]
    [InlineData(2037, 1_666.67)]
    [InlineData(2038, 0)]
    public void Pinel_2023_nine_years_extended_to_twelve(int year, double expected)
        => Assert.Equal((decimal)expected, Pinel.AnnualReduction(200_000m, 2023, false, 9, 12, 2026, year));

    [Fact]
    public void Pinel_base_is_capped_by_the_price_and_the_surface()
    {
        Assert.Equal(275_000m, Pinel.Base(320_000m, 50m));
        Assert.Equal(300_000m, Pinel.Base(320_000m, null));
    }
}
