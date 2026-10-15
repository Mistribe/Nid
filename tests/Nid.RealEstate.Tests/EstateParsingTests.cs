using Nid.RealEstate.Application.Import;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3F1 §2: a rule's API field, as the file's member an import issue names.</summary>
public class EstateParsingTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("name", ".name")]
    [InlineData("purchaseDate", ".purchase_date")]
    [InlineData("purchasePrice", ".purchase_price.amount")]
    [InlineData("surfaceM2", ".surface_m2")]
    [InlineData("every.unit", ".every.unit")]
    [InlineData("rentMonthly", ".rent_monthly.amount")]
    public void A_rule_field_is_named_as_the_file_member(string field, string member)
        => Assert.Equal(member, EstateParsing.Member(field));
}
