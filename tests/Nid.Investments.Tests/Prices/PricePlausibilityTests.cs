using Nid.Investments.Domain.Prices;
using NodaTime;

namespace Nid.Investments.Tests.Prices;

public class PricePlausibilityTests
{
    private static readonly LocalDate Today = new(2026, 10, 23);

    [Theory]
    [InlineData(119, 100, "Accepted")]
    [InlineData(121, 100, "Rejected")]
    [InlineData(81, 100, "Accepted")]
    [InlineData(79, 100, "Rejected")]
    public void Change_against_the_reference(decimal price, decimal reference, string verdict)
        => Assert.Equal(Enum.Parse<PriceVerdict>(verdict), PricePlausibility.Check(new(price, "EUR", Today), reference, Today, 0.20m, 10));

    [Fact]
    public void Without_reference_it_is_accepted()
        => Assert.Equal(PriceVerdict.Accepted, PricePlausibility.Check(new(100m, "EUR", Today), null, Today, 0.20m, 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Zero_or_negative_is_invalid(decimal price)
        => Assert.Equal(PriceVerdict.Invalid, PricePlausibility.Check(new(price, "EUR", Today), 100m, Today, 0.20m, 10));

    [Fact]
    public void A_price_too_large_to_store_is_invalid()
        => Assert.Equal(PriceVerdict.Invalid, PricePlausibility.Check(new(100_000_000_000_000m, "EUR", Today), null, Today, 0.20m, 10));

    [Fact]
    public void A_future_date_is_invalid()
        => Assert.Equal(PriceVerdict.Invalid, PricePlausibility.Check(new(100m, "EUR", Today.PlusDays(1)), 100m, Today, 0.20m, 10));

    [Fact]
    public void Eleven_days_old_is_invalid_ten_is_not()
    {
        Assert.Equal(PriceVerdict.Invalid, PricePlausibility.Check(new(100m, "EUR", Today.PlusDays(-11)), 100m, Today, 0.20m, 10));
        Assert.Equal(PriceVerdict.Accepted, PricePlausibility.Check(new(100m, "EUR", Today.PlusDays(-10)), 100m, Today, 0.20m, 10));
    }
}
