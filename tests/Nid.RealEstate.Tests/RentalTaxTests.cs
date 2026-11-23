using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.RealEstate.Application;
using Nid.RealEstate.Contracts;

namespace Nid.RealEstate.Tests;

/// <summary>Rental 2044 spec §3: the category → line table.</summary>
public class RentalTaxTests
{
    [Fact]
    public void Each_category_has_its_one_line_or_none()
    {
        var lines = PropertyCategories.Income.Concat(PropertyCategories.Expense).ToDictionary(c => c, RentalTax.LineOf);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["rent"] = TaxLineCodes.Rental211, ["recoverable_charges"] = null, ["other_income"] = null,
            ["management_fees"] = TaxLineCodes.Rental221, ["insurance"] = TaxLineCodes.Rental223, ["maintenance"] = TaxLineCodes.Rental224,
            ["property_tax"] = TaxLineCodes.Rental227, ["condo_fees"] = TaxLineCodes.Rental229, ["energy"] = null,
            // Repairs and improvement are deductible; building or enlarging never is, so it stays to check, uncounted.
            ["works"] = TaxLineCodes.Rental224, ["construction_works"] = TaxLineCodes.RentalToCheck, ["other"] = TaxLineCodes.RentalToCheck,
        }, lines);
    }
}
