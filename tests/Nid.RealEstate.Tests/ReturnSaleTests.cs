using Nid.Core.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Property return spec §4.3 and §5: what the return hands the tax rules about the property.</summary>
public class ReturnSaleTests
{
    private static readonly PropertyDetails Bought = new(PropertyType.Rental, new LocalDate(2020, 1, 1), 150_000m, 10_000m, 2_000m, 0m,
        40m, null, PropertyCadence.Yearly, null, null, null);

    [Fact]
    public void Pinel_base_leaves_works_out() => Assert.Equal(162_000m, ReturnSale.PinelCostPrice(Bought));

    [Fact]
    public void Sale_counts_no_works_already_deducted()
    {
        var sale = ReturnSale.ForSale(Loans.Item(), Bought);

        Assert.Equal((0m, 12_000m, "rental"), (sale.ActualWorks, sale.ActualFees, sale.Type));
    }
}
