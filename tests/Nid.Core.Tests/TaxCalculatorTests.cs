using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Tax estimate spec §4: examples worked by hand on the 2025 revenue rules.</summary>
public class TaxCalculatorTests
{
    private static TaxInputs Inputs(IReadOnlyList<TaxDeclarant> declarants, int dependents = 0, int year = 2025) => new(
        year, declarants, dependents, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    private static TaxDeclarant Other(decimal taxable) => new("A", 0m, EmploymentStatus.NonExecutive, taxable, 0m);

    [Fact]
    public void An_entered_net_taxable_income_is_counted_as_it_is()
    {
        var result = TaxCalculator.Compute(Inputs([new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 50_000m)]));

        // 50 000 − the 10 % allowance, no contributions to deduct and no CSG to add.
        Assert.Equal(50_000m, result.Declarants[0].NetTaxableIncome);
        Assert.Equal(45_000m, result.Declarants[0].Taxable);
        Assert.Equal(SalaryMethod.Entered, result.Declarants[0].Method);
    }

    [Fact]
    public void Entered_and_estimated_salaries_add_up()
    {
        var result = TaxCalculator.Compute(Inputs([new("A", 10_000m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 20_000m)]));

        // 20 000 + 10 000 × 78 % + 284.925 of CSG.
        Assert.Equal(28_084.93m, result.Declarants[0].NetTaxableIncome);
        Assert.Equal(25_276.43m, result.Declarants[0].Taxable);
        Assert.Equal(SalaryMethod.Mixed, result.Declarants[0].Method);
    }

    [Fact]
    public void Without_entered_income_the_result_is_unchanged()
    {
        var result = TaxCalculator.Compute(Inputs([
            new("A", 50_000m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 0m),
            new("B", 40_000m, EmploymentStatus.Executive, 0m, 0m)], dependents: 2) with { ChildrenUnder6 = 2, ChildcareOutsideHome = 6_000m });

        Assert.Equal(36_382.16m, result.Declarants[0].Taxable);
        Assert.Equal(28_025.73m, result.Declarants[1].Taxable);
        Assert.Equal(247m, result.IncomeTax);
        Assert.All(result.Declarants, d => Assert.Equal(SalaryMethod.Estimated, d.Method));
        Assert.Equal(SalaryMethod.None, TaxCalculator.Compute(Inputs([Other(1_000m)])).Declarants[0].Method);
    }

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
        // Spec §4.2: the ceiling now comes from last year's income (no longer the year's own), so the 50 000 is given as
        // PreviousYearIncome: ceiling 10 % × 50 000 = 5 000.
        var declarant = new TaxDeclarant("A", 0m, EmploymentStatus.NonExecutive, 55_555.56m, 8_000m, PreviousYearIncome: 50_000m);

        var result = TaxCalculator.Compute(Inputs([declarant, Other(0m)]));

        Assert.Equal(5_000m, result.Declarants[0].PerDeducted);
        Assert.Equal(45_000m, result.NetTaxableIncome);
    }

    [Fact]
    public void Unused_per_ceilings_raise_the_year_ceiling()
    {
        // Spec §4.2: last year's 50 000 (no longer the year's own income) gives 5 000, plus the 2 000 unused.
        var declarant = new TaxDeclarant("A", 0m, EmploymentStatus.NonExecutive, 55_555.56m, 8_000m, PerUnusedCeiling: 2_000m, PreviousYearIncome: 50_000m);

        var result = TaxCalculator.Compute(Inputs([declarant, Other(0m)]));

        Assert.Equal(7_000m, result.Declarants[0].PerDeducted);
    }

    [Fact]
    public void The_notice_ceiling_caps_the_deduction()
    {
        var declarant = Other(50_000m) with { PerContributions = 8_000m, PerCeiling = 4_000m, PerExpiring = 1_500m };

        var result = TaxCalculator.Compute(Inputs([declarant]));

        var per = result.Declarants[0].Per!;
        Assert.Equal(4_000m, per.Ceiling);
        Assert.Equal(PerCeilingSource.Notice, per.Source);
        Assert.Equal(1_500m, per.Expiring);
        Assert.Equal(8_000m, per.Contributions);
        Assert.Equal(4_000m, per.Deducted);
        Assert.Equal(5_080m, per.Excess);
        Assert.Equal(0m, per.Remaining);
        Assert.Equal(4_000m, result.Declarants[0].PerDeducted);
        Assert.Equal(41_000m, result.NetTaxableIncome); // 45 000 after the allowance − 4 000
        Assert.Equal([TaxWarnings.PerOverCeiling], result.Warnings);
        // What expires never exceeds the ceiling.
        Assert.Equal(4_000m, TaxCalculator.Compute(Inputs([declarant with { PerExpiring = 5_000m }])).Declarants[0].Per!.Expiring);
    }

    [Theory]
    [InlineData(58_431.27, 5_843.13)] // 10 % × 58 431.27 = 5 843.127
    [InlineData(0, 4_710)] // the floor: 10 % × PASS 47 100
    [InlineData(500_000, 37_680)] // capped at 8 PASS: 10 % × 376 800
    public void Without_a_notice_the_ceiling_comes_from_last_years_income(double previous, double ceiling)
    {
        var declarant = Other(30_000m) with { PerContributions = 1_000m, PreviousYearIncome = (decimal)previous };

        var result = TaxCalculator.Compute(Inputs([declarant]));

        var per = result.Declarants[0].Per!;
        Assert.Equal((decimal)ceiling, per.Ceiling);
        Assert.Equal(PerCeilingSource.Estimated, per.Source);
        Assert.Equal((decimal)ceiling - 1_000m, per.Remaining);
        Assert.Equal([TaxWarnings.PerCeilingEstimated], result.Warnings);
    }

    [Fact]
    public void An_estimated_ceiling_warns_only_with_contributions()
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m)]));

        Assert.Equal(PerCeilingSource.Estimated, result.Declarants[0].Per!.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Previous_year_rules_never_go_below_the_first_year()
    {
        // 2026 reads the 2025 PASS; 2025 has no earlier rules and reads its own.
        Assert.Equal(4_710m, TaxCalculator.Compute(Inputs([Other(30_000m)], year: 2026)).Declarants[0].Per!.Ceiling);
        Assert.Equal(4_710m, TaxCalculator.Compute(Inputs([Other(30_000m)], year: 2025)).Declarants[0].Per!.Ceiling);
    }

    [Fact]
    public void Unused_ceilings_are_added_only_to_an_estimated_ceiling()
    {
        var notice = Other(30_000m) with { PerCeiling = 4_000m, PerUnusedCeiling = 3_000m };
        var estimated = Other(30_000m) with { PerUnusedCeiling = 3_000m };

        Assert.Equal(4_000m, TaxCalculator.Compute(Inputs([notice])).Declarants[0].Per!.Ceiling);
        Assert.Equal(7_710m, TaxCalculator.Compute(Inputs([estimated])).Declarants[0].Per!.Ceiling); // 4 710 + 3 000
    }

    [Fact]
    public void Pooling_covers_one_spouses_excess_with_the_others_free_ceiling()
    {
        var a = Other(60_000m) with { PerContributions = 8_000m, PerCeiling = 5_000m };
        var b = Other(30_000m) with { Name = "B", PerContributions = 1_000m, PerCeiling = 4_000m };

        // A's 3 000 over the ceiling fits in B's free 3 000.
        var pooled = TaxCalculator.Compute(Inputs([a, b]) with { PerPooling = true });
        Assert.True(pooled.Per!.Pooled);
        Assert.Equal(8_000m, pooled.Declarants[0].Per!.Deducted);
        Assert.Equal(0m, pooled.Declarants[0].Per!.Excess);
        Assert.Equal(0m, pooled.Declarants[0].Per!.Remaining);
        Assert.Equal(8_000m, pooled.Declarants[0].PerDeducted);
        Assert.Equal(1_000m, pooled.Declarants[1].Per!.Deducted);
        Assert.Equal(0m, pooled.Declarants[1].Per!.Remaining);
        Assert.Equal(0m, pooled.Per.Remaining);
        Assert.Equal(72_000m, pooled.NetTaxableIncome); // 54 000 + 27 000 − 9 000
        Assert.Empty(pooled.Warnings);

        // B paying 2 000 leaves only 2 000 free: 1 000 of A's stays over.
        var partly = TaxCalculator.Compute(Inputs([a, b with { PerContributions = 2_000m }]) with { PerPooling = true });
        Assert.Equal(7_000m, partly.Declarants[0].Per!.Deducted);
        Assert.Equal(1_000m, partly.Declarants[0].Per!.Excess);
        Assert.Equal(2_000m, partly.Declarants[1].Per!.Deducted);
        Assert.Equal(0m, partly.Per!.Remaining);
        Assert.Equal([TaxWarnings.PerOverCeiling], partly.Warnings);

        // Without pooling each keeps to their own ceiling.
        var apart = TaxCalculator.Compute(Inputs([a, b]));
        Assert.False(apart.Per!.Pooled);
        Assert.Equal(5_000m, apart.Declarants[0].Per!.Deducted);
        Assert.Equal(3_000m, apart.Declarants[0].Per!.Excess);
        Assert.Equal(3_000m, apart.Declarants[1].Per!.Remaining);
        Assert.Equal(3_000m, apart.Per.Remaining);
    }

    [Fact]
    public void Pooled_each_declarant_shows_the_households_free_ceiling()
    {
        var a = Other(60_000m) with { PerContributions = 6_000m, PerCeiling = 5_000m };
        var b = Other(30_000m) with { Name = "B", PerContributions = 1_000m, PerCeiling = 4_000m };

        // A's 1 000 over goes on B's free 3 000: 2 000 left for the household, shown on both rows.
        var pooled = TaxCalculator.Compute(Inputs([a, b]) with { PerPooling = true });
        Assert.Equal(6_000m, pooled.Declarants[0].Per!.Deducted);
        Assert.Equal(2_000m, pooled.Declarants[0].Per!.Remaining);
        Assert.Equal(2_000m, pooled.Declarants[1].Per!.Remaining);
        Assert.Equal(2_000m, pooled.Per!.Remaining);

        // Mirrored, B's excess against A's free ceiling: A 5 000 / 1 000, B 4 000 / 6 000 → B deducts 6 000, 2 000 left.
        var mirrored = TaxCalculator.Compute(Inputs([a with { PerContributions = 1_000m }, b with { PerContributions = 6_000m }]) with { PerPooling = true });
        Assert.Equal(1_000m, mirrored.Declarants[0].Per!.Deducted);
        Assert.Equal(6_000m, mirrored.Declarants[1].Per!.Deducted);
        Assert.Equal(0m, mirrored.Declarants[1].Per!.Excess);
        Assert.Equal(6_000m, mirrored.Declarants[1].PerDeducted);
        Assert.Equal(2_000m, mirrored.Declarants[0].Per!.Remaining);
        Assert.Equal(2_000m, mirrored.Declarants[1].Per!.Remaining);
        Assert.Equal(2_000m, mirrored.Per!.Remaining);
        Assert.Equal(74_000m, mirrored.NetTaxableIncome); // 54 000 + 27 000 − 7 000

        // Not pooled, each row keeps its own: A 5 000 − 1 000, B 4 000 − 4 000.
        var apart = TaxCalculator.Compute(Inputs([a with { PerContributions = 1_000m }, b with { PerContributions = 6_000m }]));
        Assert.Equal(4_000m, apart.Declarants[0].Per!.Remaining);
        Assert.Equal(0m, apart.Declarants[1].Per!.Remaining);
        Assert.Equal(4_000m, apart.Per!.Remaining);
    }

    [Fact]
    public void Pooling_with_a_single_declarant_changes_nothing()
    {
        var a = Other(60_000m) with { PerContributions = 8_000m, PerCeiling = 5_000m };

        var pooled = TaxCalculator.Compute(Inputs([a]) with { PerPooling = true });
        var alone = TaxCalculator.Compute(Inputs([a]));

        Assert.False(pooled.Per!.Pooled);
        Assert.Equal(alone.Declarants[0].Per, pooled.Declarants[0].Per);
        Assert.Equal(5_000m, pooled.Declarants[0].PerDeducted);
        Assert.Equal(alone.Per, pooled.Per);
        Assert.Equal(alone.IncomeTax, pooled.IncomeTax);
    }

    [Fact]
    public void Tax_saved_is_the_tax_without_PER_minus_the_tax()
    {
        var inputs = Inputs([
            new("A", 50_000m, EmploymentStatus.NonExecutive, 0m, 3_000m, PerCeiling: 10_000m),
            new("B", 40_000m, EmploymentStatus.Executive, 0m, 0m)], dependents: 2) with { ChildrenUnder6 = 2, ChildcareOutsideHome = 6_000m };

        var result = TaxCalculator.Compute(inputs);
        var without = TaxCalculator.Compute(inputs with { Declarants = [.. inputs.Declarants.Select(d => d with { PerContributions = 0m })] });

        // 61 407 on 3 shares: 3 × 11 % × (20 469 − 11 600) = 2 926.77, decote 1 483 − 45.25 % × 2 926.77 = 158.64,
        // − 3 000 of childcare: −232, against the 247 of the couple example.
        Assert.Equal(-232m, result.IncomeTax);
        Assert.Equal(247m, without.IncomeTax);
        Assert.Equal(479m, result.Per!.TaxSaved);
        Assert.Equal(without.IncomeTax - result.IncomeTax, result.Per.TaxSaved);
        Assert.Equal(0m, without.Per!.TaxSaved);
    }

    [Fact]
    public void Room_in_bracket_is_what_stays_at_the_marginal_rate()
    {
        var single = Other(40_000m) with { ActualExpenses = 0m };

        Assert.Equal(10_421m, TaxCalculator.Compute(Inputs([single])).Per!.RoomInBracket); // 40 000 − 29 579
        Assert.Equal(0m, TaxCalculator.Compute(Inputs([single with { OtherTaxable = 10_000m }])).Per!.RoomInBracket); // 0 % bracket
        // A couple: 80 000 on 2 shares, (40 000 − 29 579) × 2.
        Assert.Equal(20_842m, TaxCalculator.Compute(Inputs([single, single])).Per!.RoomInBracket);
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

    [Fact]
    public void The_reference_income_is_the_net_taxable_income_alone()
    {
        var result = TaxCalculator.Compute(Inputs([Other(50_000m)]));

        Assert.Equal(45_000m, result.ReferenceIncome);
    }

    [Fact]
    public void The_reference_income_adds_back_the_per_deduction()
    {
        // Art. 1417 IV CGI: the PER deduction lowers the taxable income, not the reference income.
        var result = TaxCalculator.Compute(Inputs([Other(50_000m) with { PerContributions = 3_000m, PerCeiling = 4_000m }]));

        Assert.Equal(42_000m, result.NetTaxableIncome);
        Assert.Equal(45_000m, result.ReferenceIncome);
    }

    [Fact]
    public void The_reference_income_adds_flat_taxed_capital_and_exempt_income_not_benefits()
    {
        var inputs = Inputs([Other(50_000m) with { ExemptIncome = 2_400.60m }, Other(0m) with { ExemptIncome = 500m }])
            with { CapitalIncome = 1_200m, NonTaxableIncome = 3_000m };

        var result = TaxCalculator.Compute(inputs);

        Assert.Equal(45_000m, result.NetTaxableIncome);
        // 45 000 + 1 200 of dividends and interest + 2 900.60 of exempt overtime, to the euro below; family benefits stay out.
        Assert.Equal(49_100m, result.ReferenceIncome);
    }
}
