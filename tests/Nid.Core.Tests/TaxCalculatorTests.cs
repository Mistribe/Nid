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
    public void The_other_rental_regime_is_compared_when_the_household_may_choose()
    {
        var rents = Inputs([Other(0m), Other(0m)]) with { RentalGross = 10_000m, RentalCharges = 4_000m, RentalInterest = 1_000m };

        // Micro: 7 000 taxable, 1 204 of levies; real: 5 000, 860. No income tax either way.
        Assert.Equal(-344m, TaxCalculator.Compute(rents).RentalRegimeGap);
        Assert.Equal(344m, TaxCalculator.Compute(rents with { RentalRegime = RentalRegime.Real }).RentalRegimeGap);
        Assert.Null(TaxCalculator.Compute(Inputs([Other(0m), Other(0m)])).RentalRegimeGap);
        Assert.Null(TaxCalculator.Compute(rents with { RentalGross = 16_000m }).RentalRegimeGap);
        Assert.Null(TaxCalculator.Compute(rents with { PinelReduction = 3_000m }).RentalRegimeGap);
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

    [Theory]
    [InlineData(2025, 9_500, 20_200)] // BOI-RFPI-BASE-30-20, first example: 10 700 + 9 500
    [InlineData(2025, 19_600, 21_400)] // second example: 21 400 at most
    [InlineData(2025, 0, 10_700)]
    [InlineData(2026, 9_500, 10_700)] // works paid after 2025: no raise (ann. III art. 41 DO)
    public void Energy_renovation_works_raise_the_deficit_cap(int year, int works, int deficit)
    {
        var result = TaxCalculator.Compute(Inputs([Other(60_000m), Other(0m)], year: year) with
        {
            RentalGross = 6_000m, RentalCharges = 30_000m, RentalRegime = RentalRegime.Real, RentalEnergyWorks = works,
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
        Assert.Equal(4_000m, per.Excess);
        Assert.Equal(0m, per.Remaining);
        Assert.Equal(4_000m, result.Declarants[0].PerDeducted);
        Assert.Equal(41_000m, result.NetTaxableIncome); // 45 000 after the allowance − 4 000
        Assert.Equal([TaxWarnings.PerOverCeiling], result.Warnings);
        // What expires never exceeds the ceiling.
        Assert.Equal(4_000m, TaxCalculator.Compute(Inputs([declarant with { PerExpiring = 5_000m }])).Declarants[0].Per!.Expiring);
    }

    [Theory]
    [InlineData(0, 8_824)] // the year's 4 710 first, then the 4 114 expiring
    [InlineData(5_000, 3_824)]
    [InlineData(8_824, 0)] // reached: nothing is lost
    [InlineData(12_000, 0)]
    public void The_minimum_keeps_the_expiring_ceiling_after_the_years_own(double paid, double minimum)
    {
        // The 2026 notice: 4 114 + 4 399 + 4 637 unused, 4 710 on 2025's income.
        var declarant = Other(50_000m) with { PerContributions = (decimal)paid, PerCeiling = 17_860m, PerUnusedCeiling = 13_150m, PerExpiring = 4_114m };

        var per = TaxCalculator.Compute(Inputs([declarant])).Declarants[0].Per!;

        Assert.Equal((decimal)minimum, per.Minimum);
        Assert.False(per.MinimumEstimated);
        Assert.Equal(17_860m - (decimal)paid, per.Remaining);
    }

    [Fact]
    public void Nothing_expiring_needs_no_minimum()
    {
        var declarant = Other(50_000m) with { PerCeiling = 17_860m, PerUnusedCeiling = 13_150m };

        Assert.Equal(0m, TaxCalculator.Compute(Inputs([declarant])).Declarants[0].Per!.Minimum);
    }

    [Fact]
    public void Without_the_unused_ceilings_the_years_own_is_estimated()
    {
        // The year's own ceiling on last year's income: 10 % × 40 000 is under the 4 637 floor.
        var declarant = Other(50_000m) with { PerCeiling = 17_860m, PerExpiring = 4_114m, PreviousYearIncome = 40_000m, PerContributions = 1_000m };

        var per = TaxCalculator.Compute(Inputs([declarant])).Declarants[0].Per!;

        Assert.Equal(4_637m + 4_114m - 1_000m, per.Minimum);
        Assert.True(per.MinimumEstimated);
    }

    [Fact]
    public void The_spouses_use_of_the_ceiling_counts_towards_the_minimum()
    {
        var a = Other(50_000m) with { PerCeiling = 10_000m, PerUnusedCeiling = 6_000m, PerExpiring = 2_000m, PerContributions = 1_000m };
        var b = Other(50_000m) with { PerCeiling = 5_000m, PerContributions = 8_000m };

        var pooled = TaxCalculator.Compute(Inputs([a, b]) with { PerPooling = true });

        // a keeps 4 000 + 2 000; a's own 1 000 and b's 3 000 excess on a's ceiling leave 2 000.
        Assert.Equal(2_000m, pooled.Declarants[0].Per!.Minimum);
    }

    [Theory]
    [InlineData(58_431.27, 5_843.13)] // 10 % × 58 431.27 = 5 843.127
    [InlineData(0, 4_637)] // the floor: 10 % × the 2024 PASS, 46 368
    [InlineData(500_000, 37_094)] // capped at 8 PASS: 10 % × 370 944
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

    [Theory]
    [InlineData(2025, 4_637, 37_094)] // the 2024 PASS, 46 368 (brochure IR 2026)
    [InlineData(2026, 4_710, 37_680)] // the 2025 PASS, 47 100 (impots.gouv.fr, épargne retraite)
    [InlineData(2027, 4_710, 37_680)] // carried forward
    public void The_estimated_ceiling_reads_the_previous_years_pass(int year, int floor, int cap)
    {
        Assert.Equal(floor, TaxCalculator.Compute(Inputs([Other(30_000m)], year: year)).Declarants[0].Per!.Ceiling);
        var high = Other(30_000m) with { PreviousYearIncome = 500_000m };
        Assert.Equal(cap, TaxCalculator.Compute(Inputs([high], year: year)).Declarants[0].Per!.Ceiling);
    }

    [Fact]
    public void The_exempt_income_after_the_allowance_is_given_for_the_next_ceiling()
    {
        // 50 000 + 2 000 of overtime: the allowance grows from 5 000 to 5 200, 1 800 of it stays.
        var result = TaxCalculator.Compute(Inputs([Other(50_000m) with { ExemptIncome = 2_000m }]));

        Assert.Equal(45_000m, result.Declarants[0].Taxable);
        Assert.Equal(1_800m, result.Declarants[0].ExemptNet);
    }

    [Fact]
    public void Unused_ceilings_are_added_only_to_an_estimated_ceiling()
    {
        var notice = Other(30_000m) with { PerCeiling = 4_000m, PerUnusedCeiling = 3_000m };
        var estimated = Other(30_000m) with { PerUnusedCeiling = 3_000m };

        Assert.Equal(4_000m, TaxCalculator.Compute(Inputs([notice])).Declarants[0].Per!.Ceiling);
        Assert.Equal(7_637m, TaxCalculator.Compute(Inputs([estimated])).Declarants[0].Per!.Ceiling); // 4 637 + 3 000
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

        Assert.Equal(1_080m, result.DonationReduction); // 2025, before 14 October: 75 % × 1 000 + 66 % × 500
    }

    [Theory]
    [InlineData(2025, 1_500, 1_500, 2_160)] // 75 % × (1 000 + 1 500 → 2 000) + 66 % × 1 000
    [InlineData(2025, 0, 2_500, 1_830)] // 75 % × 2 000 + 66 % × 500
    [InlineData(2026, 2_500, 0, 1_830)] // 2 000 for the whole year
    public void Aid_donations_are_capped_at_2_000_from_14_october_2025(int year, int early, int late, int reduction)
    {
        var result = TaxCalculator.Compute(Inputs([Other(60_000m), Other(0m)], year: year) with { DonationsAid = early, DonationsAidLate = late });

        Assert.Equal(reduction, result.DonationReduction);
    }

    [Fact]
    public void Aid_donations_stay_outside_the_20_percent()
    {
        // 90 000 taxable: 20 % is 18 000 for the other donations, the 1 000 beyond the 2 000 included; the 2 000 at 75 % beside it
        // (BOI-IR-RICI-250-30 § 35). 75 % × 2 000 + 66 % × 18 000.
        var result = TaxCalculator.Compute(Inputs([Other(100_000m)], year: 2026) with { DonationsAid = 3_000m, Donations = 17_000m });

        Assert.Equal(13_380m, result.DonationReduction);
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
        var inputs = Inputs([Other(50_000m) with { ExemptIncome = 2_400.60m }, Other(20_000m) with { ExemptIncome = 500m }])
            with { CapitalIncome = 1_200m, NonTaxableIncome = 3_000m };

        var result = TaxCalculator.Compute(inputs);

        Assert.Equal(63_000m, result.NetTaxableIncome);
        // 63 000 + 1 200 of dividends and interest + 2 610.54 of exempt overtime after the 10 % allowance (2 160.54 + 450), to the
        // euro below; family benefits stay out.
        Assert.Equal(66_810m, result.ReferenceIncome);
    }

    [Fact]
    public void The_reference_income_counts_exempt_overtime_after_the_ten_percent_allowance()
    {
        // A notice: 52 000 of salaries, 1 250 of exempt overtime declared, 1 125 net in the reference income.
        var result = TaxCalculator.Compute(Inputs([Other(52_000m) with { ExemptIncome = 1_250m }]));

        Assert.Equal(46_800m, result.NetTaxableIncome);
        Assert.Equal(47_925m, result.ReferenceIncome);
    }

    [Fact]
    public void The_reference_income_counts_exempt_overtime_whole_under_actual_expenses()
    {
        var result = TaxCalculator.Compute(Inputs([Other(50_000m) with { ActualExpenses = 3_000m, ExemptIncome = 1_000m }]));

        Assert.Equal(47_000m, result.NetTaxableIncome);
        Assert.Equal(48_000m, result.ReferenceIncome);
    }

    [Fact]
    public void The_exempt_overtime_allowance_stops_at_the_ceiling()
    {
        // 150 000 already reaches the 14 555 ceiling: the overtime takes no allowance of its own.
        var result = TaxCalculator.Compute(Inputs([Other(150_000m) with { ExemptIncome = 2_000m }]));

        Assert.Equal(135_445m, result.NetTaxableIncome);
        Assert.Equal(137_445m, result.ReferenceIncome);
    }

    [Theory]
    [InlineData(41_447, 4_144.7)]
    [InlineData(3_000, 509)]   // the minimum
    [InlineData(400, 400)]     // never more than the salaries
    [InlineData(200_000, 14_555)] // the maximum
    public void The_salary_allowance_is_ten_percent_between_its_bounds(double income, double allowance)
        => Assert.Equal((decimal)allowance, TaxRules.For(2025).SalaryAllowance((decimal)income));

    /// <summary>Spec §4bis, art. 1729 G and 204 J CGI: a cut of 5 % to 10 %, never beyond 10 % with the rate one tenth lower.</summary>
    private static WithholdingCut SafeCut(TaxResult result)
    {
        var (w, cut) = (result.Withholding, result.Withholding.Cut!);
        Assert.InRange(cut.Rate, 0.9m * w.Rate + 0.1m, 0.95m * w.Rate);
        Assert.Equal(w.Annual - cut.Annual, cut.Less);
        Assert.Equal(result.IncomeTax - cut.Annual, cut.Balance);
        Assert.True(cut.Balance <= 0m);
        return cut;
    }

    private static TaxInputs YoungCouple() => Inputs([
        new("A", 50_000m, EmploymentStatus.NonExecutive, 0m, 0m),
        new("B", 40_000m, EmploymentStatus.Executive, 0m, 0m)], dependents: 2) with { ChildrenUnder6 = 2, ChildcareOutsideHome = 6_000m };

    [Fact]
    public void The_safe_cut_lowers_both_salaries_in_the_same_ratio()
    {
        var result = TaxCalculator.Compute(YoungCouple());

        // 4.5 % × 90 % + 0.1 = 4.15, so 4.2 % on the real 71 564.33: 3 005.70, 214.69 less than at 4.5 %.
        var cut = SafeCut(result);
        Assert.Equal(4.2m, cut.Rate);
        Assert.Equal(3_005.70m, cut.Annual);
        Assert.Equal(214.69m, cut.Less);
        Assert.Equal(141.49m, cut.Declarants[0].Monthly); // 4.2 % × 40 424.63 ÷ 12, on the real salaries
        var (a, b) = (cut.Declarants[0].Salaries, cut.Declarants[1].Salaries);
        Assert.True(a < result.Withholding.Declarants[0].Salaries && b < result.Withholding.Declarants[1].Salaries);
        Assert.Equal(result.Withholding.Declarants[0].Salaries / result.Withholding.Declarants[1].Salaries, a / b, 3);
        // The entered salaries do give that rate.
        var entered = YoungCouple() with
        {
            Declarants = [new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: a), new("B", 0m, EmploymentStatus.Executive, 0m, 0m, SalaryTaxable: b)],
        };
        Assert.Equal(4.2m, TaxCalculator.Compute(entered).Withholding.Rate);
    }

    [Fact]
    public void A_single_declarant_with_home_employment_gets_a_safe_cut()
    {
        var result = TaxCalculator.Compute(Inputs([new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 60_000m)]) with { HomeEmployment = 10_000m });

        var cut = SafeCut(result);
        Assert.Single(cut.Declarants);
        Assert.True(cut.Declarants[0].Salaries < 60_000m);
    }

    [Fact]
    public void Rental_income_stays_as_it_is_and_its_instalment_follows_the_cut_rate()
    {
        var result = TaxCalculator.Compute(Inputs([Other(40_000m), Other(0m)]) with { RentalGross = 10_000m, Donations = 2_000m });

        var cut = SafeCut(result);
        Assert.Equal(0m, cut.Declarants[1].Salaries);
        Assert.Equal(Math.Round((cut.Rate / 100 + 0.172m) * 7_000m / 12, 2, MidpointRounding.AwayFromZero), cut.RentalMonthly);
    }

    [Fact]
    public void The_real_rate_is_the_income_tax_over_the_withheld_income_and_the_gap_is_what_is_lent_each_month()
    {
        // 9 300 withheld at 15.5 % for an income tax of 8 644 (a 656 € refund): 14.4 %, 54.67 a month too many.
        var result = TaxCalculator.Compute(Inputs([Other(60_000m)]) with { Donations = 1_000m });

        Assert.Equal(14.4m, TaxCalculator.RealRate(result));
        Assert.Equal(54.67m, TaxCalculator.MonthlyGap(result, result.Withholding.Declarants[0]));
    }

    [Fact]
    public void The_couples_monthly_gaps_add_up_to_the_refund()
    {
        var result = TaxCalculator.Compute(YoungCouple());

        var gaps = result.Withholding.Declarants.Sum(d => TaxCalculator.MonthlyGap(result, d));
        Assert.InRange(gaps, -result.Withholding.Balance / 12 - 0.02m, -result.Withholding.Balance / 12 + 0.02m); // each monthly figure is to the cent
        Assert.True(TaxCalculator.RealRate(result) < result.Withholding.Rate);
    }

    [Fact]
    public void Without_income_there_is_no_real_rate_and_no_gap()
    {
        var result = TaxCalculator.Compute(Inputs([Other(0m)]));

        Assert.Null(TaxCalculator.RealRate(result));
        Assert.Equal(0m, TaxCalculator.MonthlyGap(result, result.Withholding.Declarants[0]));
    }

    [Fact]
    public void Without_reductions_or_credits_there_is_no_cut()
        => Assert.Null(TaxCalculator.Compute(Inputs([Other(60_000m), Other(30_000m)])).Withholding.Cut);

    [Fact]
    public void The_cut_stops_where_the_refund_ends()
    {
        // 60 000 at 15.5 %: 9 300 withheld. A 250 € donation leaves a refund of 161 €, under the 5 % minimum (465 €).
        Assert.Null(TaxCalculator.Compute(Inputs([Other(60_000m)]) with { Donations = 250m }).Withholding.Cut);

        // 1 000 € (660 € off): a 656 € refund, so 14.5 % (8 700, 600 less) rather than the 14.1 % the 10 % would allow.
        var result = TaxCalculator.Compute(Inputs([Other(60_000m)]) with { Donations = 1_000m });
        var cut = SafeCut(result);
        Assert.Equal(-656m, result.Withholding.Balance);
        Assert.Equal(14.5m, cut.Rate);
        Assert.Equal(600m, cut.Less);
    }

    [Theory]
    [InlineData(30_000)]
    [InlineData(45_000)]
    [InlineData(71_000)]
    [InlineData(120_000)]
    public void The_rounded_cut_rate_never_goes_beyond_ten_percent(double salaries)
    {
        var result = TaxCalculator.Compute(Inputs([Other((decimal)salaries)]) with { HomeEmployment = 12_000m });
        if (result.Withholding.Cut is not null)
            SafeCut(result);
    }
}
