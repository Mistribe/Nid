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
            Currency.Eur, RateTable.Empty(End, 7), Start, End)!;

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
            Currency.Eur, rates, Start, End)!;

        // 1 200 × 0.9 − 1 000 × 0.8.
        Assert.Equal(("800.00", "1080.00", "280.00"), (split.Start.Amount, split.End.Amount, split.Change.Amount));
        Assert.Equal(D(split.Change.Amount), split.Parts.Sum(p => D(p.Amount.Amount)));
    }

    [Fact]
    public void Nothing_before_and_after_is_no_section()
        => Assert.Null(YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, Eur(0m), Eur(0m), 0), new NetWorthPoint(End, Eur(0m), Eur(0m), 0))],
            Currency.Eur, RateTable.Empty(End, 7), Start, End));

    [Fact]
    public void A_missing_rate_is_no_section()
        => Assert.Null(YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, new Dictionary<Currency, decimal> { [Currency.Usd] = 10m }, Eur(0m), 1),
                new NetWorthPoint(End, Eur(10m), Eur(0m), 1))],
            Currency.Eur, RateTable.Empty(End, 7), Start, End));
}
