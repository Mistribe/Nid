using Nid.Investments.Infrastructure.Persistence;

namespace Nid.Investments.Tests;

public class SnakeCaseTests
{
    [Theory]
    [InlineData("PremiumsBefore2017", "premiums_before_2017")]
    [InlineData("NetAnnualized5yPct", "net_annualized_5y_pct")]
    [InlineData("UcShareFloorPct", "uc_share_floor_pct")]
    public void A_column_name_is_snake_cased_with_its_digits_set_apart(string property, string column)
        => Assert.Equal(column, InvestmentsDbContext.ToSnakeCase(property));
}
