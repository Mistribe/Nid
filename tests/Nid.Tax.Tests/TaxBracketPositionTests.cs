using Nid.Tax.Application;
using Nid.Tax.Domain;

namespace Nid.Tax.Tests;

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
        Assert.Equal(233_000m, p.DisplayEnd); // last From 185 737 x 1.25 wins, rounded up
        Assert.Equal(330_000m, TaxBracketPosition.Of(Rules, 300_000m, 1m).DisplayEnd); // quotient x 1.1 wins
    }

    [Fact]
    public void Quotient_on_a_band_start_is_still_below_it()
    {
        var from = Rules.Brackets[2].From;
        var p = TaxBracketPosition.Of(Rules, from * 2m, 2m);

        Assert.Equal(from, p.NextFrom);
        Assert.Equal(0m, p.ToNext);
        Assert.Equal(Rules.Brackets[1].Rate, TaxCalculator.MarginalRate(Rules, p.Quotient));
    }

    [Fact]
    public void Bands_carry_the_rules_rates() =>
        Assert.Equal(Rules.Brackets.Select(b => b.Rate), TaxBracketPosition.Of(Rules, 50_000m, 1m).Bands.Select(b => b.Rate));

    [Fact]
    public void View_maps_bands_and_amounts()
    {
        var v = TaxBracketView.Of(TaxBracketPosition.Of(Rules, 73_900m, 2.5m));

        Assert.Equal(new TaxBandView("11844.00", "30200.00", "11"), v.Bands[1]);
        Assert.Equal("30", v.Bands[2].Rate);
        Assert.Null(v.Bands[^1].To);
        Assert.Equal("29560.00", v.Quotient);
        Assert.Equal("640.00", v.ToNext);
    }

    [Fact]
    public void Zero_income_sits_in_the_first_band()
    {
        var p = TaxBracketPosition.Of(Rules, 0m, 1m);

        Assert.Equal(0m, p.Quotient);
        Assert.Equal(Rules.Brackets[1].From, p.NextFrom);
    }
}
