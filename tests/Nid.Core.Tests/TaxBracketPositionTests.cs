using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

public class TaxBracketPositionTests
{
    private static readonly TaxRules Rules = TaxRules.For(2026);

    [Fact]
    public void Quotient_is_taxable_per_part() =>
        Assert.Equal(29_560m, TaxBracketPosition.Of(Rules, 73_900m, 2.5m).Quotient);

    [Fact]
    public void Next_bracket_is_the_first_above()
    {
        var p = TaxBracketPosition.Of(Rules, 73_900m, 2.5m);
        var next = Rules.Brackets.First(b => b.From > 29_560m).From;

        Assert.Equal(next, p.NextFrom);
        Assert.Equal(next - 29_560m, p.ToNext);
        Assert.Equal(Rules.Brackets.Count, p.Bands.Count);
        Assert.Equal(Rules.Brackets[1].From, p.Bands[0].To);
        Assert.Null(p.Bands[^1].To);
    }

    [Fact]
    public void Top_bracket_has_no_next()
    {
        var last = Rules.Brackets[^1].From;
        var p = TaxBracketPosition.Of(Rules, (last + 10_000m) * 2m, 2m);

        Assert.Null(p.NextFrom);
        Assert.Null(p.ToNext);
        Assert.True(p.DisplayEnd > p.Quotient);
        Assert.Equal(0m, p.DisplayEnd % 1_000m);
    }

    [Fact]
    public void Zero_income_sits_in_the_first_band()
    {
        var p = TaxBracketPosition.Of(Rules, 0m, 1m);

        Assert.Equal(0m, p.Quotient);
        Assert.Equal(Rules.Brackets[1].From, p.NextFrom);
    }
}
