using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Projection spec §3.3: the household net worth month by month.</summary>
public class HouseholdProjectionTests
{
    private static readonly LocalDate Today = new(2026, 10, 27);
    private static readonly ItemId Envelope = new(Guid.NewGuid());

    private static ProjectionAssumptions Assumptions(decimal cashRate = 0m, decimal inflation = 0m, bool freed = true) => new(
        new Dictionary<string, decimal> { [AllocationClasses.Cash] = cashRate }, inflation, null, AllocationClasses.Cash, freed);

    private static SavingsSlice Cash(decimal amount) => new(Envelope, AllocationClasses.Cash, AllocationLiquidity.Available, amount);

    private static IReadOnlyList<RealEstateMonth> NoDebt(int months, Func<int, decimal>? payments = null) =>
        Enumerable.Range(0, months + 1)
            .Select(k => new RealEstateMonth(new YearMonth(2026, 10).PlusMonths(k), 0m, 0m, payments?.Invoke(k) ?? 0m)).ToList();

    private static HouseholdProjectionResult Run(
        decimal cash, int months = 12, decimal savings = 0m, ProjectionAssumptions? assumptions = null,
        IReadOnlyList<decimal>? income = null, IReadOnlyList<RealEstateMonth>? realEstate = null,
        IReadOnlyList<ProjectionMilestone>? milestones = null) =>
        HouseholdProjection.Project(new(
            Today, months, [Cash(cash)], income ?? Enumerable.Repeat(0m, months + 1).ToList(), realEstate ?? [],
            savings, assumptions ?? Assumptions(), milestones ?? []));

    [Fact]
    public void First_point_is_today()
    {
        var result = Run(10_000m, realEstate: [new(new YearMonth(2026, 10), 300_000m, 200_000m, 1_000m), .. NoDebt(12).Skip(1)]);
        Assert.Equal(new HouseholdPoint(Today, 110_000m, 110_000m, 10_000m, 300_000m, 200_000m, 10_000m, 300_000m, 200_000m), result.Points[0]);
    }

    [Fact]
    public void Cash_compounds_monthly_to_the_annual_rate()
    {
        var points = Run(10_000m, assumptions: Assumptions(cashRate: 0.02m)).Points;
        Assert.Equal(10_200.00m, points[12].Savings);
        Assert.Equal(new LocalDate(2027, 10, 31), points[12].On);
    }

    [Fact]
    public void Cash_grows_at_its_yearly_rate()
    {
        var assumptions = Assumptions(cashRate: 0.02m) with
        {
            ClassRatesByYear = new Dictionary<string, IReadOnlyDictionary<int, decimal>> { [AllocationClasses.Cash] = new Dictionary<int, decimal> { [2027] = 0.03m } },
        };
        var points = Run(10_000m, months: 14, assumptions: assumptions).Points;

        // November and December 2026 at 2 %, then the twelve months of 2027 at 3 %: 10,000 × 1.02^(2/12) × 1.03 = 10,334.05.
        var m2 = (decimal)Math.Pow(1.02, 1.0 / 12) - 1m;
        var m3 = (decimal)Math.Pow(1.03, 1.0 / 12) - 1m;
        var expected = 10_000m;
        for (var k = 1; k <= 14; k++) expected *= 1m + (k <= 2 ? m2 : m3);
        Assert.Equal(Math.Round(expected, 2, MidpointRounding.AwayFromZero), points[14].Savings);
        Assert.Equal(10_334.05m, points[14].Savings);
    }

    [Fact]
    public void Monthly_savings_go_to_the_savings_class() =>
        Assert.Equal(1_200m, Run(0m, savings: 100m).Points[12].Savings);

    [Fact]
    public void Real_divides_by_cumulated_inflation()
    {
        var point = Run(10_000m, assumptions: Assumptions(inflation: 0.02m)).Points[12];
        Assert.Equal(9_803.92m, point.Real);
        Assert.Equal(10_000m, point.Nominal);
    }

    [Fact]
    public void Real_parts_divide_by_the_same_inflation()
    {
        var realEstate = Enumerable.Range(0, 13)
            .Select(k => new RealEstateMonth(new YearMonth(2026, 10).PlusMonths(k), 300_000m, 200_000m, 0m)).ToList();
        var point = Run(10_000m, assumptions: Assumptions(inflation: 0.02m), realEstate: realEstate).Points[12];
        Assert.Equal((10_000m, 300_000m, 200_000m), (point.Savings, point.Property, point.Debt));
        Assert.Equal((9_803.92m, 294_117.65m, 196_078.43m), (point.SavingsReal, point.PropertyReal, point.DebtReal));
        Assert.Equal(107_843.14m, point.Real);
    }

    [Fact]
    public void Income_rise_adds_to_savings()
    {
        var income = new List<decimal> { 2_000m, 2_000m }.Concat(Enumerable.Repeat(2_500m, 11)).ToList();
        Assert.Equal(1_000m, Run(0m, income: income).Points[3].Savings);
    }

    [Fact]
    public void Freed_loan_payment_goes_to_savings()
    {
        var realEstate = NoDebt(12, k => k < 2 ? 1_000m : 0m);
        Assert.Equal(2_000m, Run(0m, realEstate: realEstate).Points[3].Savings);
        Assert.Equal(0m, Run(0m, realEstate: realEstate, assumptions: Assumptions(freed: false)).Points[3].Savings);
    }

    [Fact]
    public void Debt_and_property_follow_the_real_estate_months()
    {
        var realEstate = Enumerable.Range(0, 13)
            .Select(k => new RealEstateMonth(new YearMonth(2026, 10).PlusMonths(k), 300_000m + k * 1_000m, 200_000m - k * 500m, 1_000m)).ToList();
        var points = Run(0m, realEstate: realEstate).Points;
        for (var k = 0; k <= 12; k++)
        {
            Assert.Equal(realEstate[k].Debt, points[k].Debt);
            Assert.Equal(realEstate[k].PropertyValue, points[k].Property);
        }
    }

    [Fact]
    public void Negative_savings_drain_cash_and_warn()
    {
        var result = Run(500m, savings: -200m);
        Assert.Equal(new YearMonth(2027, 1), result.SavingsExhaustedIn);
        Assert.Contains(result.Milestones, m => m.Kind == "savings_exhausted" && m.On == new LocalDate(2027, 1, 31));
    }

    /// <summary>1 000 in cash and 50 000 locked, saving −500 a month: cash plus pot reads 500, 0, −500 at months 1 to 3.</summary>
    private static YearMonth? ExhaustedWith(int? unlocksAt) => HouseholdProjection.Project(new(
        Today, 12, [Cash(1_000m), new(Envelope, AllocationClasses.Cash, AllocationLiquidity.Locked, 50_000m, unlocksAt)],
        Enumerable.Repeat(0m, 13).ToList(), [], -500m, Assumptions(), [])).SavingsExhaustedIn;

    [Fact]
    public void A_locked_slice_without_unlock_stays_locked() => Assert.Equal(new YearMonth(2027, 1), ExhaustedWith(null)); // retirement spec §2

    [Fact]
    public void A_per_unlocking_after_the_shortfall_does_not_save_it() => Assert.Equal(new YearMonth(2027, 1), ExhaustedWith(6));

    [Fact]
    public void An_unlocked_per_counts_as_available() => Assert.Null(ExhaustedWith(2));

    [Fact]
    public void Milestones_outside_the_grid_are_dropped()
    {
        var result = Run(0m, milestones:
        [
            new(new LocalDate(2060, 1, 1), "loan_end", "far", null),
            new(new LocalDate(2027, 3, 15), "loan_end", "near", null),
        ]);
        var kept = Assert.Single(result.Milestones);
        Assert.Equal(new LocalDate(2027, 3, 15), kept.On);
    }

    private static HouseholdProjectionResult RunEvents(
        IReadOnlyList<SavingsSlice> slices, int months = 12, ProjectionAssumptions? assumptions = null,
        IReadOnlyList<decimal>? adjustments = null, IReadOnlyList<decimal>? cashFlows = null,
        IReadOnlyList<SliceWithdrawal>? withdrawals = null) =>
        HouseholdProjection.Project(new(
            Today, months, slices, Enumerable.Repeat(0m, months + 1).ToList(), [], 0m, assumptions ?? Assumptions(), [],
            adjustments, cashFlows, withdrawals));

    [Fact]
    public void Monthly_adjustment_adds_to_monthly_savings()
    {
        var adjustments = new List<decimal> { 0m, 0m }.Concat(Enumerable.Repeat(300m, 11)).ToList();
        Assert.Equal(600m, RunEvents([], adjustments: adjustments).Points[3].Savings);
    }

    [Fact]
    public void Changes_are_indexed_from_their_start()
    {
        // Inflation spec C17 (events): an income change follows g(k) ÷ g(s), a savings change i(k) ÷ i(s), from its start month s.
        var assumptions = Assumptions(inflation: 0.02m) with { IncomeGrowth = 0.01m };
        var rates = ProjectionRates.Resolve(assumptions, null, Today, 12, [AllocationClasses.Cash]);
        var (g, i) = (rates.Income, rates.Inflation);
        var points = HouseholdProjection.Project(new(
            Today, 12, [], Enumerable.Repeat(0m, 13).ToList(), [], 0m, assumptions, [],
            Changes: [new ScheduledChange(3, 6, 100m, Income: true), new ScheduledChange(6, 13, -50m, Income: false)])).Points;

        Assert.Equal(0m, points[2].Savings);
        Assert.Equal(100m, points[3].Savings); // in euros of its start month
        var expected = Enumerable.Range(3, 3).Sum(k => 100m * g[k] / g[3]) + Enumerable.Range(6, 7).Sum(k => -50m * i[k] / i[6]);
        Assert.Equal(Math.Round(expected, 2, MidpointRounding.AwayFromZero), points[12].Savings);
        Assert.NotEqual(Math.Round(300m - 350m, 2), points[12].Savings);
    }

    [Fact]
    public void Cash_flow_enters_the_pot_once()
    {
        var cashFlows = Enumerable.Range(0, 13).Select(k => k == 2 ? -5_000m : 0m).ToList();
        var points = RunEvents([Cash(10_000m)], cashFlows: cashFlows).Points;
        Assert.Equal(10_000m, points[1].Savings);
        Assert.Equal(5_000m, points[2].Savings);
        Assert.Equal(5_000m, points[12].Savings);
    }

    [Fact]
    public void Withdrawal_leaves_its_item_pro_rata()
    {
        SavingsSlice[] slices =
        [
            new(Envelope, AllocationClasses.Equity, AllocationLiquidity.Available, 6_000m),
            new(Envelope, AllocationClasses.Cash, AllocationLiquidity.Available, 4_000m),
        ];
        SliceWithdrawal[] withdrawals = [new(1, Envelope, 5_000m)];
        Assert.Equal(5_000m, RunEvents(slices, withdrawals: withdrawals).Points[1].Savings);

        var assumptions = new ProjectionAssumptions(
            new Dictionary<string, decimal> { [AllocationClasses.Equity] = 0.2m, [AllocationClasses.Cash] = 0m },
            0m, null, AllocationClasses.Cash, true);
        Assert.Equal(5_600m, RunEvents(slices, assumptions: assumptions, withdrawals: withdrawals).Points[12].Savings);
    }

    [Fact]
    public void Withdrawal_above_the_item_takes_the_rest_from_the_pot()
    {
        var other = new ItemId(Guid.NewGuid());
        SavingsSlice[] slices =
        [
            new(Envelope, AllocationClasses.Cash, AllocationLiquidity.Available, 1_000m),
            new(other, AllocationClasses.Cash, AllocationLiquidity.Available, 10_000m),
        ];
        var points = RunEvents(slices, withdrawals: [new(1, Envelope, 1_500m)]).Points;
        Assert.Equal(11_000m, points[0].Savings);
        Assert.Equal(9_500m, points[1].Savings);
        Assert.Equal(9_500m, points[12].Savings);
    }

    [Fact]
    public void Month_zero_events_are_in_todays_point()
    {
        var cashFlows = Enumerable.Range(0, 13).Select(k => k == 0 ? 2_000m : 0m).ToList();
        var points = RunEvents([Cash(10_000m)], cashFlows: cashFlows, withdrawals: [new(0, Envelope, 3_000m)]).Points;
        Assert.Equal(9_000m, points[0].Savings);
        Assert.Equal(9_000m, points[12].Savings);
    }
}
