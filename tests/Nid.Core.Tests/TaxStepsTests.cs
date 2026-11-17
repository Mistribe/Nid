using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

public class TaxStepsTests
{
    private static TaxInputs Inputs(IReadOnlyList<TaxDeclarant> declarants, int dependents = 0) => new(
        2025, declarants, dependents, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    private static TaxDeclarant Salaried(string name, decimal salary, decimal per = 0m) =>
        new(name, 0m, EmploymentStatus.NonExecutive, 0m, per, SalaryTaxable: salary, PerCeiling: 20_000m);

    private static TaxResult Household() => TaxCalculator.Compute(Inputs([Salaried("A", 60_000m, 2_000m), Salaried("B", 30_000m)]) with
    {
        RentalGross = 10_000m,
    });

    private static IEnumerable<TaxStep> Group(TaxResult r, string group) => r.Steps!.Where(s => s.Group == group);

    [Fact]
    public void Income_steps_land_on_net_taxable_income()
    {
        var r = Household();
        var income = Group(r, TaxStepGroups.Income).ToList();

        Assert.Equal(new TaxStep("net_taxable", TaxStepGroups.Income, r.NetTaxableIncome, 0m, r.NetTaxableIncome), income[^1]);
        Assert.Equal(r.NetTaxableIncome, income[^2].To);
        Assert.Contains(income, s => s.Code == "per");
        Assert.Contains(income, s => s.Code == "rental");
    }

    [Fact]
    public void Tax_steps_land_on_total_tax()
    {
        var r = Household();
        var tax = Group(r, TaxStepGroups.Tax).ToList();

        Assert.Equal(r.IncomeTax, tax.Single(s => s.Code == "income_tax").To);
        Assert.Equal("total_tax", tax[^1].Code);
        Assert.Equal(r.TotalTax, tax[^1].To);
        Assert.Equal(r.TotalTax, tax[^2].To);
    }

    [Fact]
    public void Zero_steps_are_left_out()
    {
        var codes = TaxCalculator.Compute(Inputs([Salaried("A", 30_000m)])).Steps!.Select(s => s.Code).ToList();

        Assert.DoesNotContain("rental", codes);
        Assert.DoesNotContain("high_income", codes);
    }

    [Fact]
    public void Refundable_credits_cross_zero()
    {
        var r = TaxCalculator.Compute(Inputs([Salaried("A", 20_000m)], dependents: 1) with { ChildrenUnder6 = 1, ChildcareOutsideHome = 3_500m, HomeEmployment = 12_000m });
        var tax = Group(r, TaxStepGroups.Tax).ToList();

        Assert.True(r.IncomeTax < 0m);
        Assert.True(tax.Single(s => s.Code == "income_tax").To < 0m);
        Assert.Equal(r.TotalTax, tax[^1].To);
        Assert.Equal(r.TotalTax, tax[^2].To);
    }

    [Fact]
    public void Deficit_over_income_lands_through_rounding()
    {
        var r = TaxCalculator.Compute(Inputs([Salaried("A", 10_000m)]) with { RentalGross = 5_000m, RentalCharges = 30_000m, RentalRegime = RentalRegime.Real });
        var income = Group(r, TaxStepGroups.Income).ToList();

        Assert.Equal(0m, r.NetTaxableIncome);
        Assert.Equal(0m, income[^2].To);
        Assert.Contains(income, s => s.Code == "rounding");
        Assert.DoesNotContain(Household().Steps!, s => s.Code == "rounding" && s.Group == TaxStepGroups.Income);
    }

    [Fact]
    public void Order_is_fixed()
    {
        var r = TaxCalculator.Compute(Inputs([Salaried("A", 60_000m, 2_000m)]) with { RentalGross = 10_000m, CapitalIncome = 5_000m, CapitalScale = true });
        string[] order = ["salaries", "salary_allowance", "pensions", "pension_allowance", "other_taxable", "rental", "rental_deficit", "capital_scale",
            "deductible_csg", "per", "alimony", "age_disability", "net_taxable", "gross_tax", "decote", "pinel", "donations", "childcare",
            "home_employment", "niche_cap", "income_tax", "flat_tax", "social_levies", "high_income", "total_tax"];
        var codes = r.Steps!.Select(s => s.Code).Where(c => c != "rounding").ToList();
        var indexes = codes.Select(c => Array.IndexOf(order, c)).ToList();

        Assert.True(codes.Count > 8);
        Assert.Equal(indexes.Order(), indexes);
    }

    [Fact]
    public void Rare_lines_are_steps_with_the_calculators_own_figure()
    {
        var retired = new TaxDeclarant("A", 0m, EmploymentStatus.NonExecutive, 8_000m, 0m, Pensions: 18_000m, Over65: true);
        var r = TaxCalculator.Compute(Inputs([retired]) with
        {
            AlimonyPaid = 1_000m, Donations = 300m, PinelReduction = 200m, CapitalIncome = 3_000m,
        });
        var steps = r.Steps!.ToDictionary(s => (s.Group, s.Code));
        decimal Of(string group, string code) => steps[(group, code)].Amount;

        Assert.Equal(18_000m, Of("income", "pensions"));
        Assert.Equal(-r.Declarants[0].PensionAllowance, Of("income", "pension_allowance"));
        Assert.Equal(8_000m, Of("income", "other_taxable"));
        Assert.Equal(-r.AlimonyDeducted, Of("income", "alimony"));
        Assert.True(r.AgeDisabilityAllowance > 0m);
        Assert.Equal(-r.AgeDisabilityAllowance, Of("income", "age_disability"));
        Assert.DoesNotContain(r.Steps!, s => s.Group == "income" && s.Code == "rounding");
        Assert.Equal(-r.DonationReduction, Of("tax", "donations"));
        Assert.Equal(-r.PinelReduction, Of("tax", "pinel"));
        Assert.Equal(r.CapitalFlatTax, Of("tax", "flat_tax"));
        Assert.Equal(r.SocialLevies, Of("tax", "social_levies"));
        Assert.Equal(r.TotalTax, r.Steps!.Last(s => s.Group == "tax" && s.Code != "total_tax").To);
    }
}
