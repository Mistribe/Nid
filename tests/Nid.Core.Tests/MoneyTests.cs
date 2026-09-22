using Nid.Core.Contracts;

namespace Nid.Core.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData("EUR")]
    [InlineData("USD")]
    public void Currency_accepts_three_uppercase_letters(string code)
    {
        Assert.True(Currency.TryCreate(code, out var currency));
        Assert.Equal(code, currency.Code);
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    [InlineData("")]
    public void Currency_rejects_anything_else(string code)
        => Assert.False(Currency.TryCreate(code, out _));

    [Fact]
    public void Money_accepts_up_to_four_decimals()
    {
        var result = Money.Create(12.3456m, Currency.Eur);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Money_refuses_more_than_four_decimals()
    {
        var result = Money.Create(0.00005m, Currency.Eur);
        Assert.True(result.IsFailure);
        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal("core.money_scale", failure!.Code);
    }

    [Fact]
    public void Money_ignores_trailing_zeros_when_counting_decimals()
        => Assert.True(Money.Create(1.50000m, Currency.Eur).IsSuccess);

    [Fact]
    public void Money_serializes_with_invariant_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
        try
        {
            Money.Create(665262.12m, Currency.Eur).TryGetValue(out var money);
            Assert.Equal("665262.12", money.AmountText);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Money_parses_wire_amounts_with_invariant_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
        try
        {
            Assert.True(Money.TryParse("665262.12", "EUR", out var money));
            Assert.Equal(665262.12m, money.Amount);
            Assert.False(Money.TryParse("665262,12", "EUR", out _));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
