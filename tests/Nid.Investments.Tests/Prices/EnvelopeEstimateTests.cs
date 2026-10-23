using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests.Prices;

public class EnvelopeEstimateTests
{
    private static readonly ItemId Envelope = ItemId.New();
    private static readonly LocalDate Today = new(2026, 10, 23);
    private static readonly LocalDate Statement = new(2026, 9, 30);
    private static readonly Guid Etf = Guid.NewGuid();
    private static readonly Guid EuroFund = Guid.NewGuid();

    private static LocalDate Oct(int day) => new(2026, 10, day);

    private static EnvelopeHolding Holding(Guid id, string kind = "etf", string? isin = "IE00B4L5Y983", bool autoPrice = true)
        => new(id, Envelope, "Support", isin, kind, null, null, null, null, null, null, null, null, autoPrice);

    private static HoldingSnapshot Snapshot(LocalDate asOf, bool complete, params SnapshotLine[] lines)
        => new(Guid.NewGuid(), Envelope, asOf, null, complete, lines);

    private static SnapshotLine Line(Guid holding, decimal? units, decimal? unitPrice, decimal value) => new(holding, units, unitPrice, value, null);

    private static HoldingMovement Movement(Guid holding, LocalDate on, string kind, decimal amount, decimal? units = null, decimal? unitPrice = null)
        => new(Guid.NewGuid(), Envelope, holding, on, kind, units, unitPrice, amount, null, null, null, null);

    private static HoldingPrice Price(decimal price, LocalDate on) => new(price, on, null, d => d >= on ? price : null);

    private static readonly HoldingPrice NoPrice = new(null, null, null, _ => null);

    // Snapshot 30/09: ETF 10 u @ 100 = 1 000, euro fund 500; valuation 1 500 that day.
    private static readonly HoldingSnapshot Base = Snapshot(Statement, true, Line(Etf, 10m, 100m, 1_000m), Line(EuroFund, null, null, 500m));

    private static readonly EnvelopeHolding[] BaseHoldings = [Holding(Etf), Holding(EuroFund, "euro_fund", null)];

    private static EnvelopeEstimateResult Estimate(
        IReadOnlyList<HoldingMovement> movements, IReadOnlyDictionary<Guid, HoldingPrice> prices, LocalDate? today = null,
        IReadOnlyList<EnvelopeHolding>? holdings = null, HoldingSnapshot? snapshot = null, decimal? valuation = 1_500m)
        => EnvelopeEstimate.Of([snapshot ?? Base], valuation, Statement, holdings ?? BaseHoldings, movements, prices, today ?? Today);

    [Fact]
    public void All_priced_lines_are_units_times_price()
    {
        var estimate = Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = Price(110m, Oct(20)) });
        Assert.Equal(1_600m, estimate.Value);
        Assert.Equal(Oct(20), estimate.AsOf);
        Assert.Equal(Statement, estimate.BasedOn);
        Assert.Empty(estimate.Stale);
        Assert.Null(estimate.MissingReason);
    }

    [Fact]
    public void A_purchase_after_the_snapshot_adds_units()
    {
        var estimate = Estimate([Movement(Etf, Oct(10), MovementKinds.AllocationIn, 540m, 5m, 108m)],
            new Dictionary<Guid, HoldingPrice> { [Etf] = Price(110m, Oct(20)) });
        Assert.Equal(15m * 110m + 500m, estimate.Value);
    }

    [Fact]
    public void An_amount_only_contribution_buys_units_at_the_price_of_its_date()
    {
        var price = new HoldingPrice(110m, Oct(20), null, d => d >= Oct(10) ? 110m : null);
        var estimate = Estimate([Movement(Etf, Oct(10), MovementKinds.AllocationIn, 220m)], new Dictionary<Guid, HoldingPrice> { [Etf] = price });
        Assert.Equal(12m * 110m + 500m, estimate.Value);
    }

    [Fact]
    public void A_switch_to_the_euro_fund_keeps_the_total()
    {
        var prices = new Dictionary<Guid, HoldingPrice> { [Etf] = Price(105m, Oct(15)) };
        var before = Estimate([], prices, Oct(22));
        var after = Estimate(
            [Movement(Etf, Oct(15), MovementKinds.SwitchOut, 210m, -2m), Movement(EuroFund, Oct(15), MovementKinds.SwitchIn, 210m)],
            prices, Oct(22));
        Assert.Equal(1_550m, before.Value);
        Assert.Equal(8m * 105m + 710m, after.Value);
        Assert.Empty(after.Stale);
    }

    [Fact]
    public void A_holding_bought_after_the_snapshot_starts_from_zero()
    {
        var bought = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        var estimate = Estimate([Movement(bought, Oct(5), MovementKinds.AllocationIn, 150m, 3m, 50m)],
            new Dictionary<Guid, HoldingPrice> { [Etf] = Price(110m, Oct(20)), [bought] = Price(60m, Oct(21)), [untouched] = NoPrice },
            holdings: [.. BaseHoldings, Holding(bought, isin: "FR0000120271"), Holding(untouched, isin: "US0378331005")]);
        Assert.Equal(1_100m + 500m + 180m, estimate.Value);
        Assert.Equal(Oct(21), estimate.AsOf);
        Assert.Empty(estimate.Stale);
    }

    [Fact]
    public void Unallocated_is_kept()
    {
        var estimate = Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = Price(110m, Oct(20)) }, valuation: 1_600m);
        Assert.Equal(1_700m, estimate.Value);
    }

    [Fact]
    public void The_value_is_rounded_to_the_cent()
    {
        var estimate = Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = Price(110.0049m, Oct(20)) });
        Assert.Equal(1_600.05m, estimate.Value);
    }

    [Fact]
    public void Line_reasons()
    {
        Guid noPrice = Guid.NewGuid(), oldPrice = Guid.NewGuid(), stale = Guid.NewGuid(), off = Guid.NewGuid(), rejected = Guid.NewGuid(),
            notFound = Guid.NewGuid(), rate = Guid.NewGuid(), noUnits = Guid.NewGuid();
        var snapshot = Snapshot(Statement, true,
            Line(Etf, 10m, 100m, 1_000m), Line(EuroFund, null, null, 500m),
            Line(noPrice, 1m, 100m, 100m), Line(oldPrice, 1m, 50m, 50m), Line(stale, 2m, 100m, 200m), Line(off, 3m, 100m, 300m),
            Line(rejected, 4m, 100m, 400m), Line(notFound, 5m, 100m, 500m), Line(rate, 6m, 100m, 600m), Line(noUnits, null, null, 700m));
        var holdings = new[] { noPrice, oldPrice, stale, off, rejected, notFound, rate, noUnits }
            .Select(id => Holding(id, autoPrice: id != off)).Concat(BaseHoldings).ToList();
        var prices = new Dictionary<Guid, HoldingPrice>
        {
            [Etf] = Price(110m, Oct(20)),
            [noPrice] = NoPrice,
            [oldPrice] = Price(60m, Statement),
            [stale] = Price(120m, Oct(15)),
            [off] = Price(999m, Oct(20)),
            [rejected] = new(null, null, EstimateReasons.RejectedPrice, _ => null),
            [notFound] = new(null, null, EstimateReasons.NotFound, _ => null),
            [rate] = new(null, null, EstimateReasons.RateMissing, _ => null),
            [noUnits] = Price(10m, Oct(20)),
        };
        var estimate = EnvelopeEstimate.Of([snapshot], 4_350m, Statement, holdings,
            [Movement(noPrice, Oct(5), MovementKinds.AllocationIn, 50m), Movement(noUnits, Oct(6), MovementKinds.SwitchOut, 70m),
                Movement(noUnits, Oct(7), MovementKinds.Fee, 5m)],
            prices, Today);
        Assert.Equal(
            [
                new EstimateLine(noPrice, EstimateReasons.NoPrice), new EstimateLine(oldPrice, EstimateReasons.NoPrice),
                new EstimateLine(stale, EstimateReasons.StalePrice), new EstimateLine(off, EstimateReasons.AutoPriceOff),
                new EstimateLine(rejected, EstimateReasons.RejectedPrice), new EstimateLine(notFound, EstimateReasons.NotFound),
                new EstimateLine(rate, EstimateReasons.RateMissing), new EstimateLine(noUnits, EstimateReasons.NoUnits),
            ],
            estimate.Stale);
        // Statement values, + 50 in on noPrice, − 70 out on noUnits (a fee is not money out); the ETF at 110.
        Assert.Equal(150m + 50m + 200m + 300m + 400m + 500m + 600m + 630m + 1_100m + 500m, estimate.Value);
        Assert.Equal(Oct(20), estimate.AsOf);
    }

    [Fact]
    public void Missing_reasons()
    {
        var prices = new Dictionary<Guid, HoldingPrice> { [Etf] = Price(110m, Oct(20)) };
        Assert.Equal(EstimateReasons.NoSnapshot, EnvelopeEstimate.Of([], null, null, BaseHoldings, [], prices, Today).MissingReason);
        Assert.Equal(EstimateReasons.NoSnapshot,
            EnvelopeEstimate.Of([Snapshot(Oct(24), true, Line(Etf, 10m, 100m, 1_000m))], null, null, BaseHoldings, [], prices, Today).MissingReason);
        var partial = EnvelopeEstimate.Of([Base, Snapshot(Oct(1), false, Line(Etf, 10m, 100m, 1_000m))], null, null, BaseHoldings, [], prices, Today);
        Assert.Equal(EstimateReasons.IncompleteSnapshot, partial.MissingReason);
        Assert.Null(partial.Value);
        Assert.Equal(EstimateReasons.NewerValuation, EnvelopeEstimate.Of([Base], 1_500m, Oct(1), BaseHoldings, [], prices, Today).MissingReason);
        Assert.Equal(EstimateReasons.NoNewerPrice,
            Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = Price(110m, Statement) }).MissingReason);
        Assert.Equal(EstimateReasons.NoNewerPrice, Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = NoPrice }).MissingReason);
    }

    [Fact]
    public void Without_a_newer_price_the_stale_lines_keep_their_reason()
    {
        var estimate = Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = new(null, null, EstimateReasons.RejectedPrice, _ => null) });
        Assert.Equal(EstimateReasons.NoNewerPrice, estimate.MissingReason);
        Assert.Null(estimate.Value);
        Assert.Equal(Statement, estimate.BasedOn);
        Assert.Equal([new EstimateLine(Etf, EstimateReasons.RejectedPrice)], estimate.Stale);
    }

    [Fact]
    public void Cours_automatique_off_has_no_effect_without_an_isin()
    {
        var estimate = Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = NoPrice },
            holdings: [Holding(Etf, isin: null, autoPrice: false), Holding(EuroFund, "euro_fund", null)]);
        Assert.Equal([new EstimateLine(Etf, EstimateReasons.NoPrice)], estimate.Stale);
    }

    [Fact]
    public void An_unconvertible_price_older_than_a_week_is_stale()
    {
        var estimate = Estimate([], new Dictionary<Guid, HoldingPrice> { [Etf] = new(null, Oct(10), null, _ => null) });
        Assert.Equal([new EstimateLine(Etf, EstimateReasons.StalePrice)], estimate.Stale);
    }

    [Fact]
    public void Units_at_a_date_without_movement_are_the_line()
    {
        Assert.Equal(10m, HoldingMath.UnitsAt(Line(Etf, 10m, 100m, 1_000m), [], Statement, Today, _ => null));
        Assert.Equal(10m, HoldingMath.UnitsAt(Line(Etf, null, 100m, 1_000m), [], Statement, Today, _ => null));
        Assert.Null(HoldingMath.UnitsAt(Line(Etf, null, null, 1_000m), [], Statement, Today, _ => null));
    }

    [Fact]
    public void Units_at_a_date_count_movements_after_the_snapshot_until_the_date()
    {
        var line = Line(Etf, 10m, 100m, 1_000m);
        HoldingMovement[] movements =
        [
            Movement(Etf, Statement, MovementKinds.AllocationIn, 100m, 1m), // on the snapshot date: in it already
            Movement(Etf, Oct(5), MovementKinds.AllocationIn, 300m, 3m),
            Movement(Etf, Oct(6), MovementKinds.SwitchOut, 210m, unitPrice: 105m),
            Movement(EuroFund, Oct(7), MovementKinds.AllocationIn, 999m, 9m), // another holding
            Movement(Etf, Oct(24), MovementKinds.AllocationIn, 700m, 7m), // after the date
        ];
        Assert.Equal(10m + 3m - 2m, HoldingMath.UnitsAt(line, movements, Statement, Today, _ => null));
    }

    [Fact]
    public void An_amount_only_movement_takes_its_own_price_then_the_price_of_its_date_then_the_line_price()
    {
        var line = Line(Etf, 10m, 100m, 1_000m);
        Assert.Equal(12m, HoldingMath.UnitsAt(line, [Movement(Etf, Oct(5), MovementKinds.AllocationIn, 220m, unitPrice: 110m)], Statement, Today, _ => 200m));
        Assert.Equal(11m, HoldingMath.UnitsAt(line, [Movement(Etf, Oct(5), MovementKinds.AllocationIn, 220m)], Statement, Today,
            d => d == Oct(5) ? 220m : null));
        Assert.Equal(12.2m, HoldingMath.UnitsAt(line, [Movement(Etf, Oct(5), MovementKinds.AllocationIn, 220m)], Statement, Today, _ => null));
        // Without any price, the movement's units are unknown and so are the line's.
        Assert.Null(HoldingMath.UnitsAt(line with { UnitPrice = null }, [Movement(Etf, Oct(5), MovementKinds.AllocationIn, 220m)], Statement, Today, _ => null));
    }
}
