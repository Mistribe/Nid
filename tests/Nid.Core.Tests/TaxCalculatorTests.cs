using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Tax estimate spec §4: examples worked by hand on the 2025 revenue rules.</summary>
public class TaxCalculatorTests
{
    private static TaxInputs Inputs(IReadOnlyList<TaxDeclarant> declarants, int dependents = 0, int year = 2025) => new(
        year, declarants, dependents, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    private static TaxDeclarant Other(decimal taxable) => new("A", 0m, EmploymentStatus.NonExecutive, taxable, 0m);

    private static TaxDeclarant Pensioner(string name, decimal pensions) => new(name, 0m, EmploymentStatus.NonExecutive, 0m, 0m, Pensions: pensions);

    [Fact]
    public void A_small_pension_takes_the_pensioners_minimum_allowance()
    {
        var result = TaxCalculator.Compute(Inputs([Pensioner("A", 3_000m)]));

        // 10 % is 300, under the 454 € minimum.
        Assert.Equal(454m, result.Declarants[0].PensionAllowance);
        Assert.Equal(2_546m, result.Declarants[0].Taxable);
        // The minimum never exceeds the pension.
        Assert.Equal(200m, TaxCalculator.Compute(Inputs([Pensioner("A", 200m)])).Declarants[0].PensionAllowance);
    }

    [Fact]
    public void The_pension_allowance_is_capped_for_the_household_and_shared_in_proportion()
    {
        var result = TaxCalculator.Compute(Inputs([Pensioner("A", 30_000m), Pensioner("B", 25_000m)]));

        // 3 000 + 2 500 over the 4 439 € household cap: 4 439 × 30 / 55 and × 25 / 55.
        Assert.Equal(2_421.27m, result.Declarants[0].PensionAllowance);
        Assert.Equal(2_017.73m, result.Declarants[1].PensionAllowance);
        Assert.Equal(50_561m, result.NetTaxableIncome);
    }

    [Fact]
    public void Salary_and_pension_keep_their_own_allowance()
    {
        var result = TaxCalculator.Compute(Inputs([new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 20_000m, Pensions: 6_000m)]));

        Assert.Equal(2_000m, result.Declarants[0].Allowance);
        Assert.Equal(600m, result.Declarants[0].PensionAllowance);
        Assert.Equal(23_400m, result.Declarants[0].Taxable);
        // The pensions are withheld at the household rate beside the salaries.
        Assert.Equal(26_000m, result.Withholding.Income);
        Assert.Equal(6_000m, result.Withholding.Declarants[0].Pensions);
    }

    [Fact]
    public void Alimony_paid_comes_off_the_global_income_the_adult_childrens_within_their_cap()
    {
        // 50 000 − the 10 % allowance: 45 000 of global income.
        var inputs = Inputs([Other(50_000m)]);

        Assert.Equal(42_000m, TaxCalculator.Compute(inputs with { AlimonyPaid = 3_000m }).NetTaxableIncome);
        // Two adult children, 15 000 paid: 2 × 6 855 deducted.
        var capped = TaxCalculator.Compute(inputs with { AlimonyAdultChildren = 15_000m, AlimonyAdultChildrenCount = 2 });
        Assert.Equal(13_710m, capped.AlimonyDeducted);
        Assert.Equal(31_290m, capped.NetTaxableIncome);
        // The reference income does not add it back.
        Assert.Equal(31_290m, capped.ReferenceIncome);
    }

    [Theory]
    // dependents, of them in alternate custody, box T, disabled declarant → shares, cap
    [InlineData(1, 0, true, false, 2.0, 4_262)]   // box T with the first child: 4 262 for both half-shares
    [InlineData(1, 0, false, false, 1.5, 1_807)]  // living with another adult: no box T
    [InlineData(2, 2, true, false, 1.75, 3_035)]  // alternate custody only: two quarters, box T a quarter, 2 131 for the first pair
    [InlineData(3, 2, false, false, 2.25, 1_807 + 904 + 1_808)] // the child at home ranks first; the third child's half share in quarters
    [InlineData(0, 0, true, true, 1.5, 3_608)]    // box P, no child: no box T
    public void Shares_and_their_cap_follow_the_household(int dependents, int alternate, bool single, bool disabled, double parts, int cap)
    {
        var inputs = Inputs([new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, Disabled: disabled)], dependents)
            with { AlternateCustody = alternate, SingleParent = single };

        Assert.Equal(((decimal)parts, (decimal)cap), TaxCalculator.Shares(TaxRules.For(2025), inputs));
    }

    [Theory]
    // dependents, of them in alternate custody, disabled dependents → shares, cap (a couple)
    [InlineData(1, 0, 1, 3.0, 1_807 + 3_608)]           // box G: half a share more, capped with its complementary reduction
    [InlineData(2, 2, 1, 2.75, 904 * 2 + 3_608 / 2)]     // box I: a quarter share, half the cap
    [InlineData(2, 1, 1, 3.25, 1_807 + 904 + 3_608)]     // the disabled child is taken at home first
    [InlineData(1, 0, 3, 3.0, 1_807 + 3_608)]           // never more disabled children than dependents
    public void A_disabled_dependent_adds_a_share_and_its_cap(int dependents, int alternate, int disabled, double parts, int cap)
    {
        var inputs = Inputs([Other(0m), Other(0m)], dependents) with { AlternateCustody = alternate, DisabledDependents = disabled };

        Assert.Equal(((decimal)parts, (decimal)cap), TaxCalculator.Shares(TaxRules.For(2025), inputs));
    }

    [Theory]
    // net global income, qualifying declarants → allowance (revenus 2025: 2 822 up to 17 670, 1 411 up to 28 430)
    [InlineData(17_670, 1, 2_822)]
    [InlineData(17_671, 1, 1_411)]
    [InlineData(28_430, 2, 2_822)]
    [InlineData(28_431, 2, 0)]
    [InlineData(2_000, 2, 2_000)]  // never more than the income
    [InlineData(10_000, 0, 0)]
    public void The_age_or_disability_allowance_follows_the_income_brackets(int income, int qualifying, int allowance)
        => Assert.Equal((decimal)allowance, TaxRules.For(2025).AgeDisabilityAllowance(income, qualifying));

    [Fact]
    public void The_age_or_disability_allowance_counts_each_declarant_over_65_or_disabled_once()
    {
        // 1AS 15 000 and 1BS 12 000: 27 000 − 2 700 of the pensions' 10 % = 24 300, in the upper bracket.
        var both = Inputs([Pensioner("A", 15_000m) with { Over65 = true, Disabled = true }, Pensioner("B", 12_000m) with { Disabled = true }]);
        var none = Inputs([Pensioner("A", 15_000m), Pensioner("B", 12_000m)]);

        var result = TaxCalculator.Compute(both);
        Assert.Equal(2_822m, result.AgeDisabilityAllowance);
        Assert.Equal(24_300m - 2_822m, result.NetTaxableIncome);
        Assert.Equal(0m, TaxCalculator.Compute(none).AgeDisabilityAllowance);
    }

    [Fact]
    public void A_couple_never_takes_box_t_and_each_disabled_declarant_adds_half_a_share()
    {
        var inputs = Inputs([new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, Disabled: true), new("B", 0m, EmploymentStatus.NonExecutive, 0m, 0m, Disabled: true)], dependents: 1)
            with { SingleParent = true };

        Assert.Equal((3.5m, 2 * 3_608m + 1_807m), TaxCalculator.Shares(TaxRules.For(2025), inputs));
    }

    [Fact]
    public void A_child_in_alternate_custody_raises_the_home_employment_cap_by_half()
    {
        var inputs = Inputs([Other(80_000m)], dependents: 2) with { AlternateCustody = 1, HomeEmployment = 20_000m };

        // 12 000 + 1 500 + 750 = 14 250, half of it as a credit.
        Assert.Equal(7_125m, TaxCalculator.Compute(inputs).HomeEmploymentCredit);
    }

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
    [InlineData(null, 10_000, 0, 0, 0, 0)] // micro, nothing carried
    [InlineData(null, 10_000, 0, 0, 9_000, 2_000)] // micro: 7 000 absorbed of the 9 000 carried
    [InlineData(RentalRegime.Real, 12_000, 14_000, 3_000, 0, 2_000)] // the interest beyond the rents
    [InlineData(RentalRegime.Real, 5_000, 0, 20_000, 0, 4_300)] // beyond the 10 700 the global income takes
    [InlineData(RentalRegime.Real, 5_000, 0, 20_000, 1_000, 5_300)] // and the earlier deficit the year's loss leaves whole
    [InlineData(RentalRegime.Real, 10_000, 2_000, 5_000, 4_000, 1_000)] // a profit of 3 000 absorbs 3 000 of the 4 000
    public void The_rental_deficit_left_is_carried_forward(RentalRegime? regime, int gross, int interest, int charges, int carried, int forward)
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m), Other(0m)]) with
        {
            RentalGross = gross, RentalInterest = interest, RentalCharges = charges, RentalRegime = regime, RentalDeficitCarried = carried,
        });

        Assert.Equal(forward, result.RentalDeficitCarriedForward);
    }

    [Fact]
    public void The_carried_forward_deficit_is_the_2044_carried_lines()
    {
        // 2044 lines 441 and the interest's: what the form carries is what the estimate carries, with nothing carried before.
        var columns = Rental2044.Columns([new(Guid.NewGuid(), "A", 5_000m, 3_000m, 20m, 1_000m, 9_000m, 1_200m, 980m, 8_000m)]);
        var totals = Rental2044.Totals(columns, 10_700m);
        var result = TaxCalculator.Compute(Inputs([Other(30_000m)]) with
        {
            RentalGross = totals.Receipts, RentalCharges = totals.Charges, RentalInterest = totals.Interest, RentalRegime = RentalRegime.Real,
        });

        Assert.Equal(totals.CarriedDeficit, result.RentalDeficitCarriedForward);
        Assert.Equal(totals.GlobalDeficit, result.RentalDeficit);
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
    [InlineData(2027, 4_806, 38_448)] // the 2026 PASS, 48 060
    [InlineData(2028, 4_806, 38_448)] // carried forward
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
        Assert.Contains(TaxWarnings.RulesProvisional, result.Warnings);
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

    private static TaxInputs WithCapital(decimal salary, decimal capital, decimal dividends, bool scale)
        => Inputs([new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: salary)]) with
        {
            CapitalIncome = capital, CapitalDividends = dividends, CapitalScale = scale,
        };

    [Fact]
    public void Under_the_flat_tax_capital_income_pays_12_8_percent_and_stays_out_of_the_scale()
    {
        // 40 000 entered, 36 000 after the allowance: 1 977.69 + 6 421 × 30 % on the scale.
        var result = TaxCalculator.Compute(WithCapital(40_000m, 5_000m, 4_000m, scale: false));

        Assert.Equal((36_000m, 3_904m, 640m, 860m), (result.NetTaxableIncome, result.IncomeTax, result.CapitalFlatTax, result.SocialLevies));
        Assert.Equal((0m, 0m, false), (result.DividendAllowance, result.DeductibleCsgNextYear, result.CapitalScale));
        Assert.Equal(41_000m, result.ReferenceIncome);
        // The scale: 39 400 taxable, 4 924 of tax against 3 904 + 640.
        Assert.Equal(380m, result.CapitalOptionGap);
    }

    [Fact]
    public void Under_the_scale_the_dividends_lose_40_percent_and_the_csg_becomes_deductible()
    {
        var result = TaxCalculator.Compute(WithCapital(40_000m, 5_000m, 4_000m, scale: true));

        // 36 000 + 1 000 of interest + 4 000 × 60 %.
        Assert.Equal((39_400m, 4_924m, 0m, 860m), (result.NetTaxableIncome, result.IncomeTax, result.CapitalFlatTax, result.SocialLevies));
        Assert.Equal((1_600m, 340m, true), (result.DividendAllowance, result.DeductibleCsgNextYear, result.CapitalScale));
        // The reference income adds the allowance back: the same as under the flat tax.
        Assert.Equal(41_000m, result.ReferenceIncome);
        Assert.Equal(-380m, result.CapitalOptionGap);
        // The capital income is outside the withholding: 4 923.99 × 36 000 / 39 400 over the 40 000 of salaries.
        Assert.Equal(11.2m, result.Withholding.Rate);
    }

    [Fact]
    public void A_low_income_household_saves_the_flat_tax_under_the_scale()
    {
        // 10 800 + 3 000 × 60 % = 12 600: 110 of tax, wiped by the decote.
        var result = TaxCalculator.Compute(WithCapital(12_000m, 3_000m, 3_000m, scale: false));

        Assert.Equal(384m, result.CapitalFlatTax);
        Assert.Equal(-384m, result.CapitalOptionGap);
        Assert.Null(TaxCalculator.Compute(Inputs([Other(30_000m)])).CapitalOptionGap);
    }

    [Fact]
    public void The_deductible_csg_comes_off_the_global_income()
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m)]) with { DeductibleCsg = 500m });

        // 30 000 − the 10 % allowance − 500.
        Assert.Equal(26_500m, result.NetTaxableIncome);
        Assert.Equal(26_500m, result.ReferenceIncome);
    }

    [Theory]
    // Single: 3 % from 250 000; couple: 3 % from 500 000, 4 % beyond 1 000 000 (BOI-IR-CHR § 1 examples).
    [InlineData(250_000, false, 0)]
    [InlineData(300_000, false, 1_500)]
    [InlineData(1_500_000, false, 47_500)]
    [InlineData(700_000, true, 6_000)]
    [InlineData(1_500_000, true, 35_000)]
    public void The_high_income_contribution_follows_the_reference_income(int reference, bool couple, int expected)
        => Assert.Equal(((decimal)expected, false), TaxCalculator.HighIncome(reference, couple, null));

    [Fact]
    public void A_first_year_over_the_threshold_is_smoothed_by_the_quotient()
    {
        // BOI-IR-CHR § 220: a couple at 3 500 000 after 340 000 and 270 000: base 1 902 500, twice 51 100 instead of 115 000.
        Assert.Equal((102_200m, true), TaxCalculator.HighIncome(3_500_000m, true, (340_000m, 270_000m)));
        Assert.Equal((115_000m, false), TaxCalculator.HighIncome(3_500_000m, true, null));
        // An earlier year over the threshold shuts the quotient.
        Assert.Equal((115_000m, false), TaxCalculator.HighIncome(3_500_000m, true, (600_000m, 270_000m)));
    }

    [Fact]
    public void The_high_income_contribution_is_part_of_the_total()
    {
        // 314 555 − the 14 555 allowance cap: a reference income of 300 000.
        var result = TaxCalculator.Compute(Inputs([Other(314_555m)]));

        Assert.Equal(1_500m, result.HighIncomeContribution);
        Assert.Equal(result.IncomeTax + result.SocialLevies + result.CapitalFlatTax + 1_500m, result.TotalTax);
    }

    [Fact]
    public void Realised_gains_are_taxed_with_the_capital_income_after_the_earlier_losses()
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m)]) with { RealisedGains = 2_000m, CapitalLossesCarried = 500m });

        // 1 500 left at 12.8 % and, as patrimony income, at the 18.6 % that already reaches 2025 gains; in the reference income.
        Assert.Equal((1_500m, 0m, 192m, 279m), (result.RealisedGainsTaxable, result.CapitalLossesCarriedForward, result.CapitalFlatTax, result.SocialLevies));
        Assert.Equal(27_000m + 1_500m, result.ReferenceIncome);
    }

    [Fact]
    public void A_net_loss_is_carried_forward_with_the_earlier_ones_and_taxes_nothing()
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m)]) with { RealisedGains = -800m, CapitalLossesCarried = 300m, CapitalIncome = 1_000m });

        Assert.Equal((0m, 1_100m, 128m), (result.RealisedGainsTaxable, result.CapitalLossesCarriedForward, result.CapitalFlatTax));
    }

    [Fact]
    public void Under_the_scale_the_gains_join_the_global_income_without_the_dividends_allowance()
    {
        var result = TaxCalculator.Compute(Inputs([Other(30_000m)]) with { RealisedGains = 1_000m, CapitalDividends = 1_000m, CapitalIncome = 1_000m, CapitalScale = true });

        // 27 000 + 1 000 of gains + 1 000 of dividends × 60 %; the CSG of both deductible next year.
        Assert.Equal((28_600m, 400m, 136m), (result.NetTaxableIncome, result.DividendAllowance, result.DeductibleCsgNextYear));
    }

    [Fact]
    public void Remaining_tax_saved_is_the_recomputed_gain()
    {
        var a = Other(60_000m) with { PerContributions = 1_000m, PerCeiling = 5_000m };
        var b = Other(40_000m) with { Name = "B", PerContributions = 500m, PerCeiling = 3_000m };

        var now = TaxCalculator.Compute(Inputs([a, b]));
        var paid = TaxCalculator.Compute(Inputs([a with { PerContributions = 5_000m }, b with { PerContributions = 3_000m }]));

        Assert.Equal(6_500m, now.Per!.Remaining);
        Assert.True(now.IncomeTax > paid.IncomeTax);
        Assert.Equal(now.IncomeTax - paid.IncomeTax, now.Per.RemainingTaxSaved);
    }

    [Fact]
    public void Remaining_tax_saved_counts_pooled_room_once()
    {
        var a = Other(60_000m) with { PerCeiling = 5_000m };
        var b = Other(40_000m) with { Name = "B", PerCeiling = 4_000m };

        var now = TaxCalculator.Compute(Inputs([a, b]) with { PerPooling = true });
        // The household's 9 000 free ceiling, all paid by the first declarant (4 000 of it goes against B's room).
        var paid = TaxCalculator.Compute(Inputs([a with { PerContributions = 9_000m }, b]) with { PerPooling = true });

        Assert.Equal(9_000m, now.Per!.Remaining);
        Assert.Equal(now.IncomeTax - paid.IncomeTax, now.Per.RemainingTaxSaved);
        Assert.True(now.Per.RemainingTaxSaved > 0m);
    }

    [Fact]
    public void Remaining_tax_saved_is_zero_without_room()
    {
        var result = TaxCalculator.Compute(Inputs([Other(60_000m) with { PerContributions = 5_000m, PerCeiling = 5_000m }]));

        Assert.Equal(0m, result.Per!.Remaining);
        Assert.Equal(0m, result.Per.RemainingTaxSaved);
    }

    [Fact]
    public void Remaining_tax_saved_is_zero_in_the_zero_bracket()
    {
        var result = TaxCalculator.Compute(Inputs([Other(10_000m) with { PerCeiling = 4_000m }]));

        Assert.Equal(4_000m, result.Per!.Remaining);
        Assert.Equal(0m, result.IncomeTax);
        Assert.Equal(0m, result.Per.RemainingTaxSaved);
    }

    [Theory]
    // The official simulator (impôt 2026 sur les revenus 2025, modèle complet), 1AJ 40 000, 2DC 4 000, 2TR 1 000, 3VG 2 000, with and
    // without 2OP; the levies with 2BH 5 000, the dividends' and interest's being withheld at 17.2 %: 372 on the gains.
    [InlineData(false, 36_000, 3_904, 896)]
    [InlineData(true, 41_400, 5_524, 0)]
    public void A_capital_income_scenario_matches_the_official_simulator(bool scale, int taxable, int incomeTax, int flat)
    {
        var result = TaxCalculator.Compute(WithCapital(40_000m, 5_000m, 4_000m, scale) with { RealisedGains = 2_000m });

        Assert.Equal(((decimal)taxable, (decimal)incomeTax, (decimal)flat, 43_000m), (result.NetTaxableIncome, result.IncomeTax, result.CapitalFlatTax, result.ReferenceIncome));
        Assert.Equal(860m + 372m, result.SocialLevies);
    }
}
