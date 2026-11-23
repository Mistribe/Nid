using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Tax.Domain;

namespace Nid.Tax.Tests;

/// <summary>#512: the handlers' input checks and the observed contribution rate, pure functions of the domain.</summary>
public class TaxChecksTests
{
    private static TaxInputs Inputs(int dependents = 0, int under6 = 0, params TaxDeclarant[] declarants) => new(
        2025, declarants.Length == 0 ? [new("A", 30_000m, EmploymentStatus.NonExecutive, 0m, 0m)] : declarants, dependents, under6,
        0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

    [Theory]
    [InlineData(TaxRules.FirstYear - 1, false)]
    [InlineData(TaxRules.FirstYear, true)]
    [InlineData(2027, true)]
    [InlineData(2028, false)]
    public void A_year_runs_from_the_first_of_the_rules_to_next_year(int year, bool ok)
        => Assert.Equal(ok, TaxChecks.YearOk(year, 2026));

    [Fact]
    public void A_contribution_rate_is_a_fraction_up_to_sixty_percent()
        => Assert.Equal([true, true, false, false], new[] { 0m, 0.60m, 0.61m, -0.01m }.Select(TaxChecks.RateOk));

    [Fact]
    public void The_simulation_wants_one_or_two_declarants_and_the_children_among_the_dependents()
    {
        var adult = new TaxDeclarant("A", 30_000m, EmploymentStatus.NonExecutive, 0m, 0m);

        Assert.True(TaxChecks.InputsOk(Inputs(dependents: 2, under6: 1)));
        Assert.False(TaxChecks.InputsOk(Inputs(0, 0, adult, adult, adult)));
        Assert.False(TaxChecks.InputsOk(Inputs(dependents: 1, under6: 2)));
        Assert.False(TaxChecks.InputsOk(Inputs(dependents: 21)));
        Assert.False(TaxChecks.InputsOk(Inputs(0, 0, adult with { ContributionRate = 0.7m })));
        Assert.False(TaxChecks.InputsOk(Inputs(0, 0, adult with { PerCeiling = -1m })));
        Assert.False(TaxChecks.InputsOk(Inputs(dependents: 1) with { AlternateCustody = 2 }));
        Assert.False(TaxChecks.InputsOk(Inputs(dependents: 2, under6: 1) with { AlternateCustody = 2, AlternateUnder6 = 2 }));
    }

    [Fact]
    public void A_profile_names_known_statuses_and_keeps_its_figures_in_range()
    {
        var adult = Guid.CreateVersion7();
        var profile = TaxProfile.Empty with { Statuses = new Dictionary<Guid, string> { [adult] = "executive" }, RentalRegime = "real", Department = "2A" };

        Assert.True(TaxChecks.ProfileOk(profile));
        Assert.False(TaxChecks.ProfileOk(profile with { Statuses = new Dictionary<Guid, string> { [adult] = "boss" } }));
        Assert.False(TaxChecks.ProfileOk(profile with { RentalRegime = "flat" }));
        Assert.False(TaxChecks.ProfileOk(profile with { Department = "20" }));
        Assert.False(TaxChecks.ProfileOk(profile with { Donations = -1m }));
        Assert.False(TaxChecks.ProfileOk(profile with { PerCeilingsExpiring = new Dictionary<Guid, decimal> { [adult] = 100m } }));
        Assert.True(TaxChecks.ProfileOk(profile with
        {
            PerCeilings = new Dictionary<Guid, decimal> { [adult] = 1_000m }, PerCeilingsExpiring = new Dictionary<Guid, decimal> { [adult] = 100m },
        }));
    }

    [Fact]
    public void The_wire_names_go_both_ways()
    {
        foreach (var status in Enum.GetValues<EmploymentStatus>())
            Assert.True(TaxWire.TryStatus(TaxWire.Wire(status), out var back) && back == status);
        foreach (var regime in Enum.GetValues<RentalRegime>())
            Assert.True(TaxWire.TryRegime(TaxWire.Wire(regime), out var back) && back == regime);
        Assert.False(TaxWire.TryStatus(null, out _));
        Assert.False(TaxWire.TryRegime("flat", out _));
    }

    [Fact]
    public void The_observed_rate_reads_the_payslips_holding_both_figures_of_one_adult()
    {
        var adult = new PersonId(Guid.CreateVersion7());
        var other = new PersonId(Guid.CreateVersion7());
        var item = new ItemId(Guid.CreateVersion7());
        TaxLine Line(string code, PersonId person, decimal amount) => new(code, item, "Salary", person, amount);

        Assert.Null(ObservedRate.Of([Line(TaxLineCodes.SalaryGross, adult, 3_000m)], adult));
        var rate = ObservedRate.Of(
            [Line(TaxLineCodes.SalaryRateGross, adult, 3_000m), Line(TaxLineCodes.SalaryRateTaxable, adult, 2_400m), Line(TaxLineCodes.SalaryRateGross, other, 9_000m)],
            adult);

        // 600 lost to the contributions, the non-deductible CSG added back, over the gross.
        Assert.Equal(Math.Round((600m + TaxRules.NonDeductibleCsg * 3_000m) / 3_000m, 4, MidpointRounding.AwayFromZero), rate);
    }
}
