using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Rental 2044 spec §3: the form's lines, totals and deficit split, worked by hand.</summary>
public class Rental2044Tests
{
    private static Rental2044Input Input(decimal l211 = 0m, decimal l222 = 0m, decimal l223 = 0m, decimal l224 = 0m, decimal l227 = 0m, decimal l250 = 0m) =>
        new(Guid.NewGuid(), "A", l211, 0m, l222, l223, l224, l227, 0m, l250);

    private static Rental2044Totals Totals(Rental2044Input input) =>
        Rental2044.Totals(Rental2044.Columns([input]), 10_700m);

    [Fact]
    public void Each_line_is_rounded_to_the_euro_half_up()
    {
        var column = Rental2044.Columns([Input(l211: 1_234.50m, l224: 99.49m)])[0];

        Assert.Equal(1_235m, column.L211);
        Assert.Equal(1_235m, column.L215);
        Assert.Equal(99m, column.L224);
        Assert.Equal(99m, column.L240);
    }

    [Fact]
    public void A_positive_result_goes_to_4BA()
    {
        var totals = Totals(Input(l211: 9_600m, l222: 20m, l223: 300m, l227: 900m, l250: 2_000m));

        Assert.Equal(6_380m, totals.Result);
        Assert.Equal(6_380m, totals.Taxable);
        Assert.Equal(0m, totals.InterestDeficit + totals.ChargesDeficit + totals.GlobalDeficit + totals.CarriedDeficit);
    }

    [Fact]
    public void A_deficit_from_charges_is_capped_at_10_700()
    {
        var totals = Totals(Input(l211: 5_000m, l224: 20_000m, l250: 1_000m));

        Assert.Equal(-16_000m, totals.Result);
        Assert.Equal(0m, totals.InterestDeficit);
        Assert.Equal(10_700m, totals.GlobalDeficit);
        Assert.Equal(5_300m, totals.CarriedDeficit);
        Assert.Equal(0m, totals.Taxable);
    }

    [Fact]
    public void Interest_above_the_rents_is_carried_not_set_against_income()
    {
        var totals = Totals(Input(l211: 3_000m, l224: 2_000m, l250: 4_000m));

        Assert.Equal(-3_000m, totals.Result);
        Assert.Equal(1_000m, totals.InterestDeficit);
        Assert.Equal(2_000m, totals.ChargesDeficit);
        Assert.Equal(2_000m, totals.GlobalDeficit);
        Assert.Equal(1_000m, totals.CarriedDeficit);
    }

    [Fact]
    public void Columns_are_ordered_by_name_then_property()
    {
        var low = new Guid("00000000-0000-0000-0000-000000000001");
        var high = new Guid("00000000-0000-0000-0000-000000000002");

        var names = Rental2044.Columns([
            new Rental2044Input(high, "Éa", 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
            new Rental2044Input(high, "B", 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
            new Rental2044Input(low, "B", 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m)]);

        Assert.Equal([low, high, high], names.Take(3).Select(c => c.Property));
        Assert.Equal("Éa", names[2].Name);
    }

    [Fact]
    public void The_estimate_uses_the_same_deficit()
    {
        var declarant = new TaxDeclarant("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m);
        var inputs = new TaxInputs(2025, [declarant], 0, 0, 5_000m, 1_000m, 20_000m, RentalRegime.Real, 0m, 0m, 0m, 0m, 0m, 0m, 0m);

        Assert.Equal(10_700m, TaxCalculator.Compute(inputs).RentalDeficit);
    }
}
