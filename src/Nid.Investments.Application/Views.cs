using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
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
    decimal? UcShareFloorPct, AmountView? PremiumsBefore2017, FiguresView Figures, IReadOnlyList<ValuationView> Valuations, IReadOnlyList<FlowView> Flows) : IExposesItems
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

/// <summary><c>Units</c> and <c>ProposedFee</c> are only on <c>unexplained_units</c>: the fee is a proposal, never written.</summary>
public sealed record HoldingWarningView(string Code, Guid? HoldingId, LocalDate? On, string? Units, AmountView? ProposedFee)
{
    public static HoldingWarningView Of(HoldingWarning w)
        => new(w.Code, w.HoldingId, w.On, Units6.Of(w.Units), w.ProposedFee is { } fee ? AmountView.From(fee) : null);
}

/// <summary>
/// Issue #34 §2.5, as stated; <c>EstimatedImplicitCost</c> is an estimate (§3): total fees × the mean of the holding's values at the start and
/// the end of the year, null without both.
/// </summary>
public sealed record CostReportView(
    ItemId EnvelopeId, Guid HoldingId, int Year, decimal GrossReturnPct, decimal TotalFeesPct, decimal NetReturnPct, decimal? ContractFeesPct,
    decimal? SupportFeesPct, decimal? RetrocessionPct, decimal? NetAnnualized5yPct, string? SourceLabel, AmountView? EstimatedImplicitCost,
    IReadOnlyList<HoldingWarningView> Warnings) : IExposesItems
{
    public static CostReportView Of(ItemId envelope, CostReport r, Money? implicitCost) => new(envelope, r.HoldingId, r.Year, r.GrossReturnPct,
        r.TotalFeesPct, r.NetReturnPct, r.ContractFeesPct, r.SupportFeesPct, r.RetrocessionPct, r.NetAnnualized5yPct, r.SourceLabel,
        implicitCost is { } c ? AmountView.From(c) : null,
        HoldingMath.IsInconsistent(r) ? [new HoldingWarningView(HoldingWarnings.CostReportInconsistent, r.HoldingId, null, null, null)] : []);

    public IEnumerable<ItemId> ExposedItems() => [EnvelopeId];
}

public sealed record CostReportList(ItemId EnvelopeId, Guid HoldingId, IReadOnlyList<CostReportView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [EnvelopeId];
}

/// <summary>Issue #34 §3: a holding's figures at its latest value on or before the read's date (<see cref="AsOf"/>).</summary>
public sealed record HoldingFiguresView(
    LocalDate AsOf, AmountView Value, AmountView MoneyIn, AmountView MoneyOut, AmountView Gain, decimal? GainPct, decimal? AnnualReturnPct,
    AmountView ExplicitFees, AmountView GainBeforeFees)
{
    public static HoldingFiguresView? Of(HoldingFigures? f) => f is null ? null : new(f.AsOf, AmountView.From(f.Value), AmountView.From(f.MoneyIn),
        AmountView.From(f.MoneyOut), AmountView.From(f.Gain), f.GainPct, f.AnnualReturnPct, AmountView.From(f.ExplicitFees), AmountView.From(f.GainBeforeFees));
}

/// <summary>A holding's movement; a switch's leg also carries <see cref="SwitchLegs"/>, every leg of its operation (itself included, issue #45).</summary>
public sealed record MovementView(
    Guid Id, LocalDate OccurredOn, string Kind, string? Units, string? UnitPrice, AmountView Amount, Guid? EnvelopeFlowId, string? SwitchRef,
    string? Label, string? ExternalRef, IReadOnlyList<SwitchLegView>? SwitchLegs = null)
{
    public static MovementView Of(HoldingMovement m, Currency currency) => new(m.Id, m.OccurredOn, m.Kind, Units6.Of(m.Units), Units6.Of(m.UnitPrice),
        AmountView.From(Amounts.Of(m.Amount, currency)), m.EnvelopeFlowId, m.SwitchRef, m.Label, m.ExternalRef);

    public static MovementView Of(HoldingMovement m, Currency currency, IReadOnlyList<HoldingMovement> envelopeMovements, IReadOnlyList<StoredHolding> holdings)
        => Of(m, currency) with
        {
            SwitchLegs = m.SwitchRef is null ? null : envelopeMovements.Where(x => x.SwitchRef == m.SwitchRef)
                .OrderBy(x => MovementKinds.IsIn(x.Kind)).ThenBy(x => x.Id)
                .Select(x => new SwitchLegView(x.HoldingId, holdings.FirstOrDefault(h => h.Holding.Id == x.HoldingId)?.Holding.Name ?? "", x.Kind,
                    Units6.Of(x.Units), Units6.Of(x.UnitPrice), AmountView.From(Amounts.Of(x.Amount, currency))))
                .ToList(),
        };
}

/// <summary>One leg of a switch: the money leaving (<c>switch_out</c>, <c>maturity</c>) first, then the money arriving (<c>switch_in</c>).</summary>
public sealed record SwitchLegView(Guid HoldingId, string HoldingName, string Kind, string? Units, string? UnitPrice, AmountView Amount);

public sealed record HoldingMovementList(ItemId EnvelopeId, Guid HoldingId, IReadOnlyList<MovementView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [EnvelopeId];
}

/// <summary>One point per snapshot valuing the holding; <c>NetContributions</c> is its money in − out up to that date.</summary>
public sealed record HoldingSeries(ItemId EnvelopeId, Guid HoldingId, IReadOnlyList<SeriesPoint> Points) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [EnvelopeId];
}

public sealed record MovementRecorded(Guid Id, IReadOnlyList<HoldingWarningView> Warnings);

public sealed record SwitchRecorded(string SwitchRef, IReadOnlyList<Guid> FromMovementIds, IReadOnlyList<Guid> ToMovementIds, IReadOnlyList<HoldingWarningView> Warnings);

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
    LocalDate? OpenedOn, LocalDate? MaturesOn, decimal? EntryCostPct, LocalDate? ClosedOn, string? Note, uint Version, LineView? Line,
    HoldingFiguresView? Figures, bool AutoPrice, PriceView? Price)
{
    public static HoldingView Of(StoredHolding s, LineView? line, HoldingFigures? figures, PriceView? price)
    {
        var h = s.Holding;
        return new(h.Id, h.Name, h.Isin, h.Kind, h.AssetClass, h.Issuer, h.RiskIndicator, h.OpenedOn, h.MaturesOn, h.EntryCostPct, h.ClosedOn, h.Note,
            s.Version, line, HoldingFiguresView.Of(figures), h.AutoPrice, price);
    }
}

/// <summary>Holding prices spec §5.3: the latest price of a holding's ISIN, in the source's currency: the accepted one, or a newer refused one.</summary>
public sealed record PriceView(string Value, string Currency, LocalDate OnDate, string Source, string Status, string? Previous)
{
    public static PriceView? Of(LatestPrices? latest)
        => (latest?.Rejected ?? latest?.Accepted) is { } p
            ? new(Units6.Of(p.Price)!, p.Currency.Code, p.OnDate, p.Source, p.Status.ToString().ToLowerInvariant(), Units6.Of(p.Previous))
            : null;
}

public sealed record EstimateLineView(Guid HoldingId, string Reason);

/// <summary>Holding prices spec §5.3: the envelope's estimate today, or no <c>Value</c> and the <c>MissingReason</c>.</summary>
public sealed record EnvelopeEstimateView(
    AmountView? Value, LocalDate? AsOf, LocalDate? BasedOn, bool Converted, IReadOnlyList<EstimateLineView> Stale, string? MissingReason)
{
    public static EnvelopeEstimateView Of(Prices.EnvelopeEstimate e)
        => new(AmountView.From(e.Value), e.AsOf, e.BasedOn, e.Converted, e.Stale.Select(s => new EstimateLineView(s.HoldingId, s.Reason)).ToList(), e.MissingReason);
}

/// <summary>
/// Holdings spec §3: every holding of the envelope and its line in the latest snapshot on or before the date. <c>Unallocated</c> is the
/// envelope's valuation of the snapshot's date minus the total, null without one. <c>ExplicitFees</c> is Σ <c>fee</c> movements up to the
/// snapshot's date. <c>UcSharePct</c> is the snapshot's share of units of account (neither euro fund nor cash), 0.1 %.
/// <c>Estimate</c> is a figure of today: null when the date is another.
/// </summary>
public sealed record HoldingsView(
    ItemId EnvelopeId, string Currency, LocalDate? HoldingsAsOf, Guid? SnapshotId, bool? Complete, AmountView? Total, AmountView? Unallocated,
    AmountView? ExplicitFees, decimal? UcSharePct, decimal? UcShareFloorPct,
    IReadOnlyList<HoldingView> Holdings, IReadOnlyList<HoldingWarningView> Warnings, EnvelopeEstimateView? Estimate) : IExposesItems
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
