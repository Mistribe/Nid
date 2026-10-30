using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Issue #104: an envelope's value by asset class and liquidity.</summary>
public class EnvelopeAllocationTests
{
    private static readonly ItemId Envelope = new(Guid.NewGuid());

    private static EnvelopeHolding Holding(string kind, string? assetClass = null)
        => new(Guid.NewGuid(), Envelope, kind, null, kind, assetClass, null, null, null, null, null, null, null);

    private static HoldingSnapshot Snapshot(LocalDate on, params (EnvelopeHolding Holding, decimal Value)[] lines)
        => new(Guid.NewGuid(), Envelope, on, null, true, lines.Select(l => new SnapshotLine(l.Holding.Id, null, null, l.Value, null)).ToList());

    private static Dictionary<Guid, EnvelopeHolding> Of(params EnvelopeHolding[] holdings) => holdings.ToDictionary(h => h.Id);

    [Fact]
    public void Without_a_snapshot_the_kind_of_envelope_classes_it_a_savings_account_exactly_a_pea_approximately()
    {
        Assert.Equal([new AllocationSlice("cash", "available", 1000m, false)], EnvelopeAllocation.Of(EnvelopeKind.Savings, 1000m, null, Of()));
        Assert.Equal([new AllocationSlice("equity", "days", 1000m, true)], EnvelopeAllocation.Of(EnvelopeKind.Pea, 1000m, null, Of()));
        Assert.Equal([new AllocationSlice("mixed", "locked", 1000m, true)], EnvelopeAllocation.Of(EnvelopeKind.Per, 1000m, null, Of()));
        Assert.Equal([new AllocationSlice("mixed", "days", 1000m, true)], EnvelopeAllocation.Of(EnvelopeKind.LifeInsurance, 1000m, null, Of()));
        Assert.Equal([new AllocationSlice("mixed", "locked", 1000m, true)], EnvelopeAllocation.Of(EnvelopeKind.Tontine, 1000m, null, Of()));
    }

    [Fact]
    public void A_snapshot_splits_the_envelopes_value_pro_rata_by_class_the_slices_summing_to_it_exactly()
    {
        var euro = Holding("euro_fund");
        var etf = Holding("etf");
        var bonds = Holding("fund", assetClass: "bond");
        var slices = EnvelopeAllocation.Of(EnvelopeKind.LifeInsurance, 10_000m,
            Snapshot(new LocalDate(2026, 9, 30), (euro, 3000m), (etf, 3000m), (bonds, 3000m)), Of(euro, etf, bonds));

        Assert.Equal(["bond", "equity", "capital_guaranteed"], slices.Select(s => s.Class));
        Assert.All(slices, s => Assert.Equal("days", s.Liquidity));
        Assert.Equal([false, true, false], slices.Select(s => s.Approximate)); // the ETF's class is guessed, the fund's is declared
        Assert.Equal(10_000m, slices.Sum(s => s.Amount));
        Assert.Equal(3333.33m, Math.Round(slices[0].Amount, 2));
    }

    [Fact]
    public void Lines_of_the_same_class_are_one_slice_and_a_zero_line_counts_for_nothing()
    {
        var a = Holding("equity");
        var b = Holding("etf", assetClass: "equity");
        var gone = Holding("bond");
        var slices = EnvelopeAllocation.Of(EnvelopeKind.Pea, 900m, Snapshot(new LocalDate(2026, 9, 30), (a, 100m), (b, 200m), (gone, 0m)), Of(a, b, gone));

        Assert.Equal([new AllocationSlice("equity", "days", 900m, false)], slices);
    }

    [Fact]
    public void Estimated_lines_weigh_instead_of_the_snapshot_the_slices_summing_to_the_value_exactly()
    {
        var euro = Holding("euro_fund");
        var etf = Holding("etf");
        var snapshot = Snapshot(new LocalDate(2026, 9, 30), (euro, 500m), (etf, 1000m));
        var slices = EnvelopeAllocation.Of(EnvelopeKind.Pea, 1_600.01m, snapshot, Of(euro, etf),
            [new EstimatedLine(euro.Id, 500m), new EstimatedLine(etf.Id, 1100m)]);

        // The statement's weights would give the ETF 1 066.67; the estimated lines give it 1 100.
        Assert.Equal([("equity", 1100.01m), ("capital_guaranteed", 500m)], slices.Select(s => (s.Class, Math.Round(s.Amount, 2))));
        Assert.Equal(1_600.01m, slices.Sum(s => s.Amount));
    }

    [Fact]
    public void A_snapshot_without_a_positive_total_falls_back_to_the_kind_of_envelope()
    {
        var a = Holding("equity");
        Assert.Equal([new AllocationSlice("cash", "available", 50m, false)],
            EnvelopeAllocation.Of(EnvelopeKind.Checking, 50m, Snapshot(new LocalDate(2026, 9, 30), (a, 0m)), Of(a)));
    }

    [Fact]
    public void The_latest_snapshot_on_or_before_today_is_the_one_read()
    {
        var today = new LocalDate(2026, 10, 22);
        HoldingSnapshot[] snapshots = [Snapshot(today.PlusDays(-30)), Snapshot(today.PlusDays(-1)), Snapshot(today.PlusDays(1))];
        Assert.Equal(today.PlusDays(-1), EnvelopeAllocation.Latest(snapshots, Envelope, today)!.AsOf);
    }
}
