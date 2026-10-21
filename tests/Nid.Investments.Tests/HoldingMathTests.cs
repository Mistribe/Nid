using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

public class HoldingMathTests
{
    private static readonly Guid EuroFund = Guid.NewGuid();
    private static readonly Guid FundA = Guid.NewGuid();

    private static HoldingSnapshot Snapshot(params SnapshotLine[] lines) => new(Guid.NewGuid(), ItemId.New(), new LocalDate(2025, 12, 31), null, true, lines);

    // Issue #34 §8, the 2025-12-31 snapshot.
    private static readonly HoldingSnapshot Golden = Snapshot(
        new SnapshotLine(EuroFund, null, null, 4_806.70m, null),
        new SnapshotLine(FundA, 78.4m, 75m, 5_880m, null));

    [Theory]
    [InlineData("US0378331005")]
    [InlineData("FR0000120271")]
    [InlineData("IE00B4L5Y983")]
    public void Real_isins_are_valid(string isin) => Assert.True(HoldingMath.IsIsin(isin));

    [Theory]
    [InlineData("US0378331006")] // wrong check digit
    [InlineData("us0378331005")] // lower case
    [InlineData("US037833100")] // too short
    [InlineData("1S0378331005")] // country is not two letters
    [InlineData("US037833100A")] // check is not a digit
    [InlineData(null)]
    public void Malformed_isins_are_not(string? isin) => Assert.False(HoldingMath.IsIsin(isin));

    [Fact]
    public void The_golden_snapshot_weighs_55_and_45_percent()
    {
        Assert.Equal(10_686.70m, HoldingMath.Total(Golden));
        Assert.Equal(55.0m, HoldingMath.Weight(Golden, Golden.Lines[1]));
        Assert.Equal(45.0m, HoldingMath.Weight(Golden, Golden.Lines[0]));
    }

    [Fact]
    public void A_zero_total_has_no_weight()
    {
        var empty = Snapshot(new SnapshotLine(FundA, 0m, 10m, 0m, null));
        Assert.Null(HoldingMath.Weight(empty, empty.Lines[0]));
    }

    [Fact]
    public void A_snapshot_matching_its_valuation_has_no_warning() => Assert.Empty(HoldingMath.Warnings(Golden, 10_686.70m));

    [Fact]
    public void Units_times_price_off_by_more_than_a_cent_warns_on_that_line()
    {
        // Units printed to 6 decimals: only the value's cent is left to rounding.
        var off = Snapshot(new SnapshotLine(FundA, 78.400000m, 75m, 5_880.02m, null), new SnapshotLine(EuroFund, null, null, 100m, null));
        Assert.Equal([new HoldingWarning(HoldingWarnings.ValueMismatch, FundA)], HoldingMath.Warnings(off, null));
        Assert.Empty(HoldingMath.Warnings(Snapshot(new SnapshotLine(FundA, 78.400000m, 75m, 5_880.01m, null)), null));
    }

    [Theory]
    [InlineData("1.185", "4653.27", false)] // issue #46, R-Co Valor (C): « tronqué à 3 décimales », 0.001 × 3 924.47 ≈ 3.92 € of room
    [InlineData("1.185", "4654.43", false)]
    [InlineData("1.185", "4654.44", true)]
    [InlineData("1.185000", "4653.27", true)] // the same units printed to 6 decimals leave no room
    [InlineData("71", "4653.27", true)] // whole units are an exact count
    public void The_value_may_differ_from_units_times_price_by_what_the_printed_units_leave_out(string units, string value, bool warns)
    {
        var line = new SnapshotLine(FundA, decimal.Parse(units, CultureInfo.InvariantCulture), 3_924.47m, decimal.Parse(value, CultureInfo.InvariantCulture), null);
        Assert.Equal(warns, HoldingMath.Warnings(Snapshot(line), null).Count == 1);
    }

    [Theory]
    [InlineData("10681.70", true)] // issue #34 §8: 5 € off
    [InlineData("10685.69", true)]
    [InlineData("10685.70", false)]
    [InlineData("10687.70", false)]
    public void The_total_against_the_valuation_warns_beyond_one_euro(string valuation, bool warns)
    {
        var expected = warns ? new[] { new HoldingWarning(HoldingWarnings.SumMismatch) } : [];
        Assert.Equal(expected, HoldingMath.Warnings(Golden, decimal.Parse(valuation, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
