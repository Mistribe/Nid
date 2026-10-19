using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Issue #34 §3 and the golden case of §8: a holding's gain, gain %, XIRR, period performance and explicit fees.</summary>
public class HoldingPerformanceTests
{
    private static readonly ItemId Item = ItemId.New();
    private static readonly Guid EuroFund = Guid.NewGuid();
    private static readonly Guid FundA = Guid.NewGuid();
    private static readonly LocalDate Start = new(2024, 1, 1);
    private static readonly LocalDate End2024 = new(2024, 12, 31);
    private static readonly LocalDate Mid2025 = new(2025, 6, 30);
    private static readonly LocalDate End2025 = new(2025, 12, 31);

    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();

    private static HoldingMovement Move(Guid holding, LocalDate on, string kind, decimal amount, decimal? units = null, decimal? price = null, string? switchRef = null)
        => new(Guid.NewGuid(), Item, holding, on, kind, units, price, amount, null, switchRef, null, null);

    private static HoldingSnapshot Snapshot(LocalDate on, params SnapshotLine[] lines) => new(Guid.NewGuid(), Item, on, null, true, lines);

    private static readonly HoldingMovement[] Movements =
    [
        Move(EuroFund, Start, MovementKinds.AllocationIn, 3_000m),
        Move(FundA, Start, MovementKinds.AllocationIn, 7_000m, 100m, 70m),
        Move(EuroFund, End2024, MovementKinds.Interest, 90m),
        Move(FundA, End2024, MovementKinds.Fee, 61.60m, -0.8m, 77m),
        Move(FundA, Mid2025, MovementKinds.SwitchOut, 1_600m, -20m, 80m, "s1"),
        Move(EuroFund, Mid2025, MovementKinds.SwitchIn, 1_600m, switchRef: "s1"),
        Move(EuroFund, End2025, MovementKinds.Interest, 116.70m),
        Move(FundA, End2025, MovementKinds.Fee, 60.00m, -0.8m, 75m),
    ];

    private static readonly HoldingSnapshot[] Snapshots =
    [
        Snapshot(End2025, new SnapshotLine(EuroFund, null, null, 4_806.70m, null), new SnapshotLine(FundA, 78.4m, 75m, 5_880m, null)),
        Snapshot(End2024, new SnapshotLine(EuroFund, null, null, 3_090m, null), new SnapshotLine(FundA, 99.2m, 77m, 7_638.40m, null)),
    ];

    private static readonly Flow[] Flows = [new(Guid.NewGuid(), Item, Start, FlowKind.Contribution, Eur(10_000m), null, null, null)];

    private static readonly Valuation[] Valuations =
        [new(Guid.NewGuid(), Item, End2024, Eur(10_728.40m), null), new(Guid.NewGuid(), Item, End2025, Eur(10_686.70m), null)];

    [Fact]
    public void Fund_a_reproduces_the_golden_case()
    {
        var a = HoldingMath.At(FundA, Snapshots, Movements, Currency.Eur, End2025)!;

        Assert.Equal((Eur(7_000m), Eur(1_600m), Eur(480.00m), 6.9m, 3.6m), (a.MoneyIn, a.MoneyOut, a.Gain, a.GainPct, a.AnnualReturnPct));
        Assert.Equal((Eur(121.60m), Eur(601.60m)), (a.ExplicitFees, a.GainBeforeFees));
        Assert.Equal(Eur(-158.40m), HoldingMath.Period(FundA, Snapshots, Movements, Currency.Eur, new LocalDate(2025, 1, 1), End2025).Performance);
    }

    [Fact]
    public void The_euro_fund_reproduces_the_golden_case()
    {
        var e = HoldingMath.At(EuroFund, Snapshots, Movements, Currency.Eur, End2025)!;

        Assert.Equal((Eur(4_600m), Eur(0m), Eur(206.70m), 4.5m, 3.0m), (e.MoneyIn, e.MoneyOut, e.Gain, e.GainPct, e.AnnualReturnPct));
        Assert.Equal(Eur(0m), e.ExplicitFees);
        Assert.Equal(Eur(116.70m), HoldingMath.Period(EuroFund, Snapshots, Movements, Currency.Eur, new LocalDate(2025, 1, 1), End2025).Performance);
    }

    [Fact]
    public void The_holdings_sum_to_the_unchanged_envelope_figures()
    {
        var envelope = EnvelopeMath.At(Valuations, Flows, Currency.Eur, ValuationCadence.Yearly, null, End2025);
        var period = EnvelopeMath.Period(Valuations, Flows, Currency.Eur, new LocalDate(2025, 1, 1), End2025);

        Assert.Equal((Eur(686.70m), 6.9m, 3.4m), (envelope.Gain!.Value, envelope.GainPct, envelope.AnnualReturnPct));
        Assert.Equal(Eur(-41.70m), period.Performance);
        Assert.Equal(686.70m, new[] { FundA, EuroFund }.Sum(h => HoldingMath.At(h, Snapshots, Movements, Currency.Eur, End2025)!.Gain.Amount));
        Assert.Equal(-41.70m, new[] { FundA, EuroFund }
            .Sum(h => HoldingMath.Period(h, Snapshots, Movements, Currency.Eur, new LocalDate(2025, 1, 1), End2025).Performance!.Value.Amount));
    }

    [Fact]
    public void The_series_gives_each_snapshot_with_money_in_minus_out()
        => Assert.Equal([(End2024, Eur(7_638.40m), Eur(7_000m)), (End2025, Eur(5_880m), Eur(5_400m))],
            HoldingMath.Series(FundA, Snapshots, Movements, Currency.Eur).Select(p => (p.AsOf, p.Value, p.NetContributions)));

    [Fact]
    public void Under_twelve_months_there_is_no_annual_return()
        => Assert.Null(HoldingMath.At(FundA, Snapshots, Movements, Currency.Eur, End2024)!.AnnualReturnPct);

    [Fact]
    public void Before_any_snapshot_there_are_no_figures() => Assert.Null(HoldingMath.At(FundA, Snapshots, Movements, Currency.Eur, Mid2025.PlusYears(-1)));

    [Fact]
    public void A_partial_snapshot_keeps_the_last_value_and_a_complete_one_without_the_line_is_zero()
    {
        var partial = Snapshots.Append(new HoldingSnapshot(Guid.NewGuid(), Item, Mid2025, null, false, [new SnapshotLine(EuroFund, null, null, 4_700m, null)])).ToList();
        Assert.Equal((End2024, 7_638.40m), HoldingMath.ValueAt(FundA, partial, Mid2025));

        var complete = Snapshots.Append(Snapshot(Mid2025, new SnapshotLine(EuroFund, null, null, 4_700m, null))).ToList();
        Assert.Equal((Mid2025, 0m), HoldingMath.ValueAt(FundA, complete, Mid2025));
    }

    [Fact]
    public void The_golden_movements_have_no_warning() => Assert.Empty(HoldingMath.Warnings(Movements, Flows));

    [Fact]
    public void Allocations_off_the_contribution_and_an_unbalanced_switch_warn_on_their_dates()
    {
        var moves = Movements.Append(Move(FundA, Start, MovementKinds.AllocationIn, 5m))
            .Append(Move(FundA, End2025, MovementKinds.Maturity, 1_050m, switchRef: "s2"))
            .Append(Move(EuroFund, End2025, MovementKinds.SwitchIn, 1_048.99m, switchRef: "s2"))
            .Append(Move(FundA, Mid2025, MovementKinds.AllocationOut, 100m))
            .ToList();

        Assert.Equal(
            [new HoldingWarning(HoldingWarnings.AllocationMismatch, null, Start), new HoldingWarning(HoldingWarnings.AllocationMismatch, null, Mid2025),
                new HoldingWarning(HoldingWarnings.SwitchUnbalanced, null, End2025)],
            HoldingMath.Warnings(moves, Flows));
        Assert.Empty(HoldingMath.Warnings([Move(FundA, End2025, MovementKinds.Maturity, 1_050m, switchRef: "s3"),
            Move(EuroFund, End2025, MovementKinds.SwitchIn, 1_049m, switchRef: "s3")], Flows));
    }

    private static readonly CostReport FundA2025 = new(FundA, 2025, 8.00m, 2.50m, 5.50m, null, null, null, null, null);

    [Fact]
    public void The_implicit_cost_of_fund_a_in_2025_is_the_golden_estimate()
        => Assert.Equal(Eur(168.98m), HoldingMath.ImplicitCost(FundA2025, Snapshots, Currency.Eur));

    [Fact]
    public void Without_a_value_at_the_start_of_the_year_there_is_no_implicit_cost()
        => Assert.Null(HoldingMath.ImplicitCost(FundA2025 with { Year = 2024 }, Snapshots, Currency.Eur));

    [Fact]
    public void A_cost_report_whose_gross_minus_fees_is_not_its_net_is_inconsistent()
    {
        Assert.False(HoldingMath.IsInconsistent(FundA2025));
        Assert.False(HoldingMath.IsInconsistent(FundA2025 with { NetReturnPct = 5.51m }));
        Assert.True(HoldingMath.IsInconsistent(FundA2025 with { NetReturnPct = 5.52m }));
    }

    [Fact]
    public void The_golden_units_are_all_explained() => Assert.Empty(HoldingMath.UnexplainedUnits(Snapshots, Movements, Currency.Eur));

    [Fact]
    public void Without_the_2025_fee_movement_the_missing_units_propose_a_fee_at_the_later_price()
        => Assert.Equal([new HoldingWarning(HoldingWarnings.UnexplainedUnits, FundA, End2025, -0.8m, Eur(60.00m))],
            HoldingMath.UnexplainedUnits(Snapshots, Movements.Where(m => !(m.HoldingId == FundA && m.OccurredOn == End2025)).ToList(), Currency.Eur));

    [Fact]
    public void Units_appearing_from_nowhere_warn_without_a_fee()
        => Assert.Equal([new HoldingWarning(HoldingWarnings.UnexplainedUnits, FundA, End2025, 0.2m, null)],
            HoldingMath.UnexplainedUnits(Snapshots, Movements.Select(m => m.HoldingId == FundA && m.OccurredOn == End2025 ? m with { Units = -0.6m } : m)
                .Append(Move(FundA, End2025, MovementKinds.Fee, 15m, -0.4m, 75m)).ToList(), Currency.Eur));

    [Fact]
    public void The_units_of_account_share_is_the_golden_one()
        => Assert.Equal(55.0m, HoldingMath.UcSharePct(Snapshots[0], new Dictionary<Guid, string> { [FundA] = "fund", [EuroFund] = "euro_fund" }));

    [Fact]
    public void Holding_period_performance_is_additive_and_the_holdings_sum_to_the_envelope_over_random_complete_data()
    {
        var random = new Random(20261019);
        Guid[] holdings = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        for (var run = 0; run < 200; run++)
        {
            var flows = new List<Flow>();
            var movements = new List<HoldingMovement>();
            var snapshots = new List<HoldingSnapshot>();
            var valuations = new List<Valuation>();
            for (var day = 0; day < 900; day += random.Next(20, 90))
            {
                var on = Start.PlusDays(day);
                switch (random.Next(0, 5))
                {
                    case 0: // a payment split across the holdings
                        var parts = holdings.Select(_ => (decimal)random.Next(0, 5_000)).ToArray();
                        if (parts.Sum() == 0) break;
                        flows.Add(new Flow(Guid.NewGuid(), Item, on, FlowKind.Contribution, Eur(parts.Sum()), null, null, null));
                        movements.AddRange(holdings.Zip(parts).Where(p => p.Second > 0).Select(p => Move(p.First, on, MovementKinds.AllocationIn, p.Second)));
                        break;
                    case 1: // a withdrawal from one holding
                        var amount = random.Next(1, 1_000);
                        flows.Add(new Flow(Guid.NewGuid(), Item, on, FlowKind.Withdrawal, Eur(amount), null, null, null));
                        movements.Add(Move(holdings[random.Next(0, 3)], on, MovementKinds.AllocationOut, amount));
                        break;
                    case 2: // a balanced switch
                        var moved = random.Next(1, 2_000);
                        var from = random.Next(0, 3);
                        var reference = Guid.NewGuid().ToString("N");
                        movements.Add(Move(holdings[from], on, random.Next(0, 2) == 0 ? MovementKinds.SwitchOut : MovementKinds.Maturity, moved, switchRef: reference));
                        movements.Add(Move(holdings[(from + 1) % 3], on, MovementKinds.SwitchIn, moved, switchRef: reference));
                        break;
                    case 3: // a fee or interest: part of performance
                        movements.Add(Move(holdings[random.Next(0, 3)], on, random.Next(0, 2) == 0 ? MovementKinds.Fee : MovementKinds.Interest, random.Next(1, 200)));
                        break;
                    default: // a complete snapshot, recorded as the envelope's valuation
                        var lines = holdings.Select(h => new SnapshotLine(h, null, null, random.Next(0, 20_000) + random.Next(0, 100) / 100m, null)).ToList();
                        snapshots.Add(Snapshot(on, lines.ToArray()));
                        valuations.Add(new Valuation(Guid.NewGuid(), Item, on, Eur(lines.Sum(l => l.Value)), null));
                        break;
                }
            }
            if (snapshots.Count < 2) continue;
            var dates = snapshots.Select(s => s.AsOf).Order().ToList();
            var (a, b, c) = (dates[0], dates[random.Next(0, dates.Count)], dates[^1]);
            decimal Perf(Guid h, LocalDate x, LocalDate y) => HoldingMath.Period(h, snapshots, movements, Currency.Eur, x, y).Performance!.Value.Amount;

            foreach (var h in holdings)
                Assert.Equal(Perf(h, a, c), Perf(h, a, b) + Perf(h, b, c));
            Assert.Equal(EnvelopeMath.At(valuations, flows, Currency.Eur, ValuationCadence.Monthly, null, c).Gain!.Value.Amount,
                holdings.Sum(h => HoldingMath.At(h, snapshots, movements, Currency.Eur, c)!.Gain.Amount));
            Assert.Equal(EnvelopeMath.Period(valuations, flows, Currency.Eur, a, c).Performance!.Value.Amount, holdings.Sum(h => Perf(h, a, c)));
        }
    }
}
