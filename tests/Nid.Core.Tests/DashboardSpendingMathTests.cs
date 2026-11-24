using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using Nid.Core.Domain.Dashboard;
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
        => DashboardComposition.Compose([], [new CommitmentsPart(CommitmentSources.Subscriptions, 1, Eur(1000m), Eur(1000m))],
            [new IncomePart(income ? 1 : 0, income ? Eur(2000m) : [])], spending, [], [], Currency.Eur, rates ?? RateTable.Empty(Today, 7), Today);

    /// <summary>One category per slice; « Non classées » is the slice without one.</summary>
    private static SpendingPart Part(params (string Name, decimal Amount)[] slices)
        => new(Eur(slices.Sum(s => s.Amount)), [.. slices.Select(s => Slice(s.Name, Eur(s.Amount)))]);

    private static SpendingSlice Slice(string name, Dictionary<Currency, decimal> amount)
        => new(name == "Non classées" ? null : Guid.NewGuid(), name, amount);

    private static (string, string)[] Slices(DashboardView view)
        => [.. view.SpendingSlices.Select(s => (s.Others ? "*" + s.Name : s.Uncategorised ? "?" + s.Name : s.Name, s.Amount.Amount))];

    [Fact]
    public void What_is_left_is_the_margin_less_everyday_spending_and_goes_below_zero()
    {
        var view = Compose([Part(("Alimentation", 700m), ("Non classées", 500m))]);

        Assert.Equal("1000.00", view.Margin!.Amount);
        Assert.Equal("1200.00", view.EverydaySpending!.Amount);
        Assert.Equal("-200.00", view.Left!.Amount);
        Assert.Equal([("Alimentation", "700.00"), ("?Non classées", "500.00")], Slices(view));
    }

    [Fact]
    public void The_month_budget_the_spending_page_reads_is_accueil_s()
    {
        IReadOnlyList<CommitmentsPart> commitments = [new CommitmentsPart(CommitmentSources.Subscriptions, 1, Eur(1000m), Eur(1000m))];
        IReadOnlyList<SpendingPart> spending = [Part(("Alimentation", 700m), ("Non classées", 500m))];
        foreach (var income in new[] { 1, 0 })
        {
            IReadOnlyList<IncomePart> earned = [new IncomePart(income, income > 0 ? Eur(2000m) : [])];
            var view = DashboardComposition.Compose([], commitments, earned, spending, [], [], Currency.Eur, RateTable.Empty(Today, 7), Today);
            var month = DashboardMath.Budget(commitments, earned, spending, Currency.Eur, RateTable.Empty(Today, 7), Today);
            Assert.Equal((view.Income.Total?.Amount, view.Commitments.Total?.Amount, view.Margin?.Amount, view.EverydaySpending?.Amount, view.Left?.Amount),
                (M(month.Income), M(month.Committed), M(month.Margin), M(month.Everyday), M(month.Left)));
        }
        // No income declared: unknown, never 0, and so is what is left.
        var none = DashboardMath.Budget(commitments, [new IncomePart(0, new Dictionary<Currency, decimal>())], spending, Currency.Eur, RateTable.Empty(Today, 7), Today);
        Assert.Equal((null, 1000m, null, 1200m, null), (none.Income, none.Committed, none.Margin, none.Everyday, none.Left));

        static string? M(decimal? amount) => amount?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void A_period_adds_its_months_up_and_its_savings_rate_is_what_is_left_of_the_income()
    {
        // #428: a raise in the second month; what is left of 4 500 is 1 000, 22.2 %.
        var period = MonthBudget.Sum([new MonthBudget(2000m, 1000m, 700m), new MonthBudget(2500m, 1000m, 800m)]);
        Assert.Equal((4500m, 2000m, 1500m, 1000m, 22.2m), (period.Income, period.Committed, period.Everyday, period.Left, period.SavingsRate));
        // Overspent: a negative rate.
        Assert.Equal(-10m, new MonthBudget(1000m, 600m, 500m).SavingsRate);

        // One month unknown makes the period's part unknown; no income, or none above 0, has no rate.
        var unknown = MonthBudget.Sum([new MonthBudget(null, 1000m, 700m), new MonthBudget(2500m, 1000m, null)]);
        Assert.Equal((null, 2000m, null, null), (unknown.Income, unknown.Committed, unknown.Everyday, unknown.SavingsRate));
        Assert.Null(new MonthBudget(0m, 100m, 50m).SavingsRate);
    }

    [Fact]
    public void Nothing_to_read_a_missing_rate_or_no_margin_leave_the_figures_unknown()
    {
        foreach (var none in new[] { Compose([]), Compose([SpendingPart.None]) })
            Assert.Equal((null, null, 0), (none.EverydaySpending, none.Left, none.SpendingSlices.Count));

        var unrated = Compose([new SpendingPart(new Dictionary<Currency, decimal> { [Currency.Usd] = 50m }, [Slice("Transport", new Dictionary<Currency, decimal> { [Currency.Usd] = 50m })])]);
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
                Slice("Transport", usd), // 90 + 10
                Slice("Alimentation", Eur(150m)), Slice("Logement", Eur(60m)),
                Slice("Santé", Eur(40m)), Slice("Loisirs", Eur(40m)),
                Slice("Shopping", Eur(30m)), Slice("Dons", Eur(15m)),
                // A net refund is no band of its own: it lowers « Autres ».
                Slice("Assurances", Eur(-10m)),
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
    public void Slices_group_by_category_never_by_name()
    {
        var food = Guid.NewGuid();
        var view = Compose([
            new SpendingPart(Eur(160m), [new SpendingSlice(food, "Alimentation", Eur(100m)), new SpendingSlice(Guid.NewGuid(), "Non classées", Eur(60m))]),
            new SpendingPart(Eur(80m), [new SpendingSlice(food, "Alimentation", Eur(30m)), new SpendingSlice(Guid.NewGuid(), "Alimentation", Eur(20m)),
                new SpendingSlice(null, "Non classées", Eur(30m))]),
        ]);

        // A user's « Non classées » is a category of its own; two « Alimentation » stay apart.
        Assert.Equal([("Alimentation", "130.00"), ("Non classées", "60.00"), ("?Non classées", "30.00"), ("Alimentation", "20.00")], Slices(view));
    }

    [Fact]
    public void Refunds_larger_than_the_spending_leave_it_below_zero_and_the_slices_above_it()
    {
        var view = Compose([Part(("Alimentation", 100m), ("Assurances", -150m))]);

        Assert.Equal(("-50.00", "1050.00"), (view.EverydaySpending!.Amount, view.Left!.Amount));
        Assert.Equal([("Alimentation", "100.00")], Slices(view)); // no band for the refund, no « Autres »
    }
}
