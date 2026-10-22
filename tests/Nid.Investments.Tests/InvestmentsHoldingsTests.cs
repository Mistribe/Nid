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

        public InvestmentsHoldings Build()
            => InvestmentsHoldingsMath.Build(Facts, new HoldingsExport(Holdings, Snapshots, Movements, new Dictionary<Guid, ImportMark>()),
                Reports, Currency.Eur, Rates, Today);
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
