using Nid.Tax.Application;
using Nid.Tax.Domain;

namespace Nid.Tax.Tests;

/// <summary>#726: a tip whose saving exceeds the year's total tax states the refund the tax would turn into.</summary>
public class TaxTipRefundsTests
{
    private static readonly TaxResult Base = TaxCalculator.Compute(new TaxInputs(
        2025, [new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: 30_000m)], 0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m));

    [Fact]
    public void A_saving_above_the_total_tax_gives_its_refund_and_one_below_none()
    {
        var r = Base with { TotalTax = 300m, Per = new PerHousehold(false, 5_000m, 0m, 0m, RemainingTaxSaved: 1_100m), RentalRegimeGap = -200m, CapitalOptionGap = -450m };

        Assert.Equal(new TaxTipRefundsView("800.00", null, "150.00"), TaxTipRefundsView.Of(r));
    }

    [Fact]
    public void No_refund_when_no_saving_exceeds_the_tax()
        => Assert.Null(TaxTipRefundsView.Of(Base with { TotalTax = 300m, Per = null, RentalRegimeGap = 120m, CapitalOptionGap = -300m }));
}
