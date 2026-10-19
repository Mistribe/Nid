using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Application;

/// <summary>An amount on the wire: a decimal string in invariant culture and its currency (same shape as Subscriptions' MoneyView).</summary>
public sealed record AmountView(string Amount, string Currency)
{
    public static AmountView From(Money money) => new(money.AmountText, money.Currency.Code);

    public static AmountView? From(Money? money) => money is { } m ? From(m) : null;
}

public sealed record FiguresView(
    AmountView? Value, LocalDate? AsOf, AmountView? NetContributions, AmountView? Gain, decimal? GainPct, decimal? AnnualReturnPct,
    AmountView? Fees, AmountView? GainAfterFees, AmountView? Income,
    AmountView ContributionsSince, AmountView WithdrawalsSince, bool MissingOpening, int? StaleDays)
{
    public static FiguresView From(EnvelopeFigures f) => new(
        AmountView.From(f.Value), f.AsOf, AmountView.From(f.NetContributions), AmountView.From(f.Gain), f.GainPct, f.AnnualReturnPct,
        AmountView.From(f.Fees), AmountView.From(f.GainAfterFees), AmountView.From(f.Income),
        AmountView.From(f.ContributionsSince), AmountView.From(f.WithdrawalsSince), f.MissingOpening, f.StaleDays);
}

public sealed record EnvelopeSummary(
    ItemId Id, string DisplayName, string Kind, string Currency, string Visibility, bool Closed, bool CanWrite, FiguresView Figures,
    IReadOnlyList<ItemOwnerView> Owners);

public sealed record EnvelopeList(IReadOnlyList<EnvelopeSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => i.Id);
}

public sealed record ValuationView(Guid Id, LocalDate AsOf, AmountView Amount, string? Note);

public sealed record FlowView(Guid Id, LocalDate OccurredOn, string Kind, AmountView Amount, string? Category, string? ExternalRef, string? Note);

public sealed record EnvelopeDetail(
    ItemId Id, string DisplayName, string Kind, string Currency, string Visibility, bool Closed, bool CanWrite,
    string? Institution, string? AccountHint, LocalDate? OpenedOn, string ValuationCadence, int? StaleAfterDays, string? Note,
    FiguresView Figures, IReadOnlyList<ValuationView> Valuations, IReadOnlyList<FlowView> Flows) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

public sealed record PeriodView(
    LocalDate From, LocalDate To, LocalDate? StartAsOf, AmountView? StartValue, LocalDate? EndAsOf, AmountView? EndValue,
    AmountView? NetContributions, AmountView? Performance)
{
    // Named Of, not From: a positional property already named From collides with a same-named static method (CS0102).
    public static PeriodView Of(PeriodPerformance p) => new(p.From, p.To, p.StartAsOf, AmountView.From(p.StartValue),
        p.EndAsOf, AmountView.From(p.EndValue), AmountView.From(p.NetContributions), AmountView.From(p.Performance));
}

public sealed record SeriesPoint(LocalDate AsOf, AmountView Value, AmountView NetContributions);

public sealed record EnvelopeSeries(ItemId Id, IReadOnlyList<SeriesPoint> Points) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [Id];
}

public sealed record HoldingWarningView(string Code, Guid? HoldingId)
{
    public static HoldingWarningView Of(HoldingWarning w) => new(w.Code, w.HoldingId);
}

/// <summary>A holding's line in the snapshot read: as printed, with its weight in the snapshot (0.1 %, null when the total is zero).</summary>
public sealed record LineView(string? Units, string? UnitPrice, AmountView Value, string? CostBasisPerUnit, decimal? WeightPct)
{
    public static LineView Of(SnapshotLine l, decimal? weight, Currency currency)
        => new(Units6.Of(l.Units), Units6.Of(l.UnitPrice), AmountView.From(Amounts.Of(l.Value, currency)), Units6.Of(l.CostBasisPerUnit), weight);
}

/// <summary>Units and unit prices on the wire: decimal strings with their 6 decimals, like amounts.</summary>
public static class Units6
{
    public static string? Of(decimal? x) => x?.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record HoldingView(
    Guid Id, string Name, string? Isin, string Kind, string? AssetClass, string? Issuer, int? RiskIndicator,
    LocalDate? OpenedOn, LocalDate? MaturesOn, decimal? EntryCostPct, LocalDate? ClosedOn, string? Note, uint Version, LineView? Line)
{
    public static HoldingView Of(StoredHolding s, LineView? line, Currency currency)
    {
        var h = s.Holding;
        return new(h.Id, h.Name, h.Isin, h.Kind, h.AssetClass, h.Issuer, h.RiskIndicator, h.OpenedOn, h.MaturesOn, h.EntryCostPct, h.ClosedOn, h.Note,
            s.Version, line);
    }
}

/// <summary>
/// Holdings spec §3: every holding of the envelope and its line in the latest snapshot on or before the date. <c>Unallocated</c> is the
/// envelope's valuation of the snapshot's date minus the total, null without one.
/// </summary>
public sealed record HoldingsView(
    ItemId EnvelopeId, string Currency, LocalDate? HoldingsAsOf, Guid? SnapshotId, bool? Complete, AmountView? Total, AmountView? Unallocated,
    IReadOnlyList<HoldingView> Holdings, IReadOnlyList<HoldingWarningView> Warnings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [EnvelopeId];
}

public sealed record SnapshotLineView(Guid HoldingId, string? Units, string? UnitPrice, AmountView Value, string? CostBasisPerUnit);

public sealed record SnapshotView(Guid Id, LocalDate AsOf, string? SourceLabel, bool Complete, AmountView Total, IReadOnlyList<SnapshotLineView> Lines);

public sealed record HoldingSnapshotList(ItemId EnvelopeId, IReadOnlyList<SnapshotView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [EnvelopeId];
}

public sealed record SnapshotRecorded(Guid Id, IReadOnlyList<HoldingWarningView> Warnings);
