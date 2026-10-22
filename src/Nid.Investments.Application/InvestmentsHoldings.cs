using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Investments.Application.InvestmentsOverviewMath;

namespace Nid.Investments.Application;

/// <summary>Investments dashboard spec §5.2: the holdings of the household's envelopes together, in the Foyer / Moi scope.</summary>
public sealed record GetInvestmentsHoldingsQuery(
    VisibilityScope Scope, decimal AssumedReturnPct = FeesProjection.DefaultReturnPct,
    decimal ReferenceFeesPct = FeesProjection.DefaultReferencePct) : IQuery<InvestmentsHoldings>;

/// <summary>A kind of holding (euro fund, ETF…) and its share of what is detailed, 0.1 %.</summary>
public sealed record HoldingKindShareView(string Kind, AmountView? Value, decimal? SharePct);

/// <summary>D19: an envelope's share of units of account in its latest snapshot, and its floor.</summary>
public sealed record EnvelopeUcShareView(ItemId EnvelopeId, decimal? UcSharePct, decimal? FloorPct);

/// <summary>
/// A holding of the latest snapshot of its envelope (D16): its value in its envelope's currency and converted, its weight in the
/// household's value, its figures (<see cref="HoldingMath.At"/>), and the total fees of its cost report of <c>CostYear</c> with the
/// estimated cost they make (D18).
/// </summary>
public sealed record HoldingOverviewRow(
    ItemId EnvelopeId, string EnvelopeName, Guid HoldingId, string Name, string? Isin, string Kind, string Currency, LocalDate AsOf,
    string Value, AmountView? ValueConverted, decimal? SharePct, decimal? GainPct, decimal? AnnualReturnPct,
    decimal? TotalFeesPct, AmountView? ImplicitCost, LocalDate? MaturesOn, decimal? CostSharePct = null);

/// <summary>D17: one ISIN held in several envelopes, its value and share of the household's.</summary>
public sealed record ConcentrationView(string Isin, string Name, AmountView? Value, decimal? SharePct, IReadOnlyList<ItemId> EnvelopeIds);

public sealed record UndetailedEnvelopeView(ItemId EnvelopeId, string Name, AmountView? Value);

/// <summary>
/// Issue #103: a detailed envelope's fees in % of <c>CostYear</c>'s reports, weighted by its latest snapshot's lines (cash aside);
/// <c>YearlyCost</c> is those rates on the reported lines' values, converted; <c>CoveredPct</c> the share of its lines with a report.
/// </summary>
public sealed record EnvelopeFeesView(
    ItemId EnvelopeId, string Name, decimal? ContractFeesPct, decimal? SupportFeesPct, decimal? TotalFeesPct, AmountView? YearlyCost, decimal? CoveredPct);

/// <summary>Issue #103: the reported holdings' value after <c>Years</c> at the assumed return less their fees, and less the reference's.</summary>
public sealed record FeesHorizonView(int Years, AmountView WithFees, AmountView WithReference, AmountView Difference);

/// <summary>Issue #103: what the household's fees cost over time, on today's value of the holdings with a report.</summary>
public sealed record FeesProjectionView(
    AmountView Base, decimal FeesPct, decimal AssumedReturnPct, decimal ReferenceFeesPct, IReadOnlyList<FeesHorizonView> Horizons);

/// <summary>
/// Spec §5.2. D16: <c>Detailed + Unallocated + Undetailed = Value</c>, the overview's. D18: <c>Cost</c> is an estimate of
/// <c>CostYear</c>. Every amount in <c>Currency</c> at today's rate; null when a rate is missing.
/// </summary>
public sealed record InvestmentsHoldings(
    string Currency, AmountView? Value, AmountView? Detailed, AmountView? Unallocated, AmountView? Undetailed, decimal? DetailedPct,
    int DetailedCount, int EnvelopeCount, IReadOnlyList<HoldingKindShareView> ByKind, decimal? UcSharePct,
    IReadOnlyList<EnvelopeUcShareView> EnvelopesUc, IReadOnlyList<HoldingOverviewRow> Holdings, IReadOnlyList<ConcentrationView> Concentrations,
    int CostYear, AmountView? Cost, decimal? CostPct, int WithoutReportCount, IReadOnlyList<UndetailedEnvelopeView> UndetailedEnvelopes,
    IReadOnlyList<EnvelopeWatchView> Watch, IReadOnlyList<OverviewFindingView> Findings,
    IReadOnlyList<EnvelopeFeesView> EnvelopeFees, FeesProjectionView? Projection) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Holdings.Select(h => h.EnvelopeId).Concat(UndetailedEnvelopes.Select(e => e.EnvelopeId)).Concat(EnvelopeFees.Select(e => e.EnvelopeId)).Distinct();
}

[RequestHandler<GetInvestmentsHoldingsQuery, InvestmentsHoldings>]
public sealed class GetInvestmentsHoldingsHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHoldingStore holdings,
    IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx)
    : IRequestHandler<GetInvestmentsHoldingsQuery, InvestmentsHoldings>
{
    public async ValueTask<Result<InvestmentsHoldings>> HandleAsync(GetInvestmentsHoldingsQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        // D9: the open envelopes only.
        var ids = await visible.ForAsync(reader, query.Scope, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        var block = await performance.FactsManyAsync(found, accesses, ct);
        var facts = new List<EnvelopePerformance.Facts>();
        foreach (var envelope in found)
        {
            EnvelopePerformance.Facts? f;
            if (block is not null) block.TryGetValue(envelope.ItemId, out f);
            else (await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out f);
            if (f is not null && f.Item.ArchivedAt is null) facts.Add(f);
        }

        // The holdings, snapshots and movements of every envelope in three queries; the cost reports by envelope, only for those detailed.
        var export = await holdings.ExportAsync(facts.Select(f => f.Item.Id).ToList(), ct);
        var reports = new Dictionary<ItemId, IReadOnlyList<CostReport>>();
        foreach (var envelope in export.Snapshots.Select(s => s.Envelope).Distinct())
            reports[envelope] = await holdings.CostReportsAsync(envelope, ct);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = facts.Select(f => f.Currency).Where(c => c != reference).Distinct().ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        return Result.Success(InvestmentsHoldingsMath.Build(facts, export, reports, reference, rates, today, query.AssumedReturnPct, query.ReferenceFeesPct));
    }
}

/// <summary>Issue #103: the projection's defaults — a gross return, and the fees of a low-cost world ETF — in %.</summary>
public static class FeesProjection
{
    public const decimal DefaultReturnPct = 4m;
    public const decimal DefaultReferencePct = 0.2m;
}

/// <summary>Spec §5's figures, pure: tested in Nid.Investments.Tests.</summary>
internal static class InvestmentsHoldingsMath
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>The envelopes whose holdings count towards the share of units of account (D19).</summary>
    private static readonly HashSet<EnvelopeKind> UnitLinked = [EnvelopeKind.LifeInsurance, EnvelopeKind.Per];

    private static readonly int[] Horizons = [10, 20];

    public static InvestmentsHoldings Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, HoldingsExport export, IReadOnlyDictionary<ItemId, IReadOnlyList<CostReport>> reports,
        Currency reference, RateTable rates, LocalDate today, decimal assumedReturnPct = FeesProjection.DefaultReturnPct, decimal referenceFeesPct = FeesProjection.DefaultReferencePct)
    {
        var convert = new Converter(reference, rates);
        decimal? At(decimal amount, Currency currency) => convert.Of(amount, currency, today);
        var costYear = today.Year - 1;
        var holdingsOf = export.Holdings.ToDictionary(h => h.Id);

        var detailedParts = new List<decimal?>();
        var unallocatedParts = new List<decimal?>();
        var undetailed = new List<UndetailedEnvelopeView>();
        var undetailedParts = new List<decimal?>();
        var values = new List<decimal?>();
        var rows = new List<(HoldingOverviewRow Row, decimal? Converted, decimal? Base, decimal? Cost)>();
        var envelopesUc = new List<EnvelopeUcShareView>();
        var ucLines = new List<decimal?>();
        var unitLinkedLines = new List<decimal?>();
        var watch = new List<EnvelopeWatchView>();
        var envelopeFees = new List<EnvelopeFeesView>();
        var reportedLines = new List<(decimal? Converted, decimal FeesPct)>();
        var detailedCount = 0;

        foreach (var f in facts.OrderBy(f => f.Item.DisplayName, French).ThenBy(f => f.Item.Id.Value))
        {
            var figures = EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            // D16: an envelope without a value is not in the overview's value, so not here either.
            if (figures.Value is not { } value) continue;
            var valueConverted = At(value.Amount, f.Currency);
            values.Add(valueConverted);

            var snapshots = export.Snapshots.Where(s => s.Envelope == f.Item.Id).OrderByDescending(s => s.AsOf).ToList();
            var snapshot = snapshots.FirstOrDefault(s => s.AsOf <= today);
            if (snapshot is null)
            {
                undetailed.Add(new UndetailedEnvelopeView(f.Item.Id, f.Item.DisplayName, convert.View(valueConverted)));
                undetailedParts.Add(valueConverted);
                continue;
            }

            detailedCount++;
            var movements = export.Movements.Where(m => m.Envelope == f.Item.Id).ToList();
            var envelopeReports = reports.GetValueOrDefault(f.Item.Id) ?? [];
            var lines = snapshot.Lines.Where(l => l.Value != 0m).Select(l => (Line: l, Converted: At(l.Value, f.Currency))).ToList();
            detailedParts.AddRange(lines.Select(l => l.Converted));
            unallocatedParts.Add(At(value.Amount - HoldingMath.Total(snapshot), f.Currency));

            foreach (var (line, converted) in lines)
            {
                var holding = holdingsOf.GetValueOrDefault(line.HoldingId);
                var report = envelopeReports.FirstOrDefault(r => r.HoldingId == line.HoldingId && r.Year == costYear);
                var implicitCost = report is null ? null : HoldingMath.ImplicitCost(report, snapshots, f.Currency);
                var costConverted = implicitCost is { } c ? At(c.Amount, f.Currency) : null;
                var figuresOf = HoldingMath.At(line.HoldingId, snapshots, movements, f.Currency, today);
                rows.Add((new HoldingOverviewRow(f.Item.Id, f.Item.DisplayName, line.HoldingId, holding?.Name ?? string.Empty, holding?.Isin,
                        holding?.Kind ?? "other", f.Currency.Code, snapshot.AsOf, line.Value.ToString("0.00", CultureInfo.InvariantCulture),
                        convert.View(converted), null, figuresOf?.GainPct, figuresOf?.AnnualReturnPct, report?.TotalFeesPct,
                        convert.View(costConverted), holding?.MaturesOn),
                    converted, implicitCost is not null ? Base(report!, snapshots, f.Currency, At) : null, costConverted));
            }

            // Issue #103: cash has no fees to report, as D18.
            var feeable = lines.Where(l => holdingsOf.GetValueOrDefault(l.Line.HoldingId)?.Kind != "cash").ToList();
            var reported = feeable
                .Select(l => (l.Line, l.Converted, Report: envelopeReports.FirstOrDefault(r => r.HoldingId == l.Line.HoldingId && r.Year == costYear)))
                .Where(l => l.Report is not null).ToList();
            reportedLines.AddRange(reported.Select(l => (l.Converted, l.Report!.TotalFeesPct)));
            if (feeable.Count > 0)
            {
                var weight = reported.Sum(l => l.Line.Value);
                decimal? Weighted(Func<CostReport, decimal?> pick)
                {
                    var known = reported.Where(l => pick(l.Report!) is not null).ToList();
                    var w = known.Sum(l => l.Line.Value);
                    return w > 0m ? Math.Round(known.Sum(l => l.Line.Value * pick(l.Report!)!.Value) / w, 2, MidpointRounding.AwayFromZero) : null;
                }
                var feeableValue = feeable.Sum(l => l.Line.Value);
                envelopeFees.Add(new EnvelopeFeesView(f.Item.Id, f.Item.DisplayName, Weighted(r => r.ContractFeesPct), Weighted(r => r.SupportFeesPct),
                    Weighted(r => r.TotalFeesPct),
                    weight > 0m ? convert.View(At(reported.Sum(l => l.Line.Value * l.Report!.TotalFeesPct / 100m), f.Currency)) : null,
                    feeableValue > 0m ? EnvelopeMath.RoundPercent(weight / feeableValue * 100m) : null));
            }

            if (UnitLinked.Contains(f.Envelope.Kind))
            {
                var kinds = export.Holdings.Where(h => h.Envelope == f.Item.Id).ToDictionary(h => h.Id, h => h.Kind);
                var share = HoldingMath.UcSharePct(snapshot, kinds);
                envelopesUc.Add(new EnvelopeUcShareView(f.Item.Id, share, f.Envelope.UcShareFloorPct));
                unitLinkedLines.AddRange(lines.Select(l => l.Converted));
                ucLines.AddRange(lines.Where(l => kinds.TryGetValue(l.Line.HoldingId, out var k) && HoldingWire.HasUnits(k)).Select(l => l.Converted));
                if (share < f.Envelope.UcShareFloorPct)
                    watch.Add(new EnvelopeWatchView(HoldingWarnings.UcShareBelowFloor, f.Item.Id, null, snapshot.AsOf));
            }

            // As the envelope's own tab, on its latest snapshot only: the warnings of older ones were read then.
            watch.AddRange(HoldingMath.UnexplainedUnits(snapshots.Where(s => s.AsOf <= today).ToList(), movements, f.Currency)
                .Where(w => w.On == snapshot.AsOf)
                .Select(w => new EnvelopeWatchView(HoldingWarnings.UnexplainedUnits, f.Item.Id, w.HoldingId, w.On)));
            watch.AddRange(HoldingMath.Warnings(snapshot, null).Where(w => w.Code == HoldingWarnings.ValueMismatch)
                .Select(w => new EnvelopeWatchView(HoldingWarnings.ValueMismatch, f.Item.Id, w.HoldingId, snapshot.AsOf)));
            watch.AddRange(export.Holdings.Where(h => h.Envelope == f.Item.Id && h.ClosedOn is null
                    && h.MaturesOn is { } on && on >= today && on <= today.PlusDays(MaturityDays))
                .Select(h => new EnvelopeWatchView(UpcomingKinds.HoldingMaturity, f.Item.Id, h.Id, h.MaturesOn)));
        }

        var total = SumOrNull(values);
        var detailed = SumOrNull(detailedParts);
        var costed = rows.Where(r => r.Base is not null).ToList();
        var cost = SumOrNull(costed.Select(r => r.Cost));
        var costBase = SumOrNull(costed.Select(r => r.Base));
        var holdingsRows = rows
            .OrderBy(r => r.Converted is null).ThenByDescending(r => r.Converted).ThenBy(r => r.Row.Name, French).ThenBy(r => r.Row.HoldingId)
            .Select(r => r.Row with { SharePct = Share(r.Converted, total), CostSharePct = r.Base is null ? null : Share(r.Cost, cost) })
            .ToList();
        var byKind = rows.GroupBy(r => r.Row.Kind)
            .Select(g => (Kind: g.Key, Value: SumOrNull(g.Select(r => r.Converted))))
            .OrderBy(k => k.Value is null).ThenByDescending(k => k.Value).ThenBy(k => k.Kind, StringComparer.Ordinal)
            .Select(k => new HoldingKindShareView(k.Kind, convert.View(k.Value), Share(k.Value, detailed)))
            .ToList();
        var concentrations = rows.Where(r => r.Row.Isin is not null).GroupBy(r => r.Row.Isin!)
            .Where(g => g.Select(r => r.Row.EnvelopeId).Distinct().Count() >= 2)
            .Select(g => (Isin: g.Key, Name: g.First().Row.Name, Value: SumOrNull(g.Select(r => r.Converted)),
                Envelopes: g.Select(r => r.Row.EnvelopeId).Distinct().ToList()))
            .OrderBy(c => c.Value is null).ThenByDescending(c => c.Value).ThenBy(c => c.Isin, StringComparer.Ordinal)
            .Select(c => new ConcentrationView(c.Isin, c.Name, convert.View(c.Value), Share(c.Value, total), c.Envelopes))
            .ToList();

        return new InvestmentsHoldings(reference.Code, convert.View(total), convert.View(detailed), convert.View(SumOrNull(unallocatedParts)),
            convert.View(SumOrNull(undetailedParts)), Share(detailed, total), detailedCount, values.Count, byKind,
            Share(ucLines.Count == 0 ? 0m : SumOrNull(ucLines), SumOrNull(unitLinkedLines)), envelopesUc,
            holdingsRows, concentrations, costYear, convert.View(cost), Share(cost, costBase),
            // D18: a holding of cash has no fees to report.
            rows.Count(r => r.Row.TotalFeesPct is null && r.Row.Kind != "cash"),
            undetailed,
            watch.OrderBy(w => w.On ?? LocalDate.MaxIsoValue).ThenBy(w => w.Code, StringComparer.Ordinal).ThenBy(w => w.EnvelopeId.Value).ToList(),
            convert.Missing,
            envelopeFees.OrderBy(e => e.TotalFeesPct is null).ThenByDescending(e => e.TotalFeesPct).ThenBy(e => e.Name, French).ToList(),
            Projection(reportedLines, assumedReturnPct, referenceFeesPct, convert));
    }

    /// <summary>
    /// Issue #103: base × (1 + return − fees)^years, against the same base at the reference's fees; null without a reported holding
    /// or with a missing rate. A power of a decimal has no exact form: computed in double, as the annual returns, then rounded.
    /// </summary>
    internal static FeesProjectionView? Projection(
        IReadOnlyList<(decimal? Converted, decimal FeesPct)> lines, decimal assumedReturnPct, decimal referenceFeesPct, Converter convert)
    {
        if (SumOrNull(lines.Select(l => l.Converted)) is not { } @base || @base <= 0m) return null;
        var fees = lines.Sum(l => l.Converted!.Value * l.FeesPct) / @base;
        decimal Grown(decimal feesPct, int years)
            => Math.Round(@base * (decimal)Math.Pow(1 + (double)((assumedReturnPct - feesPct) / 100m), years), 2, MidpointRounding.AwayFromZero);
        return new FeesProjectionView(convert.View(@base)!, Math.Round(fees, 2, MidpointRounding.AwayFromZero), assumedReturnPct, referenceFeesPct,
            Horizons.Select(n =>
            {
                var withFees = Grown(fees, n);
                var withReference = Grown(referenceFeesPct, n);
                return new FeesHorizonView(n, convert.View(withFees)!, convert.View(withReference)!, convert.View(withReference - withFees)!);
            }).ToList());
    }

    /// <summary>D18: the mean of the holding's values the estimate uses (end of the year before and of the year), converted.</summary>
    private static decimal? Base(CostReport report, IReadOnlyList<HoldingSnapshot> snapshots, Currency currency, Func<decimal, Currency, decimal?> at)
        => HoldingMath.ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year - 1, 12, 31)) is var (_, start)
           && HoldingMath.ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year, 12, 31)) is var (_, end)
            ? at((start + end) / 2m, currency) : null;

    private static decimal? Share(decimal? part, decimal? total)
        => part is { } p && total is { } t && t > 0m ? EnvelopeMath.RoundPercent(p / t * 100m) : null;
}
