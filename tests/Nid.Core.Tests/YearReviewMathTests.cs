using System.Globalization;
using Nid.Core.Application.Reports;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #12: the year's net worth change split into its parts, each date at its own rate.</summary>
public class YearReviewMathTests
{
    private static readonly LocalDate Start = new(2023, 12, 31);
    private static readonly LocalDate End = new(2024, 12, 31);

    private static Dictionary<Currency, decimal> Eur(decimal amount) => new() { [Currency.Eur] = amount };

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    [Fact]
    public void The_parts_add_up_to_the_change()
    {
        // Investments: 1 000 paid in, 300 of gain. Real estate: a 250 000 home bought with a 200 000 loan, 2 000 of capital repaid,
        // and its estimate 5 000 down.
        var split = YearReviewMath.Split(
            [
                ("investments", new NetWorthPoint(Start, Eur(10_000m), Eur(8_000m), 1), new NetWorthPoint(End, Eur(11_300m), Eur(9_000m), 1)),
                ("real_estate", new NetWorthPoint(Start, Eur(0m), Eur(0m), 0, Eur(0m)),
                    new NetWorthPoint(End, Eur(245_000m - 198_000m), Eur(2_000m), 2, Eur(50_000m))),
            ],
            Currency.Eur, RateTable.Empty(End, 7), Start, End, out _)!;

        Assert.Equal(("10000.00", "58300.00", "48300.00"), (split.Start.Amount, split.End.Amount, split.Change.Amount));
        Assert.Equal(
            [("real_estate.moved", "50000.00"), ("real_estate.value", "-5000.00"), ("real_estate.paid_in", "2000.00"),
                ("investments.paid_in", "1000.00"), ("investments.value", "300.00")],
            split.Parts.Select(p => (p.Key, p.Amount.Amount)));
        Assert.Equal(D(split.Change.Amount), split.Parts.Sum(p => D(p.Amount.Amount)));
        // The bars run from the start: 10 000 → 60 000 → 55 000 → 57 000 → 58 000 → 58 300.
        Assert.Equal(
            [("10000.00", "60000.00"), ("55000.00", "60000.00"), ("55000.00", "57000.00"), ("57000.00", "58000.00"), ("58000.00", "58300.00")],
            split.Parts.Select(p => (p.Low.Amount, p.High.Amount)));
    }

    [Fact]
    public void A_foreign_part_converts_at_each_date()
    {
        var rates = new RateTable(
            [new FxRate(Start, Currency.Eur, 0.8m, FxRateSource.Ecb), new FxRate(End, Currency.Eur, 0.9m, FxRateSource.Ecb)], End, maxGapDays: 7);
        var usd = (decimal amount) => new Dictionary<Currency, decimal> { [Currency.Usd] = amount };

        var split = YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, usd(1_000m), usd(500m), 1), new NetWorthPoint(End, usd(1_200m), usd(600m), 1))],
            Currency.Eur, rates, Start, End, out _)!;

        // 1 200 × 0.9 − 1 000 × 0.8.
        Assert.Equal(("800.00", "1080.00", "280.00"), (split.Start.Amount, split.End.Amount, split.Change.Amount));
        Assert.Equal(D(split.Change.Amount), split.Parts.Sum(p => D(p.Amount.Amount)));
    }

    [Fact]
    public void Nothing_before_and_after_is_no_section()
        => Assert.Null(YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, Eur(0m), Eur(0m), 0), new NetWorthPoint(End, Eur(0m), Eur(0m), 0))],
            Currency.Eur, RateTable.Empty(End, 7), Start, End, out _));

    [Fact]
    public void Converted_parts_add_up_to_the_change_to_the_cent()
    {
        // At 0.333 each part has fractions of a cent: rounded one by one they would sum to 411.12, the rounded ends differ by 411.13.
        var rates = new RateTable(
            [new FxRate(Start, Currency.Eur, 0.333m, FxRateSource.Ecb), new FxRate(End, Currency.Eur, 0.333m, FxRateSource.Ecb)], End, maxGapDays: 7);
        var usd = (decimal amount) => new Dictionary<Currency, decimal> { [Currency.Usd] = amount };

        var split = YearReviewMath.Split(
            [
                ("investments", new NetWorthPoint(Start, usd(1_000.01m), usd(500.02m), 1), new NetWorthPoint(End, usd(1_234.56m), usd(700.07m), 1)),
                ("real_estate", new NetWorthPoint(Start, usd(1_000m), usd(100m), 1, usd(0m)),
                    new NetWorthPoint(End, usd(2_000.05m), usd(300.03m), 2, usd(1_500.01m))),
            ],
            Currency.Eur, rates, Start, End, out _)!;

        Assert.Equal(("666.00", "1077.13", "411.13"), (split.Start.Amount, split.End.Amount, split.Change.Amount));
        Assert.Equal(5, split.Parts.Count);
        Assert.Equal(D(split.Change.Amount), split.Parts.Sum(p => D(p.Amount.Amount)));
        Assert.Equal(D(split.End.Amount) - D(split.Start.Amount), D(split.Change.Amount));
        Assert.Equal((split.Start.Amount, split.End.Amount), (split.Parts[0].Low.Amount, split.Parts[^1].High.Amount));
    }

    [Fact]
    public void A_missing_rate_is_no_section_and_names_its_currency()
    {
        var split = YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, new Dictionary<Currency, decimal> { [Currency.Usd] = 10m }, Eur(0m), 1),
                new NetWorthPoint(End, Eur(10m), Eur(0m), 1))],
            Currency.Eur, RateTable.Empty(End, 7), Start, End, out var missing);

        Assert.Equal((null, "USD"), (split, missing));
    }
}
