using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Property return spec §4: the cash flows of the whole operation, figures checked by hand.</summary>
public class PropertyReturnTests
{
    private static readonly ItemId Home = ItemId.New();

    private static Money Eur(decimal amount) => Loans.Ok(Money.Create(amount, Currency.Eur));

    private static Flow Expense(LocalDate on, string category, decimal amount)
        => new(Guid.NewGuid(), Home, on, FlowKind.Expense, Eur(amount), category, null, null);

    private static Flow Rent(LocalDate on, decimal amount)
        => new(Guid.NewGuid(), Home, on, FlowKind.Income, Eur(amount), PropertyCategories.Rent, null, null);

    private static Tenancy Lease(LocalDate from, decimal rent, decimal charges = 0m, LocalDate? until = null)
        => new("Bail", from, until, rent, charges, null, null);

    private static PropertyDetails Bought(LocalDate on, decimal price = 100_000m)
        => new(PropertyType.Rental, on, price, 0m, 0m, 0m, null, null, PropertyCadence.Yearly, null, on, null);

    private static EffectiveAssumptions Zero(
        int yearsAfterPinel = 0, decimal? resalePrice = null, decimal rentGrowth = 0m, decimal? rentAfterPinel = null, decimal marginal = 0m)
        => new(yearsAfterPinel, resalePrice, 0m, rentGrowth, 0m, 0, rentAfterPinel, marginal, 0m, new HashSet<string>());

    private static ReturnInputs Inputs(
        PropertyDetails property, LocalDate today, IReadOnlyList<Flow>? flows = null, IReadOnlyList<Tenancy>? tenancies = null,
        EffectiveAssumptions? assumptions = null, decimal social = 0m, PinelOutlook? pinel = null, IReadOnlyList<ReturnLoan>? loans = null,
        Valuation? latest = null)
        => new(property, flows ?? [], tenancies ?? [], latest, loans ?? [], assumptions ?? Zero(), social, pinel, (_, _) => 0m, today);

    private static decimal On(IReadOnlyList<CashFlow> flows, string line, LocalDate on) => flows.Single(f => f.Line == line && f.On == on).Amount;

    [Fact]
    public void Cash_purchase_has_no_loan_lines()
    {
        var bought = new LocalDate(2019, 12, 15);
        var flows = PropertyReturn.Flows(Inputs(Bought(bought), bought, tenancies: [Lease(bought, 500m)]), new LocalDate(2020, 12, 15), 100_000m);

        Assert.Equal(-100_000m, On(flows, "purchase", bought));
        Assert.Equal(Enumerable.Range(1, 12).Select(m => (new LocalDate(2020, m, 1), 500m)),
            flows.Where(f => f.Line == "rent").Select(f => (f.On, f.Amount)));
        Assert.DoesNotContain(flows, f => f.Line.StartsWith("loan", StringComparison.Ordinal));
    }

    [Fact]
    public void Deficit_is_carried_forward()
    {
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2024, 1, 1)), new LocalDate(2026, 1, 10),
            flows: [Expense(new LocalDate(2024, 3, 1), "maintenance", 1_000m), Rent(new LocalDate(2025, 3, 1), 3_000m)],
            assumptions: Zero(marginal: 0.30m), social: 0.172m), new LocalDate(2026, 6, 1), 100_000m);

        Assert.Equal(0m, On(flows, "rental_tax", new LocalDate(2025, 7, 31)));
        Assert.Equal(-944.00m, On(flows, "rental_tax", new LocalDate(2026, 7, 31)));
    }

    [Fact]
    public void Ended_tenancy_is_extended()
    {
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2023, 7, 1)), new LocalDate(2026, 1, 10),
            tenancies: [Lease(new LocalDate(2023, 7, 1), 800m, until: new LocalDate(2025, 7, 1))]), new LocalDate(2026, 6, 1), 100_000m);

        Assert.Equal([800m, 800m, 800m, 800m], flows.Where(f => f.Line == "rent").Select(f => f.Amount));
    }

    [Fact]
    public void Rent_after_pinel_replaces_rent()
    {
        var pinel = new PinelOutlook(2020, 6, 6, new Dictionary<int, decimal>());
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2020, 1, 1)), new LocalDate(2025, 11, 10),
            tenancies: [Lease(new LocalDate(2020, 1, 1), 700m, 50m)], assumptions: Zero(rentAfterPinel: 900m), pinel: pinel),
            new LocalDate(2026, 3, 1), 100_000m);

        Assert.Equal(750m, On(flows, "rent", new LocalDate(2025, 12, 1)));
        Assert.Equal(950m, On(flows, "rent", new LocalDate(2026, 1, 1)));
    }

    [Fact]
    public void Growth_applies_by_full_year()
    {
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2025, 1, 1)), new LocalDate(2026, 3, 15),
            tenancies: [Lease(new LocalDate(2025, 1, 1), 1_000m)], assumptions: Zero(rentGrowth: 0.02m)), new LocalDate(2027, 6, 1), 100_000m);

        Assert.Equal(1_000.00m, On(flows, "rent", new LocalDate(2027, 3, 1)));
        Assert.Equal(1_020.00m, On(flows, "rent", new LocalDate(2027, 4, 1)));
    }

    [Fact]
    public void Pinel_credit_on_31_july()
    {
        var pinel = new PinelOutlook(2024, 6, 6, new Dictionary<int, decimal> { [2024] = 4_400m });
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2024, 1, 1)), new LocalDate(2026, 1, 10), pinel: pinel),
            new LocalDate(2026, 6, 1), 100_000m);

        Assert.Equal(4_400m, On(flows, "pinel", new LocalDate(2025, 7, 31)));
    }

    private static ReturnLoan OneRow(LocalDate due, decimal balance, decimal rate)
        => new(ItemId.New(), Loans.Annuity(200_000m, rate, 240),
            new LoanSchedule([new ScheduleRow(1, due, 1, rate, 1_000m, 100m, 900m, 0m, balance, null)], []), 1m);

    private static PinelOutlook Pinel2020(int initial, int total)
        => new(2020, initial, total, Enumerable.Range(2020, total).ToDictionary(y => y, _ => 4_400m));

    [Fact]
    public void Cash_case_irr_by_hand()
    {
        var bought = new LocalDate(2019, 12, 15);
        var r = PropertyReturn.Compute(Inputs(Bought(bought), bought, tenancies: [Lease(bought, 500m)],
            assumptions: Zero(yearsAfterPinel: 1, resalePrice: 100_000m)));

        Assert.Equal(new LocalDate(2020, 12, 15), r.Scenario!.Outcome.ResaleOn);
        Assert.Equal(6.2m, r.Scenario.Outcome.IrrPercent);
        Assert.Equal(6_000.00m, r.Scenario.Outcome.NetGain);
    }

    [Theory]
    [InlineData(0.015, -750.00)]
    [InlineData(0.08, -3_000.00)]
    public void Penalty_at_legal_cap(double rate, double penalty)
    {
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2020, 1, 1)), new LocalDate(2026, 1, 1),
            loans: [OneRow(new LocalDate(2026, 12, 5), 100_000m, (decimal)rate)]), new LocalDate(2027, 1, 1), 100_000m);

        Assert.Equal((decimal)penalty, On(flows, "penalty", new LocalDate(2027, 1, 1)));
        Assert.Equal(-100_000m, On(flows, "loan_balance", new LocalDate(2027, 1, 1)));
    }

    [Fact]
    public void Loan_settled_before_resale_has_no_penalty()
    {
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2020, 1, 1)), new LocalDate(2026, 1, 1),
            loans: [OneRow(new LocalDate(2025, 12, 5), 0m, 0.015m)]), new LocalDate(2027, 1, 1), 100_000m);

        Assert.DoesNotContain(flows, f => f.Line is "penalty" or "loan_balance");
    }

    [Fact]
    public void Clawback_initial_period()
    {
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2020, 1, 1)), new LocalDate(2024, 1, 10), pinel: Pinel2020(6, 9)),
            new LocalDate(2024, 3, 1), 100_000m);

        Assert.Equal(-17_600.00m, On(flows, "pinel_clawback", new LocalDate(2024, 3, 1)));
        Assert.Equal([2021, 2022, 2023, 2024], flows.Where(f => f.Line == "pinel").Select(f => f.On.Year));
    }

    [Fact]
    public void Clawback_extension()
    {
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2020, 1, 1)), new LocalDate(2027, 1, 10), pinel: Pinel2020(6, 9)),
            new LocalDate(2027, 6, 30), 100_000m);

        Assert.Equal(-4_400.00m, On(flows, "pinel_clawback", new LocalDate(2027, 6, 30)));
        Assert.Equal(Enumerable.Range(2021, 7), flows.Where(f => f.Line == "pinel").Select(f => f.On.Year));
    }

    [Fact]
    public void Entered_price_only_for_scenario()
    {
        var today = new LocalDate(2026, 1, 1);
        var r = PropertyReturn.Compute(Inputs(Bought(new LocalDate(2020, 1, 1)), today, assumptions: Zero(resalePrice: 300_000m),
            latest: new Valuation(Guid.NewGuid(), Home, today, Eur(200_000m), null)));

        Assert.Equal((300_000m, "entered"), (r.Scenario!.Outcome.ResalePrice, r.Scenario.PriceSource));
        Assert.Equal(16, r.Sensitivity.Count);
        Assert.Equal(200_000m, r.Sensitivity[0].ResalePrice);
    }

    [Fact]
    public void No_valuation_no_price()
    {
        var r = PropertyReturn.Compute(Inputs(Bought(new LocalDate(2020, 1, 1)), new LocalDate(2026, 1, 1)));

        Assert.Null(r.Scenario);
        Assert.Empty(r.Sensitivity);
        Assert.Contains("no_valuation", r.Warnings);
    }

    [Fact]
    public void Resale_in_past()
    {
        var today = new LocalDate(2026, 2, 1);
        var r = PropertyReturn.Compute(Inputs(Bought(new LocalDate(2014, 1, 1)), today, pinel: new PinelOutlook(2014, 6, 6, new Dictionary<int, decimal>()),
            assumptions: Zero(resalePrice: 150_000m)));

        Assert.Equal(today, r.Scenario!.Outcome.ResaleOn);
        Assert.Contains("resale_in_past", r.Warnings);
        Assert.Equal(new LocalDate(2020, 1, 1), r.PinelEnd);
    }

    [Fact]
    public void Past_penalty_fee_is_paid()
    {
        var loan = OneRow(new LocalDate(2024, 6, 5), 50_000m, 0.015m);
        loan = loan with { Terms = loan.Terms with { Fees = [new LoanFee(FeeKind.EarlyRepaymentPenalty, 600m, new LocalDate(2024, 5, 2))] } };
        var flows = PropertyReturn.Flows(Inputs(Bought(new LocalDate(2020, 1, 1)), new LocalDate(2026, 1, 1), loans: [loan]), new LocalDate(2027, 1, 1), 100_000m);

        Assert.Equal(-600m, On(flows, "loan_fees", new LocalDate(2024, 5, 2)));
    }
}
