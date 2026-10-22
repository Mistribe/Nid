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
    decimal? TotalFeesPct, AmountView? ImplicitCost, LocalDate? MaturesOn);

/// <summary>D17: one ISIN held in several envelopes, its value and share of the household's.</summary>
public sealed record ConcentrationView(string Isin, string Name, AmountView? Value, decimal? SharePct, IReadOnlyList<ItemId> EnvelopeIds);

public sealed record UndetailedEnvelopeView(ItemId EnvelopeId, string Name, AmountView? Value);

/// <summary>
/// Spec §5.2. D16: <c>Detailed + Unallocated + Undetailed = Value</c>, the overview's. D18: <c>Cost</c> is an estimate of
/// <c>CostYear</c>. Every amount in <c>Currency</c> at today's rate; null when a rate is missing.
/// </summary>
public sealed record InvestmentsHoldings(
    string Currency, AmountView? Value, AmountView? Detailed, AmountView? Unallocated, AmountView? Undetailed, decimal? DetailedPct,
    int DetailedCount, int EnvelopeCount, IReadOnlyList<HoldingKindShareView> ByKind, decimal? UcSharePct,
    IReadOnlyList<EnvelopeUcShareView> EnvelopesUc, IReadOnlyList<HoldingOverviewRow> Holdings, IReadOnlyList<ConcentrationView> Concentrations,
    int CostYear, AmountView? Cost, decimal? CostPct, int WithoutReportCount, IReadOnlyList<UndetailedEnvelopeView> UndetailedEnvelopes,
    IReadOnlyList<EnvelopeWatchView> Watch, IReadOnlyList<OverviewFindingView> Findings) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems()
        => Holdings.Select(h => h.EnvelopeId).Concat(UndetailedEnvelopes.Select(e => e.EnvelopeId)).Distinct();
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
        return Result.Success(InvestmentsHoldingsMath.Build(facts, export, reports, reference, rates, today));
    }
}

/// <summary>Spec §5's figures, pure: tested in Nid.Investments.Tests.</summary>
internal static class InvestmentsHoldingsMath
{
    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>The envelopes whose holdings count towards the share of units of account (D19).</summary>
    private static readonly HashSet<EnvelopeKind> UnitLinked = [EnvelopeKind.LifeInsurance, EnvelopeKind.Per];

    public static InvestmentsHoldings Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, HoldingsExport export, IReadOnlyDictionary<ItemId, IReadOnlyList<CostReport>> reports,
        Currency reference, RateTable rates, LocalDate today)
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
        var holdingsRows = rows
            .OrderBy(r => r.Converted is null).ThenByDescending(r => r.Converted).ThenBy(r => r.Row.Name, French).ThenBy(r => r.Row.HoldingId)
            .Select(r => r.Row with { SharePct = Share(r.Converted, total) })
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
        var costed = rows.Where(r => r.Base is not null).ToList();
        var cost = SumOrNull(costed.Select(r => r.Cost));
        var costBase = SumOrNull(costed.Select(r => r.Base));

        return new InvestmentsHoldings(reference.Code, convert.View(total), convert.View(detailed), convert.View(SumOrNull(unallocatedParts)),
            convert.View(SumOrNull(undetailedParts)), Share(detailed, total), detailedCount, values.Count, byKind,
            Share(ucLines.Count == 0 ? 0m : SumOrNull(ucLines), SumOrNull(unitLinkedLines)), envelopesUc,
            holdingsRows, concentrations, costYear, convert.View(cost), Share(cost, costBase),
            // D18: a holding of cash has no fees to report.
            rows.Count(r => r.Row.TotalFeesPct is null && r.Row.Kind != "cash"),
            undetailed,
            watch.OrderBy(w => w.On ?? LocalDate.MaxIsoValue).ThenBy(w => w.Code, StringComparer.Ordinal).ThenBy(w => w.EnvelopeId.Value).ToList(),
            convert.Missing);
    }

    /// <summary>D18: the mean of the holding's values the estimate uses (end of the year before and of the year), converted.</summary>
    private static decimal? Base(CostReport report, IReadOnlyList<HoldingSnapshot> snapshots, Currency currency, Func<decimal, Currency, decimal?> at)
        => HoldingMath.ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year - 1, 12, 31)) is var (_, start)
           && HoldingMath.ValueAt(report.HoldingId, snapshots, new LocalDate(report.Year, 12, 31)) is var (_, end)
            ? at((start + end) / 2m, currency) : null;

    private static decimal? Share(decimal? part, decimal? total)
        => part is { } p && total is { } t && t > 0m ? EnvelopeMath.RoundPercent(p / t * 100m) : null;
}
