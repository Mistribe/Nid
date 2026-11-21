using Nid.Core.Contracts;

namespace Nid.Core.Tests;

public sealed class DecimalsTests
{
    [Theory]
    [InlineData(1.01, "1.01")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.0302, "1.0302")]
    [InlineData(100.1, "100.1")]
    [InlineData(-0.05, "-0.05")]
    [InlineData(0, "0")]
    [InlineData(1e-5, "0.00001")]
    [InlineData(123456789012.345678, "123456789012.346")]
    public void A_double_keeps_15_significant_digits(double value, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Decimals.FromDouble(value));

    [Fact]
    public void Binary_noise_past_the_15th_digit_is_dropped()
        => Assert.Equal(1.01m, Decimals.FromDouble(Math.Pow(1.01, 1.0)));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e30)]
    public void A_value_decimal_cannot_hold_overflows_like_a_cast(double value)
        => Assert.Throws<OverflowException>(() => Decimals.FromDouble(value));
}
