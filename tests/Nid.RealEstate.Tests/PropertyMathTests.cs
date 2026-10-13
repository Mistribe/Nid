using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Spec 3C1 §5–§7: a property at d, README §12's golden case first.</summary>
public class PropertyMathTests
{
    private static readonly LocalDate D = new(2025, 10, 15);
    private static readonly ItemId Home = ItemId.New();

    /// <summary>README §12: bought 250 000 € with 19 000 € of notary fees.</summary>
    private static PropertyDetails Bought(PropertyCadence cadence = PropertyCadence.Yearly, int? staleAfterDays = null)
        => new(PropertyType.PrimaryResidence, new LocalDate(2020, 6, 15), 250_000m, 19_000m, 0m, 0m, null, null, cadence, staleAfterDays, null, null);

    private static Money Eur(decimal amount) => Loans.Ok(Money.Create(amount, Currency.Eur));

    private static Valuation Estimate(LocalDate on, decimal value) => new(Guid.NewGuid(), Home, on, Eur(value), null);

    private static Flow Expense(LocalDate on, string category, decimal amount)
        => new(Guid.NewGuid(), Home, on, FlowKind.Expense, Eur(amount), category, null, null);

    private static Flow Income(LocalDate on, string category, decimal amount)
        => new(Guid.NewGuid(), Home, on, FlowKind.Income, Eur(amount), category, null, null);

    /// <summary>A loan's figures at d: only the fields a property reads matter.</summary>
    private static LoanFigures Owing(decimal balance, decimal interest = 0m, decimal insurance = 0m, decimal fees = 0m, decimal principalRepaid = 0m)
        => new(balance, principalRepaid, interest, insurance, fees, null, null, 0, null, null, null, 0m, 0m, 0m);

    private static LinkedLoan Linked(LoanFigures figures, decimal? rate = 1m, bool otherCurrency = false, bool isPrivate = false)
        => new(ItemId.New(), otherCurrency, isPrivate, figures, rate);

    /// <summary>README §12 unless told otherwise: estimated 290 000, 12 000 of works, 150 000 still owed on one loan.</summary>
    private static PropertyFigures At(
        PropertyDetails? property = null, IReadOnlyList<Valuation>? values = null, IReadOnlyList<Flow>? flows = null,
        IReadOnlyList<LinkedLoan>? loans = null, bool shared = true)
        => PropertyMath.At(property ?? Bought(), shared,
            values ?? new[] { Estimate(new LocalDate(2025, 6, 1), 290_000m) },
            flows ?? new[] { Expense(new LocalDate(2021, 3, 1), PropertyCategories.Works, 12_000m) },
            loans ?? new[] { Linked(Owing(150_000m)) }, [], D);

    [Fact]
    public void Readme_section_12_golden_case()
    {
        var f = At();
        Assert.Equal(290_000m, f.Value);
        Assert.Equal(new LocalDate(2025, 6, 1), f.ValueOn);
        Assert.False(f.Stale);
        Assert.Equal(281_000m, f.CostPrice);
        Assert.Equal(9_000m, f.LatentGain);
        Assert.Equal(150_000m, f.LinkedBalance);
        Assert.Equal(140_000m, f.NetValue);
        Assert.Empty(f.Warnings);
    }

    [Fact]
    public void Two_linked_loans_are_both_deducted_and_principal_repaid_is_no_cost()
    {
        var main = Linked(Owing(130_000m, interest: 21_000m, insurance: 3_000m, fees: 1_000m, principalRepaid: 70_000m));
        var zeroRate = Linked(Owing(20_000m, principalRepaid: 10_000m));
        var f = At(flows: new[]
        {
            Expense(new LocalDate(2021, 3, 1), PropertyCategories.Works, 12_000m),
            Expense(new LocalDate(2024, 10, 15), "property_tax", 1_500m),
            Expense(new LocalDate(2025, 1, 10), "insurance", 400m),
        }, loans: new[] { main, zeroRate });

        Assert.Equal(150_000m, f.LinkedBalance);
        Assert.Equal(140_000m, f.NetValue);
        Assert.Equal(
            [("acquisition", 269_000m), ("loan_interest", 21_000m), ("loan_insurance", 3_000m), ("loan_fees", 1_000m),
             ("works", 12_000m), ("property_tax", 1_500m), ("insurance", 400m)],
            f.RealCostLines.Select(l => (l.Code, l.Amount)));
        // 269 000 + 25 000 of the loans + 13 900 of expenses; the 80 000 of principal repaid is not a cost.
        Assert.Equal(307_900m, f.RealCost);
        Assert.Equal([main.Id, zeroRate.Id], f.Loans.Select(l => l.Id));
    }

    [Fact]
    public void The_net_cost_of_rents_takes_rent_only()
    {
        var f = At(flows: new[]
        {
            Expense(new LocalDate(2021, 3, 1), PropertyCategories.Works, 12_000m),
            Income(new LocalDate(2025, 9, 5), PropertyCategories.Rent, 900m),
            Income(new LocalDate(2025, 10, 5), PropertyCategories.Rent, 900m),
            Income(new LocalDate(2025, 10, 6), "other_income", 50m),
        }, loans: Array.Empty<LinkedLoan>());
        Assert.Equal(281_000m, f.RealCost);
        Assert.Equal(279_200m, f.NetCostOfRents);
    }

    [Fact]
    public void Facts_dated_after_d_count_for_nothing()
    {
        var f = At(
            values: new[] { Estimate(new LocalDate(2025, 6, 1), 290_000m), Estimate(D.PlusDays(1), 300_000m) },
            flows: new[] { Expense(new LocalDate(2021, 3, 1), PropertyCategories.Works, 12_000m), Expense(D.PlusDays(1), PropertyCategories.Works, 5_000m) });
        Assert.Equal(290_000m, f.Value);
        Assert.Equal(281_000m, f.CostPrice);
    }

    [Fact]
    public void Without_an_estimate_the_value_is_unknown_never_zero_nor_the_purchase_price()
    {
        var f = At(values: Array.Empty<Valuation>());
        Assert.Null(f.Value);
        Assert.Null(f.ValueOn);
        Assert.Null(f.LatentGain);
        Assert.Null(f.NetValue);
        Assert.False(f.Stale);
        Assert.Equal(281_000m, f.CostPrice);
        Assert.Equal(150_000m, f.LinkedBalance);
        Assert.Equal([PropertyWarnings.NoValuation], f.Warnings.Select(w => w.Code));
    }

    [Theory]
    [InlineData(PropertyCadence.Yearly, null, 400, false)]
    [InlineData(PropertyCadence.Yearly, null, 401, true)]
    [InlineData(PropertyCadence.Manual, null, 5_000, false)]
    [InlineData(PropertyCadence.Yearly, 200, 200, false)]
    [InlineData(PropertyCadence.Yearly, 200, 201, true)]
    [InlineData(PropertyCadence.Manual, 200, 201, true)]
    public void An_estimate_is_stale_past_its_cadence_or_its_own_threshold(PropertyCadence cadence, int? staleAfterDays, int age, bool stale)
    {
        var f = At(Bought(cadence, staleAfterDays), values: new[] { Estimate(D.PlusDays(-age), 290_000m) });
        Assert.Equal(stale, f.Stale);
        Assert.Equal(stale ? new[] { PropertyWarnings.ValuationStale } : Array.Empty<string>(), f.Warnings.Select(w => w.Code));
        Assert.Equal(290_000m, f.Value); // a stale value still counts
    }

    [Fact]
    public void A_loan_in_another_currency_is_converted_at_ds_rate()
    {
        var dollars = Linked(Owing(10_000m, interest: 1_000m, insurance: 100m, fees: 50m), rate: 0.9m, otherCurrency: true);
        var f = At(loans: new[] { dollars });
        Assert.Equal(9_000m, f.LinkedBalance);
        Assert.Equal(281_000m, f.NetValue);
        Assert.Equal(10_000m, f.Loans[0].Balance);
        Assert.Equal(9_000m, f.Loans[0].Converted);
        Assert.Equal([900m, 90m, 45m], f.RealCostLines.Where(l => l.Code.StartsWith("loan_", StringComparison.Ordinal)).Select(l => l.Amount));
        Assert.Equal([(PropertyWarnings.LoanOtherCurrency, (ItemId?)dollars.Id)], f.Warnings.Select(w => (w.Code, w.LoanId)));
    }

    [Fact]
    public void A_loan_without_a_rate_at_d_is_left_out_and_named()
    {
        var pounds = Linked(Owing(50_000m, interest: 5_000m), rate: null, otherCurrency: true);
        var f = At(loans: new[] { Linked(Owing(150_000m)), pounds });
        Assert.Equal(150_000m, f.LinkedBalance);
        Assert.Equal(140_000m, f.NetValue);
        Assert.Equal(0m, f.RealCostLines.Single(l => l.Code == "loan_interest").Amount);
        Assert.Equal(50_000m, f.Loans[1].Balance);
        Assert.Null(f.Loans[1].Converted);
        Assert.Equal([(PropertyWarnings.FxMissing, (ItemId?)pounds.Id)], f.Warnings.Select(w => (w.Code, w.LoanId)));
    }

    [Fact]
    public void A_private_loan_on_a_shared_property_is_flagged_and_on_a_private_one_is_not()
    {
        var mine = Linked(Owing(150_000m), isPrivate: true);
        Assert.Equal([(PropertyWarnings.VisibilityMismatch, (ItemId?)mine.Id)], At(loans: new[] { mine }).Warnings.Select(w => (w.Code, w.LoanId)));
        Assert.Empty(At(loans: new[] { mine }, shared: false).Warnings);
        Assert.Equal(140_000m, At(loans: new[] { mine }).NetValue); // deducted for its reader: leaving it out of the household is 3E's
    }
}
