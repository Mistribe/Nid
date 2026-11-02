using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;
using static Nid.Investments.Application.InvestmentsOverviewMath;

namespace Nid.Investments.Application;

/// <summary>Investments dashboard spec §5.2: the holdings of the household's envelopes together, in the Foyer / Moi scope.</summary>
public sealed record GetInvestmentsHoldingsQuery(VisibilityScope Scope) : IQuery<InvestmentsHoldings>;

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

/// <summary>
/// Issue #209: a holding with a report of <c>CostYear</c> (cash aside), or an envelope's together. Today's value; its total fees and its
/// reference (the envelope kind's contract fees plus the holding kind's support fees), in %, value-weighted for an envelope; a year of
/// fees at that rate; what the fees take from the value in 10 and 20 years at the assumed return; and over 20 years what they take
/// beyond the reference's (negative when cheaper). An amount is null when a rate is missing.
/// </summary>
public sealed record FeesCostView(
    Guid? HoldingId, string Name, AmountView? Value, decimal FeesPct, decimal ReferencePct, AmountView? Yearly, AmountView? Cost10,
    AmountView? Cost20, AmountView? Gap20);

public sealed record EnvelopeFeesCostView(ItemId EnvelopeId, FeesCostView Total, IReadOnlyList<FeesCostView> Holdings);

/// <summary>
/// Issue #209: what the fees cost over time, by envelope and together; <c>AboveReference20</c> adds up the holdings dearer than their
/// reference only. Indicative: the reference and the return are the household's choice.
/// </summary>
public sealed record FeesProjectionView(
    decimal AssumedReturnPct, IReadOnlyList<EnvelopeFeesCostView> Envelopes, AmountView? Yearly, AmountView? Cost10, AmountView? Cost20,
    AmountView? AboveReference20);

/// <summary>
/// Spec §5.2. D16: <c>Detailed + Unallocated + Undetailed = Value</c>, the overview's. D18: <c>Cost</c> is an estimate of
/// <c>CostYear</c>. Every amount in <c>Currency</c> at today's rate; null when a rate is missing. Holding prices spec §5.4: an envelope
/// counted at its estimate (<c>Estimated</c>, any of them) is detailed by its estimated lines; its fees and the share of units of
/// account (D19, against the floor) stay on its snapshot.
/// </summary>
public sealed record InvestmentsHoldings(
    string Currency, AmountView? Value, AmountView? Detailed, AmountView? Unallocated, AmountView? Undetailed, decimal? DetailedPct,
    int DetailedCount, int EnvelopeCount, IReadOnlyList<HoldingKindShareView> ByKind, decimal? UcSharePct,
    IReadOnlyList<EnvelopeUcShareView> EnvelopesUc, IReadOnlyList<HoldingOverviewRow> Holdings, IReadOnlyList<ConcentrationView> Concentrations,
    int CostYear, AmountView? Cost, decimal? CostPct, int WithoutReportCount, IReadOnlyList<UndetailedEnvelopeView> UndetailedEnvelopes,
    IReadOnlyList<EnvelopeWatchView> Watch, IReadOnlyList<OverviewFindingView> Findings,
    IReadOnlyList<EnvelopeFeesView> EnvelopeFees, FeesProjectionView? Projection, bool Estimated, FeeReferences References, HoldingsExposureView? Exposure = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Holdings.Select(h => h.EnvelopeId).Concat(UndetailedEnvelopes.Select(e => e.EnvelopeId)).Concat(EnvelopeFees.Select(e => e.EnvelopeId)).Distinct();
}

[RequestHandler<GetInvestmentsHoldingsQuery, InvestmentsHoldings>]
public sealed class GetInvestmentsHoldingsHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHoldingStore holdings,
    IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx, EnvelopeEstimator estimator, ISecurityBreakdownStore breakdowns)
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

        // Holding prices spec §5.4: estimated as the reader, over the envelopes already read.
        var estimates = await estimator.EstimateAsync(facts.Select(f => f.Item.Id).ToList(), ct);

        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = facts.Select(f => f.Currency).Where(c => c != reference).Distinct().ToList();
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], today, today, ct);
        var references = FeesProjection.Effective(await household.FeeReferencesAsync(ct));
        var overview = InvestmentsHoldingsMath.Build(facts, export, reports, reference, rates, today, references, estimates);
        var isins = overview.Holdings.Select(h => h.Isin).OfType<string>().Distinct().ToList();
        var latest = await breakdowns.LatestAsync(isins, ct);
        return Result.Success(overview with { Exposure = HoldingsExposureMath.Build(overview.Holdings, latest, reference.Code, today) });
    }
}

/// <summary>Issue #209: replaces the household's fee references; a kind left out keeps its default.</summary>
public sealed record SaveFeeReferencesCommand(FeeReferences References) : ICommand<FeeReferences>;

[RequestHandler<SaveFeeReferencesCommand, FeeReferences>]
public sealed class SaveFeeReferencesHandler(IHouseholdSettings household) : IRequestHandler<SaveFeeReferencesCommand, FeeReferences>
{
    public async ValueTask<Result<FeeReferences>> HandleAsync(SaveFeeReferencesCommand command, CancellationToken ct = default)
    {
        if (FeesProjection.Check(command.References) is { } failure)
            return Result.Failure<FeeReferences>(failure);
        var references = FeesProjection.Effective(command.References);
        await household.SaveFeeReferencesAsync(references, ct);
        return Result.Success(references);
    }
}

/// <summary>
/// Issues #103, #209: the defaults, in % — a gross return; the contract fees of an online life insurance or PER; the support fees of a
/// low-cost world ETF for a fund, an ETF, a structured product or another holding, none for a euro fund (its fees are the contract's),
/// a share or a bond.
/// </summary>
public static class FeesProjection
{
    public const decimal DefaultReturnPct = 4m;

    public static readonly FeeReferences Defaults = new(DefaultReturnPct,
        Enum.GetValues<EnvelopeKind>().Select(EnvelopeWire.Of)
            .ToDictionary(k => k, k => k is "life_insurance" or "per" ? 0.5m : 0m),
        HoldingWire.Kinds.Where(k => k != "cash")
            .ToDictionary(k => k, k => k is "fund" or "etf" or "structured" or "other" ? 0.2m : 0m));

    /// <summary>The defaults, overridden by what was saved: a kind saved before it existed (or left out) keeps its default.</summary>
    public static FeeReferences Effective(FeeReferences? saved)
    {
        if (saved is null) return Defaults;
        static Dictionary<string, decimal> Merge(IReadOnlyDictionary<string, decimal> defaults, IReadOnlyDictionary<string, decimal> chosen)
            => defaults.ToDictionary(d => d.Key, d => chosen.TryGetValue(d.Key, out var v) ? v : d.Value);
        return new FeeReferences(saved.AssumedReturnPct, Merge(Defaults.Envelopes, saved.Envelopes), Merge(Defaults.Holdings, saved.Holdings));
    }

    /// <summary>A return from −10 to 20 %, known kinds only, each fee from 0 to 10 %.</summary>
    public static Failure? Check(FeeReferences references)
    {
        if (references.AssumedReturnPct is < -10m or > 20m)
            return new Failure(InvestmentsCodes.InvalidFeeReferences, "The assumed return is from -10 to 20 %.", null);
        if (references.Envelopes.Keys.Any(k => !Defaults.Envelopes.ContainsKey(k)) || references.Holdings.Keys.Any(k => !Defaults.Holdings.ContainsKey(k)))
            return new Failure(InvestmentsCodes.InvalidFeeReferences, "A reference names a known envelope or holding kind (cash has no fees).", null);
        return references.Envelopes.Values.Concat(references.Holdings.Values).Any(v => v is < 0m or > 10m)
            ? new Failure(InvestmentsCodes.InvalidFeeReferences, "Each reference is from 0 to 10 %.", null)
            : null;
    }
}

/// <summary>Spec §5's figures, pure: tested in Nid.Investments.Tests.</summary>
internal static class InvestmentsHoldingsMath
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>The envelopes whose holdings count towards the share of units of account (D19).</summary>
    private static readonly HashSet<EnvelopeKind> UnitLinked = [EnvelopeKind.LifeInsurance, EnvelopeKind.Per];

    private static readonly int[] Horizons = [10, 20];

    /// <param name="estimates">The envelopes' estimates (holding prices spec §5.4); none, the valuations and snapshots.</param>
    public static InvestmentsHoldings Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, HoldingsExport export, IReadOnlyDictionary<ItemId, IReadOnlyList<CostReport>> reports,
        Currency reference, RateTable rates, LocalDate today, FeeReferences? references = null,
        IReadOnlyDictionary<ItemId, Prices.EnvelopeEstimate>? estimates = null)
    {
        references ??= FeesProjection.Defaults;
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
        var feeLines = new List<FeeLine>();
        var detailedCount = 0;
        var estimated = false;

        foreach (var f in facts.OrderBy(f => f.Item.DisplayName, French).ThenBy(f => f.Item.Id.Value))
        {
            var figures = EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            // D16: an envelope without a value is not in the overview's value, so not here either.
            if (figures.Value is null) continue;
            var (value, asOf, isEstimated, estimatedLines) = TodayValue.Pick(figures, estimates?.GetValueOrDefault(f.Item.Id));
            estimated |= isEstimated;
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
            // The statement's lines carry the fees and the share of units of account; today's, the estimated ones when the estimate is the value.
            var statement = snapshot.Lines.Where(l => l.Value != 0m).Select(l => (l.HoldingId, l.Value, Converted: At(l.Value, f.Currency))).ToList();
            var lines = estimatedLines is null ? statement
                : estimatedLines.Where(l => l.Value != 0m).Select(l => (l.HoldingId, l.Value, Converted: At(l.Value, f.Currency))).ToList();
            detailedParts.AddRange(lines.Select(l => l.Converted));
            unallocatedParts.Add(At(value.Amount - (estimatedLines?.Sum(l => l.Value) ?? HoldingMath.Total(snapshot)), f.Currency));

            foreach (var (holdingId, lineValue, converted) in lines)
            {
                var holding = holdingsOf.GetValueOrDefault(holdingId);
                var report = envelopeReports.FirstOrDefault(r => r.HoldingId == holdingId && r.Year == costYear);
                var implicitCost = report is null ? null : HoldingMath.ImplicitCost(report, snapshots, f.Currency);
                var costConverted = implicitCost is { } c ? At(c.Amount, f.Currency) : null;
                var figuresOf = HoldingMath.At(holdingId, snapshots, movements, f.Currency, today);
                rows.Add((new HoldingOverviewRow(f.Item.Id, f.Item.DisplayName, holdingId, holding?.Name ?? string.Empty, holding?.Isin,
                        holding?.Kind ?? "other", f.Currency.Code, isEstimated ? asOf : snapshot.AsOf, lineValue.ToString("0.00", CultureInfo.InvariantCulture),
                        convert.View(converted), null, figuresOf?.GainPct, figuresOf?.AnnualReturnPct, report?.TotalFeesPct,
                        convert.View(costConverted), holding?.MaturesOn),
                    converted, implicitCost is not null ? Base(report!, snapshots, f.Currency, At) : null, costConverted));
            }

            // Issue #103: cash has no fees to report, as D18.
            var feeable = statement.Where(l => holdingsOf.GetValueOrDefault(l.HoldingId)?.Kind != "cash").ToList();
            var reported = feeable
                .Select(l => (l.HoldingId, l.Value, l.Converted, Report: envelopeReports.FirstOrDefault(r => r.HoldingId == l.HoldingId && r.Year == costYear)))
                .Where(l => l.Report is not null).ToList();
            var contractReference = references.Envelopes.GetValueOrDefault(EnvelopeWire.Of(f.Envelope.Kind));
            foreach (var l in reported)
            {
                var holding = holdingsOf.GetValueOrDefault(l.HoldingId);
                feeLines.Add(new FeeLine(f.Item.Id, f.Item.DisplayName, l.HoldingId, holding?.Name ?? string.Empty, l.Value, l.Converted,
                    l.Report!.TotalFeesPct, contractReference + references.Holdings.GetValueOrDefault(holding?.Kind ?? "other")));
            }
            if (feeable.Count > 0)
            {
                var weight = reported.Sum(l => l.Value);
                decimal? Weighted(Func<CostReport, decimal?> pick)
                {
                    var known = reported.Where(l => pick(l.Report!) is not null).ToList();
                    var w = known.Sum(l => l.Value);
                    return w > 0m ? Math.Round(known.Sum(l => l.Value * pick(l.Report!)!.Value) / w, 2, MidpointRounding.AwayFromZero) : null;
                }
                var feeableValue = feeable.Sum(l => l.Value);
                envelopeFees.Add(new EnvelopeFeesView(f.Item.Id, f.Item.DisplayName, Weighted(r => r.ContractFeesPct), Weighted(r => r.SupportFeesPct),
                    Weighted(r => r.TotalFeesPct),
                    weight > 0m ? convert.View(At(reported.Sum(l => l.Value * l.Report!.TotalFeesPct / 100m), f.Currency)) : null,
                    feeableValue > 0m ? EnvelopeMath.RoundPercent(weight / feeableValue * 100m) : null));
            }

            if (UnitLinked.Contains(f.Envelope.Kind))
            {
                var kinds = export.Holdings.Where(h => h.Envelope == f.Item.Id).ToDictionary(h => h.Id, h => h.Kind);
                var share = HoldingMath.UcSharePct(snapshot, kinds);
                envelopesUc.Add(new EnvelopeUcShareView(f.Item.Id, share, f.Envelope.UcShareFloorPct));
                // On the statement, as each envelope's share and its floor: never a share above the floor beside its warning.
                unitLinkedLines.AddRange(statement.Select(l => l.Converted));
                ucLines.AddRange(statement.Where(l => kinds.TryGetValue(l.HoldingId, out var k) && HoldingWire.HasUnits(k)).Select(l => l.Converted));
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
            Projection(feeLines, references.AssumedReturnPct, convert), estimated, references);
    }

    /// <summary>Issue #209: a holding with a report of the cost year, its value in its envelope's currency and converted.</summary>
    internal sealed record FeeLine(
        ItemId EnvelopeId, string EnvelopeName, Guid HoldingId, string Name, decimal Value, decimal? Converted, decimal FeesPct, decimal ReferencePct);

    /// <summary>
    /// Issue #209: v × (1 + return)^n − v × (1 + return − fees)^n is what the fees take in n years; the same at the reference's fees,
    /// less, is the gap. Null without a reported holding. A power of a decimal has no exact form: computed in double, as the annual
    /// returns, each holding's figure rounded to the cent and the totals added up from them.
    /// </summary>
    internal static FeesProjectionView? Projection(IReadOnlyList<FeeLine> lines, decimal assumedReturnPct, Converter convert)
    {
        if (lines.Count == 0) return null;
        decimal Grown(decimal value, decimal feesPct, int years) => value * (decimal)Math.Pow(1 + (double)((assumedReturnPct - feesPct) / 100m), years);
        static decimal Cent(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        static decimal Weighted(IEnumerable<FeeLine> of, Func<FeeLine, decimal> pick)
        {
            var list = of.ToList();
            var weight = list.Sum(l => l.Value);
            return weight > 0m ? Math.Round(list.Sum(l => l.Value * pick(l)) / weight, 2, MidpointRounding.AwayFromZero) : 0m;
        }

        var costs = lines.Select(l => (Line: l, Amounts: l.Converted is { } v
            ? new decimal?[] { Cent(v * l.FeesPct / 100m), Cent(Grown(v, 0m, 10) - Grown(v, l.FeesPct, 10)),
                Cent(Grown(v, 0m, 20) - Grown(v, l.FeesPct, 20)), Cent(Grown(v, l.ReferencePct, 20) - Grown(v, l.FeesPct, 20)) }
            : [null, null, null, null])).ToList();
        FeesCostView View(Guid? id, string name, decimal? value, decimal fees, decimal reference, IReadOnlyList<decimal?[]> amounts)
            => new(id, name, convert.View(value), fees, reference,
                convert.View(SumOrNull(amounts.Select(a => a[0]))), convert.View(SumOrNull(amounts.Select(a => a[1]))),
                convert.View(SumOrNull(amounts.Select(a => a[2]))), convert.View(SumOrNull(amounts.Select(a => a[3]))));

        // The dearest against their reference first.
        var envelopes = costs.GroupBy(c => c.Line.EnvelopeId)
            .OrderByDescending(g => SumOrNull(g.Select(c => c.Amounts[3])) ?? decimal.MinValue).ThenBy(g => g.First().Line.EnvelopeName, French)
            .Select(g => new EnvelopeFeesCostView(g.Key,
                View(null, g.First().Line.EnvelopeName, SumOrNull(g.Select(c => c.Line.Converted)), Weighted(g.Select(c => c.Line), l => l.FeesPct),
                    Weighted(g.Select(c => c.Line), l => l.ReferencePct), g.Select(c => c.Amounts).ToList()),
                g.OrderByDescending(c => c.Amounts[3] ?? decimal.MinValue).ThenBy(c => c.Line.Name, French)
                    .Select(c => View(c.Line.HoldingId, c.Line.Name, c.Line.Converted, c.Line.FeesPct, c.Line.ReferencePct, [c.Amounts])).ToList()))
            .ToList();
        decimal? Total(int i) => SumOrNull(costs.Select(c => c.Amounts[i]));
        return new FeesProjectionView(assumedReturnPct, envelopes, convert.View(Total(0)), convert.View(Total(1)), convert.View(Total(2)),
            convert.View(SumOrNull(costs.Select(c => c.Amounts[3] is { } gap ? Math.Max(gap, 0m) : (decimal?)null))));
    }

    /// <summary>D18: the mean of the holding's values the estimate uses (end of the year before and of the year), converted.</summary>
    private static decimal? Base(CostReport report, IReadOnlyList<HoldingSnapshot> snapshots, Currency currency, Func<decimal, Currency, decimal?> at)
        => HoldingMath.ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year - 1, 12, 31)) is var (_, start)
           && HoldingMath.ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year, 12, 31)) is var (_, end)
            ? at((start + end) / 2m, currency) : null;

    private static decimal? Share(decimal? part, decimal? total)
        => part is { } p && total is { } t && t > 0m ? EnvelopeMath.RoundPercent(p / t * 100m) : null;
}
