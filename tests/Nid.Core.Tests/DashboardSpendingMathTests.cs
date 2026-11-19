using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>#85 decision 10: the month's everyday spending, what is left after it, and its split by category.</summary>
public class DashboardSpendingMathTests
{
    private static readonly LocalDate Today = new(2025, 10, 15);

    private static Dictionary<Currency, decimal> Eur(decimal amount) => new() { [Currency.Eur] = amount };

    private static readonly RateTable UsdAtNinety = new([new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, maxGapDays: 7);

    /// <summary>Income 2 000, commitments 1 000: a margin of 1 000.</summary>
    private static DashboardView Compose(IReadOnlyList<SpendingPart> spending, RateTable? rates = null, bool income = true)
        => DashboardMath.Compose([], [new CommitmentsPart(CommitmentSources.Subscriptions, 1, Eur(1000m), Eur(1000m))],
            [new IncomePart(income ? 1 : 0, income ? Eur(2000m) : [])], spending, [], [], Currency.Eur, rates ?? RateTable.Empty(Today, 7), Today);

    private static SpendingPart Part(params (string Name, decimal Amount)[] slices)
        => new(Eur(slices.Sum(s => s.Amount)), [.. slices.Select(s => new SpendingSlice(s.Name, Eur(s.Amount)))]);

    private static (string, string)[] Slices(DashboardView view) => [.. view.SpendingSlices.Select(s => (s.Others ? "*" + s.Name : s.Name, s.Amount.Amount))];

    [Fact]
    public void What_is_left_is_the_margin_less_everyday_spending_and_goes_below_zero()
    {
        var view = Compose([Part(("Alimentation", 700m), ("Non classées", 500m))]);

        Assert.Equal("1000.00", view.Margin!.Amount);
        Assert.Equal("1200.00", view.EverydaySpending!.Amount);
        Assert.Equal("-200.00", view.Left!.Amount);
        Assert.Equal([("Alimentation", "700.00"), ("Non classées", "500.00")], Slices(view));
    }

    [Fact]
    public void Nothing_to_read_a_missing_rate_or_no_margin_leave_the_figures_unknown()
    {
        foreach (var none in new[] { Compose([]), Compose([SpendingPart.None]) })
            Assert.Equal((null, null, 0), (none.EverydaySpending, none.Left, none.SpendingSlices.Count));

        var unrated = Compose([new SpendingPart(new Dictionary<Currency, decimal> { [Currency.Usd] = 50m }, [new SpendingSlice("Transport", new Dictionary<Currency, decimal> { [Currency.Usd] = 50m })])]);
        Assert.Equal((null, null, 0), (unrated.EverydaySpending, unrated.Left, unrated.SpendingSlices.Count));
        Assert.Equal("1000.00", unrated.Margin!.Amount); // the margin stays as it was

        var noIncome = Compose([Part(("Alimentation", 80m))], income: false);
        Assert.Equal(("80.00", null), (noIncome.EverydaySpending!.Amount, noIncome.Left));

        var zero = Compose([new SpendingPart(Eur(0m), [])]);
        Assert.Equal(("0.00", "1000.00", 0), (zero.EverydaySpending!.Amount, zero.Left!.Amount, zero.SpendingSlices.Count));
    }

    [Fact]
    public void The_five_largest_categories_then_others_with_the_rest_each_converted()
    {
        var usd = new Dictionary<Currency, decimal> { [Currency.Usd] = 100m, [Currency.Eur] = 10m };
        var view = Compose([new SpendingPart(
            new Dictionary<Currency, decimal> { [Currency.Usd] = 100m, [Currency.Eur] = 335m },
            [
                new SpendingSlice("Transport", usd), // 90 + 10
                new SpendingSlice("Alimentation", Eur(150m)), new SpendingSlice("Logement", Eur(60m)),
                new SpendingSlice("Santé", Eur(40m)), new SpendingSlice("Loisirs", Eur(40m)),
                new SpendingSlice("Shopping", Eur(30m)), new SpendingSlice("Dons", Eur(15m)),
                // A net refund is no band of its own: it lowers « Autres ».
                new SpendingSlice("Assurances", Eur(-10m)),
            ])], UsdAtNinety);

        Assert.Equal("425.00", view.EverydaySpending!.Amount);
        // Ties in French order; « Autres » = 425 − 390.
        Assert.Equal([("Alimentation", "150.00"), ("Transport", "100.00"), ("Logement", "60.00"), ("Loisirs", "40.00"), ("Santé", "40.00"), ("*Autres", "35.00")],
            Slices(view));
    }

    [Fact]
    public void A_category_named_like_the_rest_is_never_the_rest()
    {
        var view = Compose([Part(("Autres", 90m), ("A", 80m), ("B", 70m), ("C", 60m), ("D", 50m), ("E", 40m))]);

        Assert.Equal([("Autres", "90.00"), ("A", "80.00"), ("B", "70.00"), ("C", "60.00"), ("D", "50.00"), ("*Autres", "40.00")], Slices(view));
    }

    [Fact]
    public void Refunds_larger_than_the_spending_leave_it_below_zero_and_the_slices_above_it()
    {
        var view = Compose([Part(("Alimentation", 100m), ("Assurances", -150m))]);

        Assert.Equal(("-50.00", "1050.00"), (view.EverydaySpending!.Amount, view.Left!.Amount));
        Assert.Equal([("Alimentation", "100.00")], Slices(view)); // no band for the refund, no « Autres »
    }
}
