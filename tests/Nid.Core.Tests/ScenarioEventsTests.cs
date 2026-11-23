using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Projection spec §4: a scenario's repayments, sales, purchases and pensions as the engine's monthly arrays.</summary>
public class ScenarioEventsTests
{
    private static readonly LocalDate Today = new(2026, 10, 27);
    private const int Months = 12;

    private static ProjectionInputs Inputs(ResolvedRates? rates = null, IReadOnlyList<RealEstateMonth>? realEstate = null) => new(
        Today, Months, [], Enumerable.Repeat(1_000m, Months + 1).ToList(), realEstate ?? [], 0m,
        new ProjectionAssumptions(new Dictionary<string, decimal> { [AllocationClasses.Cash] = 0m }, 0m, null, AllocationClasses.Cash, true),
        [new ProjectionMilestone(Today, "loan_end", "Loan", null)],
        Pensions: rates is null ? null : Enumerable.Repeat(0m, Months + 1).ToList(),
        PassiveIncome: rates is null ? null : Enumerable.Repeat(500m, Months + 1).ToList(), Rates: rates);

    private static ResolvedRates Flat() => new(Enumerable.Repeat(1m, Months + 1).ToList(), Enumerable.Repeat(1m, Months + 1).ToList(),
        new Dictionary<string, IReadOnlyList<decimal>>());

    private static ProjectionInputs Apply(
        ProjectionInputs inputs, RealEstateProjection? realEstate = null, IReadOnlyDictionary<Guid, decimal>? penalties = null,
        IReadOnlyList<ScenarioPurchase>? purchases = null, IReadOnlyList<ScenarioPension>? pensions = null, IReadOnlyList<RealEstateMonth>? based = null)
        => ScenarioEvents.Apply(inputs, Today, Months, based ?? [], realEstate ?? new RealEstateProjection([], []), penalties ?? new Dictionary<Guid, decimal>(),
            purchases ?? [], 0m, pensions ?? [], [new ScheduledChange(1, 3, 100m, true)], [new ProjectionMilestone(Today.PlusMonths(1), "event", "Event", null)]);

    [Fact]
    public void Without_events_the_arrays_are_zero_and_the_milestones_add_up()
    {
        var applied = Apply(Inputs());

        Assert.All(applied.MonthlyAdjustments!, a => Assert.Equal(0m, a));
        Assert.All(applied.CashFlows!, c => Assert.Equal(0m, c));
        Assert.Empty(applied.Withdrawals!);
        Assert.Equal(Months + 1, applied.RealEstate.Count);
        Assert.Equal(2, applied.Milestones.Count);
        Assert.Single(applied.Changes!);
    }

    [Fact]
    public void A_repayment_and_a_sale_move_the_savings_and_the_monthly_costs()
    {
        var repaid = Guid.NewGuid();
        var realEstate = new RealEstateProjection([], [])
        {
            Repayments = [new RepaymentOutcome(repaid, 2, 10_000m), new RepaymentOutcome(Guid.NewGuid(), 3, 0m)],
            Sales = [new SaleOutcome(Guid.NewGuid(), 5, 80_000m, 300m) { MonthlyRent = 200m, MonthlyCosts = 100m }],
        };

        var applied = Apply(Inputs(Flat()), realEstate, new Dictionary<Guid, decimal> { [repaid] = 150m });

        Assert.Equal(-10_150m, applied.CashFlows![2]);
        Assert.Equal(0m, applied.CashFlows[3]);
        Assert.Equal(80_000m, applied.CashFlows[5]);
        Assert.Equal((0m, 300m), (applied.MonthlyAdjustments![5], applied.MonthlyAdjustments[6]));
        Assert.Equal((500m, 300m), (applied.PassiveIncome![5], applied.PassiveIncome[6]));
        Assert.Equal(-100m, applied.ExpenseAdjustments![6]);
    }

    [Fact]
    public void A_purchase_withdraws_its_funding_and_costs_its_loan_and_charges_from_its_month()
    {
        var envelope = ItemId.New();
        var document = new PropertyPurchase(1, PurchaseUse.Primary, Today.PlusMonths(2), 200_000m, 0m, 0m, 0m, 100m, 600m, 0m, 0, null,
            [new FundingLine(envelope, 20_000m)],
            [new ScenarioLoan("Loan", 180_000m, 0.03m, 240, null, new ScenarioInsurance(ScenarioInsuranceKind.FixedMonthly, 0m), 0m, 0m)],
            new ScenarioAssumptions(10, new Dictionary<string, decimal>(), null, 0m, AllocationClasses.Cash));
        var rows = Enumerable.Range(1, Months).Select(i => new SimulatedPayment(i, Today.PlusMonths(2 + i), 900m, 450m, 450m, 10m, 180_000m - 450m * i)).ToList();
        var based = Enumerable.Range(0, Months + 1).Select(k => new RealEstateMonth(Today.ToYearMonth().PlusMonths(k), 0m, 0m, 1_234m)).ToList();

        var applied = Apply(Inputs(realEstate: based), purchases: [new ScenarioPurchase(2, document, [new SimulatedLoan("Loan", rows, 0m)], 50m)], based: based);

        Assert.Equal(new SliceWithdrawal(2, envelope, 20_000m), Assert.Single(applied.Withdrawals!));
        // Charges 100 + tax 50 − the rent it saves 600, plus the payment and insurance due that month (none in month 2).
        Assert.Equal((0m, 450m, -460m), (applied.MonthlyAdjustments![1], applied.MonthlyAdjustments[2], applied.MonthlyAdjustments[3]));
        Assert.Equal(200_000m, applied.RealEstate[2].PropertyValue);
        Assert.Equal(180_000m, applied.RealEstate[2].Debt);
        Assert.Equal(180_000m - 450m, applied.RealEstate[3].Debt);
        Assert.Equal(1_234m, applied.RealEstate[0].LoanPayments);
    }

    [Fact]
    public void A_pension_replaces_the_own_one_from_the_switch_month()
    {
        var own = Enumerable.Repeat(300m, Months + 1).ToList();

        var applied = Apply(Inputs(Flat()), pensions: [new ScenarioPension(4, 1_000m, own), new ScenarioPension(0, 100m, null)]);

        Assert.Equal(1_100m, applied.Income[1]);
        Assert.Equal(1_800m, applied.Income[4]);
        Assert.Equal(800m, applied.Pensions![4]);
        Assert.Equal(1_000m, applied.Income[0]);
    }
}
