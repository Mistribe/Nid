using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Issue #233: the 2042 boxes of the demo household (Alice, Bob, Léa), on invented figures.</summary>
public class Tax2042Tests
{
    private static readonly Guid Alice = Guid.NewGuid(), Bob = Guid.NewGuid();

    private static TaxInputs Demo(RentalRegime? regime = null) => new(
        2025,
        [
            new("Alice", 0m, EmploymentStatus.Executive, 0m, 2_000m, SalaryTaxable: 42_000m, ActualExpenses: 5_000m, PerCeiling: 10_000m, Person: Alice),
            new("Bob", 0m, EmploymentStatus.NonExecutive, 3_000m, 0m, SalaryTaxable: 28_000m, PerCeiling: 5_000m, Person: Bob),
        ],
        1, 1, 9_000m, 1_000m, 2_000m, regime, 1_500m, 0m, 0m, 0m, 1_200m, 300m, 100m, RentalDeficitCarried: 400m, DonationsAidLate: 50m,
        CapitalDividends: 1_000m);

    private static Dictionary<string, (Guid? Person, decimal Amount)> Boxes(TaxInputs inputs)
    {
        var result = TaxCalculator.Compute(inputs);
        var rental = Rental2044.Totals(Rental2044.Columns([new(Guid.NewGuid(), "Studio", 9_000m, 2_000m, 0m, 0m, 0m, 0m, 0m, 1_000m)]), 10_700m);
        return Tax2042.Boxes(inputs, result, rental, [("7GA", 2_500m), ("7GA", 500m)]).ToDictionary(b => b.Code, b => (b.Person, b.Amount));
    }

    [Fact]
    public void Each_declarant_fills_their_own_boxes()
    {
        var boxes = Boxes(Demo());

        Assert.Equal((Alice, 42_000m), boxes["1AJ"]);
        Assert.Equal((Alice, 5_000m), boxes["1AK"]);
        Assert.Equal((Alice, 2_000m), boxes["6NS"]);
        // Bob's unemployment goes in 1BP, apart from the salaries.
        Assert.Equal((Bob, 28_000m), boxes["1BJ"]);
        Assert.Equal((Bob, 3_000m), boxes["1BP"]);
        Assert.DoesNotContain("1BK", boxes.Keys);
        Assert.DoesNotContain("6NT", boxes.Keys);
    }

    [Fact]
    public void The_household_boxes_come_from_the_same_inputs()
    {
        var boxes = Boxes(Demo());

        Assert.Equal((null, 1_000m), boxes["2DC"]);
        Assert.Equal((null, 500m), boxes["2TR"]);
        // 12.8 % of the 1 500 of capital income, withheld whichever option.
        Assert.Equal((null, 192m), boxes["2CK"]);
        Assert.Equal((null, 192m), Boxes(Demo() with { CapitalScale = true })["2CK"]);
        Assert.Equal((null, 3_000m), boxes["7GA"]);
        Assert.Equal((null, 1_200m), boxes["7DB"]);
        Assert.Equal((null, 300m), boxes["7UF"]);
        Assert.Equal((null, 100m), boxes["7UD"]);
        Assert.Equal((null, 50m), boxes["7UQ"]);
        Assert.Equal((null, 400m), boxes["4BD"]);
    }

    [Fact]
    public void The_rental_boxes_follow_the_regime()
    {
        var micro = Boxes(Demo());
        var real = Boxes(Demo(RentalRegime.Real));

        Assert.Equal((null, 9_000m), micro["4BE"]);
        Assert.DoesNotContain("4BA", micro.Keys);
        // The 2044's result: 9 000 − 2 000 of charges − 1 000 of interest.
        Assert.Equal((null, 6_000m), real["4BA"]);
        Assert.DoesNotContain("4BE", real.Keys);
    }

    [Fact]
    public void Dividends_never_exceed_the_capital_income()
    {
        var boxes = Boxes(Demo() with { CapitalDividends = 5_000m });

        Assert.Equal((null, 1_500m), boxes["2DC"]);
        Assert.DoesNotContain("2TR", boxes.Keys);
    }
}
