using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Investments dashboard spec §5.4: the holdings of the household's envelopes together, on a fixture household.</summary>
public class InvestmentsHoldingsTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);
    private static readonly RateTable Rates = new([new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, 7);
    private const string World = "IE00B4L5Y983";

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static decimal N(AmountView? money) => decimal.Parse(money!.Amount, CultureInfo.InvariantCulture);

    /// <summary>A household of a life insurance (four holdings), a PEA (four, one the same ETF) and a savings account without holdings.</summary>
    private sealed class Household
    {
        public readonly List<EnvelopeHolding> Holdings = [];
        public readonly List<HoldingSnapshot> Snapshots = [];
        public readonly List<HoldingMovement> Movements = [];
        public readonly Dictionary<ItemId, IReadOnlyList<CostReport>> Reports = [];
        public readonly List<EnvelopePerformance.Facts> Facts = [];

        public Guid Holding(ItemId envelope, string name, string kind, string? isin = null, LocalDate? maturesOn = null)
        {
            var id = Guid.NewGuid();
            Holdings.Add(new EnvelopeHolding(id, envelope, name, isin, kind, null, null, null, null, maturesOn, null, null, null));
            return id;
        }

        public void Snapshot(ItemId envelope, LocalDate on, params SnapshotLine[] lines)
            => Snapshots.Add(new HoldingSnapshot(Guid.NewGuid(), envelope, on, null, true, lines));

        public InvestmentsHoldings Build(IReadOnlyDictionary<ItemId, Application.Prices.EnvelopeEstimate>? estimates = null)
            => InvestmentsHoldingsMath.Build(Facts, new HoldingsExport(Holdings, Snapshots, Movements, new Dictionary<Guid, ImportMark>()),
                Reports, Currency.Eur, Rates, Today, estimates: estimates);
    }

    private static SnapshotLine Line(Guid holding, decimal value, decimal? units = null, decimal? price = null) => new(holding, units, price, value, null);

    private static (Household Household, Guid LifeWorld, Guid PeaWorld) Fixture(decimal? floor = 60m)
    {
        var h = new Household();
        var life = new EnvelopeFacts("Assurance-vie", EnvelopeKind.LifeInsurance) { UcShareFloorPct = floor }
            .Flow(D(2024, 12, 31), FlowKind.Opening, 60_000m).Value(D(2026, 9, 30), 78_400m);
        var pea = new EnvelopeFacts("PEA", EnvelopeKind.Pea).Flow(D(2024, 12, 31), FlowKind.Opening, 50_000m).Value(D(2026, 9, 30), 61_500m);
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings).Flow(D(2024, 12, 31), FlowKind.Opening, 22_000m).Value(D(2026, 9, 30), 22_950m);
        h.Facts.AddRange([life.Build(), pea.Build(), savings.Build()]);

        var euro = h.Holding(life.Id, "Fonds euros", "euro_fund");
        var lifeWorld = h.Holding(life.Id, "iShares MSCI World", "etf", World);
        var scpi = h.Holding(life.Id, "SCPI Pierre", "fund");
        var athena = h.Holding(life.Id, "Athena 2023", "structured", maturesOn: D(2026, 12, 5));
        h.Snapshot(life.Id, D(2024, 12, 31), Line(lifeWorld, 25_000m));
        h.Snapshot(life.Id, D(2025, 12, 31), Line(lifeWorld, 30_000m));
        h.Snapshot(life.Id, D(2026, 9, 30), Line(euro, 28_200m), Line(lifeWorld, 31_400m), Line(scpi, 9_800m), Line(athena, 9_000m));
        // 0.85 % of the mean of 25 000 and 30 000: 233.75 for 2025.
        h.Reports[life.Id] = [new CostReport(lifeWorld, 2025, 7m, 0.85m, 6.15m, null, null, null, null, null)];

        var peaWorld = h.Holding(pea.Id, "Amundi MSCI World", "etf", World);
        var emerging = h.Holding(pea.Id, "Amundi Emerging", "etf");
        var equities = h.Holding(pea.Id, "Actions", "equity");
        var cash = h.Holding(pea.Id, "Liquidités", "cash");
        // Two units went missing between June and September, and nothing says why.
        h.Snapshots.Add(new HoldingSnapshot(Guid.NewGuid(), pea.Id, D(2026, 6, 30), null, false, [Line(peaWorld, 38_000m, 100m, 380m)]));
        h.Snapshot(pea.Id, D(2026, 9, 30), Line(peaWorld, 38_900m, 98m, 396.938776m), Line(emerging, 9_600m), Line(equities, 11_100m), Line(cash, 1_700m));
        return (h, lifeWorld, peaWorld);
    }

    [Fact]
    public void What_is_detailed_unallocated_and_undetailed_adds_up_to_the_overviews_value()
    {
        var (h, _, _) = Fixture();

        var o = h.Build();

        Assert.Equal(78_400m + 61_500m + 22_950m, N(o.Value));
        Assert.Equal((78_400m + 61_300m, 200m, 22_950m), (N(o.Detailed), N(o.Unallocated), N(o.Undetailed)));
        Assert.Equal(N(o.Value), N(o.Detailed) + N(o.Unallocated) + N(o.Undetailed));
        Assert.Equal((2, 3), (o.DetailedCount, o.EnvelopeCount));
        Assert.Equal("Livret A", Assert.Single(o.UndetailedEnvelopes).Name);
        Assert.Equal(EnvelopeMath.RoundPercent(139_700m / 162_850m * 100m), o.DetailedPct);
    }

    [Fact]
    public void The_kinds_the_rows_and_the_holdings_held_twice()
    {
        var (h, _, peaWorld) = Fixture();

        var o = h.Build();

        Assert.Equal(("etf", 31_400m + 38_900m + 9_600m), (o.ByKind[0].Kind, N(o.ByKind[0].Value)));
        Assert.Equal(N(o.Detailed), o.ByKind.Sum(k => N(k.Value)));
        Assert.Equal(8, o.Holdings.Count);
        Assert.Equal((peaWorld, "PEA", "38900.00"), (o.Holdings[0].HoldingId, o.Holdings[0].EnvelopeName, o.Holdings[0].Value));
        Assert.Equal(EnvelopeMath.RoundPercent(38_900m / 162_850m * 100m), o.Holdings[0].SharePct);
        var world = Assert.Single(o.Concentrations);
        Assert.Equal((World, 70_300m, 2), (world.Isin, N(world.Value), world.EnvelopeIds.Count));
        Assert.Equal(EnvelopeMath.RoundPercent(70_300m / 162_850m * 100m), world.SharePct);
    }

    [Fact]
    public void The_fees_are_an_estimate_of_last_year_and_the_holdings_without_a_report_are_counted()
    {
        var (h, lifeWorld, _) = Fixture();

        var o = h.Build();

        Assert.Equal((2025, 233.75m, 0.9m), (o.CostYear, N(o.Cost), o.CostPct));
        Assert.Equal(233.75m, N(o.Holdings.Single(r => r.HoldingId == lifeWorld).ImplicitCost));
        // Eight holdings, one with a report, the cash without fees to report.
        Assert.Equal(6, o.WithoutReportCount);
    }

    [Fact]
    public void Each_envelopes_fees_are_weighted_by_its_lines_cash_aside_and_an_envelope_without_a_report_says_so()
    {
        var (h, lifeWorld, _) = Fixture();
        var life = h.Facts[0].Item.Id;
        var euro = h.Holdings.Single(x => x.Name == "Fonds euros").Id;
        h.Reports[life] = [.. h.Reports[life], new CostReport(euro, 2025, 2.6m, 0.6m, 2m, 0.6m, 0m, null, null, null)];

        var o = h.Build();

        // Life: 28 200 at 0.6 % and 31 400 at 0.85 % of 78 400 of lines; only the euro fund states its contract and support fees.
        var lifeFees = o.EnvelopeFees[0];
        Assert.Equal(("Assurance-vie", 0.6m, 0m, 0.73m, 436.10m, 76.0m),
            (lifeFees.Name, lifeFees.ContractFeesPct, lifeFees.SupportFeesPct, lifeFees.TotalFeesPct, N(lifeFees.YearlyCost), lifeFees.CoveredPct));
        // PEA: no report, its cash left out of the coverage.
        var pea = o.EnvelopeFees[1];
        Assert.Equal(("PEA", null, null, (decimal?)null, 0.0m), (pea.Name, pea.TotalFeesPct, pea.YearlyCost, pea.ContractFeesPct, pea.CoveredPct));
        Assert.Equal(2, o.EnvelopeFees.Count); // the savings account has no snapshot

        // The euro fund was not held at the ends of 2024 and 2025: its estimated cost is 0, the ETF's is all of it.
        Assert.Equal(100.0m, o.Holdings.Single(r => r.HoldingId == lifeWorld).CostSharePct);
        Assert.Equal(0m, o.Holdings.Single(r => r.HoldingId == euro).CostSharePct);
        Assert.Null(o.Holdings.Single(r => r.Name == "SCPI Pierre").CostSharePct); // no report, no share
    }

    [Fact]
    public void Each_holding_costs_its_fees_compounded_against_its_envelopes_and_its_kinds_reference()
    {
        var (h, lifeWorld, _) = Fixture();
        var life = h.Facts[0].Item.Id;
        var pea = h.Facts[1].Item.Id;
        var euro = h.Holdings.Single(x => x.Name == "Fonds euros").Id;
        var emerging = h.Holdings.Single(x => x.Name == "Amundi Emerging").Id;
        h.Reports[life] = [.. h.Reports[life], new CostReport(euro, 2025, 2.6m, 0.6m, 2m, 0.6m, 0m, null, null, null)];
        h.Reports[pea] = [new CostReport(emerging, 2025, 5m, 0.1m, 4.9m, null, null, null, null, null)];

        var p = h.Build().Projection!;

        Assert.Equal(4m, p.AssumedReturnPct);
        // The life insurance first: dearer than its reference. Its euro fund's reference is the contract's 0.5 % alone, its ETF's 0.5 + 0.2 %.
        Assert.Equal([life, pea], p.Envelopes.Select(e => e.EnvelopeId));
        var lifeCost = p.Envelopes[0];
        var etf = lifeCost.Holdings.Single(x => x.HoldingId == lifeWorld);
        var fund = lifeCost.Holdings.Single(x => x.HoldingId == euro);
        Assert.Equal((0.85m, 0.7m, 266.90m), (etf.FeesPct, etf.ReferencePct, N(etf.Yearly)));
        Assert.Equal((0.6m, 0.5m, 169.20m), (fund.FeesPct, fund.ReferencePct, N(fund.Yearly)));
        // 28 200 × (1.04^n − 1.034^n), and × (1.035^20 − 1.034^20) beyond the reference; computed in double, so to the cent at most.
        Assert.InRange(N(fund.Cost10), 2_346.66m, 2_346.68m);
        Assert.InRange(N(fund.Cost20), 6_752.01m, 6_752.03m);
        Assert.InRange(N(fund.Gap20), 1_074.39m, 1_074.41m);
        Assert.InRange(N(etf.Gap20), 1_721.77m, 1_721.79m);
        // The envelope adds its holdings up and weights its rates by their values.
        Assert.Equal((59_600m, 0.73m, 0.61m), (N(lifeCost.Total.Value), lifeCost.Total.FeesPct, lifeCost.Total.ReferencePct));
        Assert.Equal(N(etf.Gap20) + N(fund.Gap20), N(lifeCost.Total.Gap20));

        // The PEA's emerging ETF costs 0.1 % against 0.2 %: a negative gap, left out of the fees above the reference.
        var cheap = Assert.Single(p.Envelopes[1].Holdings);
        Assert.InRange(N(cheap.Gap20), -393.59m, -393.57m);
        Assert.Equal(N(lifeCost.Total.Gap20), N(p.AboveReference20));
        Assert.Equal(N(etf.Yearly) + N(fund.Yearly) + N(cheap.Yearly), N(p.Yearly));
        Assert.Equal(N(etf.Cost20) + N(fund.Cost20) + N(cheap.Cost20), N(p.Cost20));
    }

    [Fact]
    public void The_households_references_and_return_replace_the_defaults()
    {
        var (h, lifeWorld, _) = Fixture();
        var chosen = FeesProjection.Effective(new FeeReferences(5m, new Dictionary<string, decimal> { ["life_insurance"] = 0.6m },
            new Dictionary<string, decimal> { ["etf"] = 0.25m }));

        var o = InvestmentsHoldingsMath.Build(h.Facts, new HoldingsExport(h.Holdings, h.Snapshots, h.Movements, new Dictionary<Guid, ImportMark>()),
            h.Reports, Currency.Eur, Rates, Today, chosen);

        // 0.6 + 0.25 % is the ETF's 0.85 %: no gap, whatever the return.
        var etf = o.Projection!.Envelopes.Single().Holdings.Single(x => x.HoldingId == lifeWorld);
        Assert.Equal((5m, 0.85m, 0m), (o.Projection.AssumedReturnPct, etf.ReferencePct, N(etf.Gap20)));
        Assert.Equal(0m, N(o.Projection.AboveReference20));
        // What was not chosen keeps its default; the response says what was used.
        Assert.Equal((0.5m, 0.2m), (o.References.Envelopes["per"], o.References.Holdings["fund"]));
        Assert.False(o.References.Holdings.ContainsKey("cash"));
    }

    [Theory]
    [InlineData(21, "etf", 0.2)]
    [InlineData(4, "cash", 0.2)]
    [InlineData(4, "etf", -0.1)]
    [InlineData(4, "etf", 10.5)]
    public void A_reference_out_of_range_or_of_an_unknown_kind_is_refused(decimal ret, string kind, decimal fees)
    {
        var failure = FeesProjection.Check(new FeeReferences(ret, new Dictionary<string, decimal>(), new Dictionary<string, decimal> { [kind] = fees }));
        Assert.Equal(InvestmentsCodes.InvalidFeeReferences, failure?.Code);
        Assert.Null(FeesProjection.Check(FeesProjection.Defaults));
    }

    [Fact]
    public void Without_a_report_there_is_no_projection_and_a_foreign_envelopes_cost_is_converted()
    {
        var (none, _, _) = Fixture();
        none.Reports.Clear();
        Assert.Null(none.Build().Projection);

        var h = new Household();
        var usd = new EnvelopeFacts("Courtier US", EnvelopeKind.Brokerage, "USD").Flow(D(2025, 1, 1), FlowKind.Opening, 10_000m).Value(D(2026, 9, 30), 10_000m);
        h.Facts.Add(usd.Build());
        var fund = h.Holding(usd.Id, "S&P 500", "etf");
        h.Snapshot(usd.Id, D(2026, 9, 30), Line(fund, 10_000m));
        h.Reports[usd.Id] = [new CostReport(fund, 2025, 10m, 0.5m, 9.5m, null, null, null, null, null)];

        var o = h.Build();

        // 0.5 % of 10 000 $ is 50 $, 45 € at 0.9.
        Assert.Equal((0.5m, 45m), (o.EnvelopeFees.Single().TotalFeesPct, N(o.EnvelopeFees.Single().YearlyCost)));
        Assert.Equal((9_000m, 45m), (N(o.Projection!.Envelopes.Single().Total.Value), N(o.Projection.Yearly)));
    }

    [Fact]
    public void The_share_of_units_of_account_and_what_to_watch()
    {
        var (h, _, peaWorld) = Fixture(floor: 70m);

        var o = h.Build();

        Assert.Equal(64.0m, o.UcSharePct);
        var life = Assert.Single(o.EnvelopesUc);
        Assert.Equal((64.0m, 70m), (life.UcSharePct, life.FloorPct));
        Assert.Equal(["uc_share_below_floor", "unexplained_units", "holding_maturity"], o.Watch.Select(w => w.Code));
        Assert.Equal((peaWorld, D(2026, 9, 30)), (o.Watch[1].HoldingId!.Value, o.Watch[1].On!.Value));
    }

    /// <summary>
    /// Holding prices spec §5.4: the life insurance estimated on 21/10, its ETF at 41 400 instead of 31 400. The detail follows the
    /// estimate; the share of units of account and the fees stay on the statement, as the floor's warning.
    /// </summary>
    [Fact]
    public void An_estimated_envelope_is_detailed_by_its_estimated_lines_its_units_of_account_and_fees_by_its_statement()
    {
        var (h, lifeWorld, _) = Fixture(floor: 70m);
        var life = h.Facts[0].Item.Id;
        var lines = h.Snapshots.Last(s => s.Envelope == life).Lines
            .Select(l => new EstimatedLine(l.HoldingId, l.HoldingId == lifeWorld ? 41_400m : l.Value)).ToList();
        var estimate = new Application.Prices.EnvelopeEstimate(life, Money.Create(88_400m, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException(),
            D(2026, 10, 21), D(2026, 9, 30), false, [], null, lines);

        var o = h.Build(new Dictionary<ItemId, Application.Prices.EnvelopeEstimate> { [life] = estimate });

        Assert.True(o.Estimated);
        Assert.Equal((88_400m + 61_500m + 22_950m, 88_400m + 61_300m, 200m), (N(o.Value), N(o.Detailed), N(o.Unallocated)));
        Assert.Equal(N(o.Value), N(o.Detailed) + N(o.Unallocated) + N(o.Undetailed));
        Assert.Equal(41_400m + 38_900m, N(Assert.Single(o.Concentrations).Value));
        Assert.Equal(("41400.00", D(2026, 10, 21)), (o.Holdings.Single(r => r.HoldingId == lifeWorld).Value, o.Holdings.Single(r => r.HoldingId == lifeWorld).AsOf));
        // As without the estimate: 64 %, under the floor of 70 %.
        Assert.Equal((64.0m, 64.0m), (o.UcSharePct, Assert.Single(o.EnvelopesUc).UcSharePct));
        Assert.Contains(o.Watch, w => w.Code == "uc_share_below_floor");
        Assert.Equal((233.75m, 0.9m), (N(o.Cost), o.CostPct));
    }

    [Fact]
    public void Without_a_snapshot_nothing_is_detailed()
    {
        var h = new Household();
        h.Facts.Add(new EnvelopeFacts("Livret A", EnvelopeKind.Savings).Flow(D(2025, 1, 1), FlowKind.Opening, 100m).Value(D(2026, 9, 30), 100m).Build());

        var o = h.Build();

        Assert.Equal((0, 1), (o.DetailedCount, o.EnvelopeCount));
        Assert.Null(o.Detailed);
        Assert.Null(o.Cost);
        Assert.Null(o.UcSharePct);
        Assert.Empty(o.Holdings);
    }
}
