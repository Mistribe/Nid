using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Investments.Application;

public enum OverviewSpan { TwelveMonths, ThreeYears, All }

/// <summary>Investments dashboard spec §3.2: the household's envelopes at a glance, in the Foyer / Moi scope (D8).</summary>
public sealed record GetInvestmentsOverviewQuery(VisibilityScope Scope, OverviewSpan Span) : IQuery<InvestmentsOverview>;

/// <summary>A figure the overview could not give (<c>core.fx_rate_missing</c>), with what it lacked.</summary>
public sealed record OverviewFindingView(string Code, string Message, string? Reference);

/// <summary>A kind of envelope (D10) and its share of the value, 0.1 %.</summary>
public sealed record KindShareView(string Kind, AmountView? Value, decimal? SharePct);

/// <summary>D7: a person's part of each envelope by its shares; the parts of an envelope add up to it to the cent.</summary>
public sealed record PersonEnvelopeShareView(Guid PersonId, AmountView? Value, AmountView? Gain, decimal? SharePct);

/// <summary>
/// An open envelope: its figures in its currency, as the list's, and its value and gain converted (D4). Holding prices spec §5.4: when
/// <c>Estimated</c>, the value (in the figures and converted) is the estimate of <c>EstimatedOn</c>; the rest of the figures stay on the valuation.
/// </summary>
public sealed record EnvelopeOverviewRow(
    ItemId Id, string Name, string Kind, string? Institution, IReadOnlyList<ItemOwnerView> Owners, string Currency, FiguresView Figures,
    AmountView? ValueConverted, AmountView? GainConverted, bool Estimated, LocalDate? EstimatedOn);

public sealed record KindValueView(string Kind, AmountView? Value);

/// <summary>One point of the curve: the envelopes' last readings on or before it, by kind, converted at its date (D4).</summary>
public sealed record OverviewPointView(LocalDate On, AmountView? Value, AmountView? NetContributions, IReadOnlyList<KindValueView> ByKind);

/// <summary>Spec §3.2: something to look at; the screen writes the sentence from the code.</summary>
public sealed record EnvelopeWatchView(string Code, ItemId EnvelopeId, Guid? HoldingId, LocalDate? On);

/// <summary>
/// Spec §3.2. Every household figure is in <c>Currency</c>, null when a rate is missing (then a finding names it). Today's figures
/// count the open envelopes; the points count a closed one before its closing (D9). <c>FirstAsOf</c> is the oldest value of any
/// visible envelope, where « Tout » of the performance starts (spec §4.2). Holding prices spec §5.4: today's value, its shares and the
/// last point count an envelope at its estimate when newer than its valuation (<c>EstimatedCount</c> of them); the gain, what was paid
/// in and the returns stay on the valuations.
/// </summary>
public sealed record InvestmentsOverview(
    string Currency, int OpenCount, int ClosedCount,
    AmountView? Value, LocalDate? OldestAsOf, LocalDate? FirstAsOf, int StaleCount, int WithoutValueCount,
    AmountView? NetContributions, AmountView? PaidIn, AmountView? Gain, decimal? GainPct, decimal? AnnualReturnPct,
    AmountView? SinceJanuary, decimal? SinceJanuaryPct, int WithoutStartCount,
    IReadOnlyList<KindShareView> ByKind, IReadOnlyList<PersonEnvelopeShareView> ByPerson, IReadOnlyList<EnvelopeOverviewRow> Envelopes,
    IReadOnlyList<OverviewPointView> Points, IReadOnlyList<EnvelopeWatchView> Watch, IReadOnlyList<OverviewFindingView> Findings,
    int EstimatedCount) : IExposesItems
{
    // The watch list names open envelopes only, all in the rows.
    public IEnumerable<ItemId> ExposedItems() => Envelopes.Select(e => e.Id);
}

public static class OverviewCodes
{
    public const string StaleValue = "stale_value";
    public const string NoValue = "no_value";
    public const string MissingOpening = "missing_opening";
}

[RequestHandler<GetInvestmentsOverviewQuery, InvestmentsOverview>]
public sealed class GetInvestmentsOverviewHandler(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHoldingStore holdings,
    IHouseholdCalendar calendar, IActorAccessor actor, IHouseholdSettings household, IFxRates fx, IPersonRegistry persons,
    EnvelopeEstimator estimator)
    : IRequestHandler<GetInvestmentsOverviewQuery, InvestmentsOverview>
{
    public async ValueTask<Result<InvestmentsOverview>> HandleAsync(GetInvestmentsOverviewQuery query, CancellationToken ct = default)
    {
        var reader = actor.Current!.PersonId!.Value;
        var today = await calendar.TodayAsync(ct);
        // D9: the closed envelopes too, for the curve before their closing.
        var ids = await visible.ForAsync(reader, query.Scope, includeArchived: true, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        // Batched reads spec §3, as the list: a block that cannot be read falls back to each envelope's own read.
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        var block = await performance.FactsManyAsync(found, accesses, ct);
        var facts = new List<EnvelopePerformance.Facts>();
        foreach (var envelope in found)
        {
            EnvelopePerformance.Facts? f;
            if (block is not null) block.TryGetValue(envelope.ItemId, out f);
            else (await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out f);
            if (f is not null) facts.Add(f);
        }

        var open = facts.Where(f => f.Item.ArchivedAt is null).ToList();
        // Holding prices spec §5.4: estimated as the reader, over the open envelopes already read.
        var estimates = await estimator.EstimateAsync(open.Select(f => f.Item.Id).ToList(), ct);
        var watch = new List<EnvelopeWatchView>();
        foreach (var m in await holdings.MaturingAsync(open.Select(f => f.Item.Id).ToList(), today, today.PlusDays(InvestmentsOverviewMath.MaturityDays), ct))
            watch.Add(new EnvelopeWatchView(UpcomingKinds.HoldingMaturity, m.Envelope, m.HoldingId, m.MaturesOn));
        // As the routine: the latest snapshot's share of units of account under the envelope's floor.
        foreach (var f in open.Where(f => f.Envelope.UcShareFloorPct is not null))
            if ((await holdings.SnapshotsAsync(f.Item.Id, ct)).FirstOrDefault(s => s.AsOf <= today) is { } snapshot
                && HoldingMath.UcSharePct(snapshot, (await holdings.HoldingsAsync(f.Item.Id, ct)).ToDictionary(h => h.Holding.Id, h => h.Holding.Kind)) < f.Envelope.UcShareFloorPct)
                watch.Add(new EnvelopeWatchView(HoldingWarnings.UcShareBelowFloor, f.Item.Id, null, snapshot.AsOf));

        var grid = InvestmentsOverviewMath.Grid(query.Span, today);
        var reference = await household.ReferenceCurrencyAsync(ct);
        var foreign = facts.Select(f => f.Currency).Where(c => c != reference).Distinct().ToList();
        // The reference's own quote is needed too: rates are per USD. The loader reaches back MaxGapDays before the first date.
        var rates = foreign.Count == 0 ? RateTable.Empty(today, 0) : await fx.LoadAsync([.. foreign, reference], grid[0], today, ct);
        return Result.Success(InvestmentsOverviewMath.Build(facts, reference, rates, today, grid, query.Span, watch, await persons.ListAsync(ct), estimates));
    }
}

/// <summary>Spec §3.2's figures, pure: tested in Nid.Investments.Tests.</summary>
internal static class InvestmentsOverviewMath
{
    /// <summary>As « À venir »: a letter must reach the insurer to choose where the proceeds go.</summary>
    public const int MaturityDays = 60;

    private const int MaxYears = 20;

    private static readonly StringComparer French = StringComparer.Create(new CultureInfo("fr-FR"), true);

    /// <summary>As the net worth curve (2D2b): the last day of each of the N months before today's, then today.</summary>
    public static IReadOnlyList<LocalDate> Grid(OverviewSpan span, LocalDate today)
    {
        var months = span switch { OverviewSpan.TwelveMonths => 12, OverviewSpan.ThreeYears => 36, _ => MaxYears * 12 };
        var firstOfMonth = new LocalDate(today.Year, today.Month, 1);
        var dates = new List<LocalDate>();
        for (var i = months; i >= 1; i--)
            dates.Add(firstOfMonth.PlusMonths(1 - i).PlusDays(-1));
        dates.Add(today);
        return dates;
    }

    /// <summary>Each amount converted alone at its date and rounded to the cent, so a total is the sum of the lines shown (D3, D4).</summary>
    internal sealed class Converter(Currency reference, RateTable rates)
    {
        private readonly SortedDictionary<string, OverviewFindingView> missing = new(StringComparer.Ordinal);

        public IReadOnlyList<OverviewFindingView> Missing => missing.Values.ToList();

        public decimal? Of(decimal amount, Currency from, LocalDate on)
        {
            if (from == reference || amount == 0m) return Math.Round(amount, 2, MidpointRounding.AwayFromZero); // zero converts to zero at any rate
            if (rates.TryConvert(amount, from, reference, on, projected: false, out var converted, out var miss))
                return Math.Round(converted, 2, MidpointRounding.AwayFromZero);
            // As the net worth curve: the reference names the currency that could not convert, even when the miss is the reference quote.
            var key = $"{from.Code}:{LocalDatePattern.Iso.Format(on)}";
            missing.TryAdd(key, new OverviewFindingView(FxCodes.RateMissing,
                $"No {miss.Currency.Code} rate within {rates.MaxGapDays} days before {LocalDatePattern.Iso.Format(miss.Date)}.", key));
            return null;
        }

        public AmountView? View(decimal? amount) => amount is { } a ? new AmountView(Text(a), reference.Code) : null;
    }

    /// <summary>An open envelope with a value today, read once; <c>Estimate</c> and its date when <see cref="TodayValue"/> picks it.</summary>
    internal sealed record Valued(EnvelopePerformance.Facts Facts, EnvelopeFigures Figures, decimal? Value, decimal? Gain, Money? Estimate = null, LocalDate? EstimatedOn = null);

    /// <param name="facts">The visible envelopes, closed ones included (D9).</param>
    /// <param name="holdingWatch">The holdings' warnings (maturities, units of account under the floor), read by the handler.</param>
    /// <param name="persons">The household in creation order, for the order of <c>ByPerson</c>.</param>
    /// <param name="estimates">The open envelopes' estimates (holding prices spec §5.4); none, the valuations.</param>
    public static InvestmentsOverview Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, Currency reference, RateTable rates, LocalDate today, IReadOnlyList<LocalDate> grid,
        OverviewSpan span, IReadOnlyList<EnvelopeWatchView> holdingWatch, IReadOnlyList<PersonInfo> persons,
        IReadOnlyDictionary<ItemId, Prices.EnvelopeEstimate>? estimates = null)
    {
        var convert = new Converter(reference, rates);
        var open = facts.Where(f => f.Item.ArchivedAt is null).ToList();
        var figures = open.Select(f => (Facts: f, Figures: EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today)))
            .ToList();
        var valued = figures.Where(x => x.Figures.Value is not null)
            .Select(x => (x.Facts, x.Figures, Today: TodayValue.Pick(x.Figures, estimates?.GetValueOrDefault(x.Facts.Item.Id))))
            .Select(x => new Valued(x.Facts, x.Figures, convert.Of(x.Today.Value.Amount, x.Facts.Currency, today),
                convert.Of(x.Figures.Gain!.Value.Amount, x.Facts.Currency, today),
                x.Today.Estimated ? x.Today.Value : null, x.Today.Estimated ? x.Today.AsOf : null))
            .ToList();

        var value = SumOrNull(valued.Select(v => v.Value));
        var net = SumOrNull(valued.Select(v => convert.Of(v.Figures.NetContributions!.Value.Amount, v.Facts.Currency, today)));
        // D5: a transfer between two envelopes is no money the household paid in.
        var paidIn = SumOrNull(valued.Select(v => convert.Of(v.Facts.Flows
            .Where(f => f.OccurredOn <= v.Figures.AsOf && f.Kind is FlowKind.Opening or FlowKind.Contribution && !IsTransfer(f))
            .Sum(f => f.Amount.Amount), v.Facts.Currency, today)));
        var gain = SumOrNull(valued.Select(v => v.Gain));

        // D3: the dashboard's « since 1 January ».
        var since = valued.Select(v => (v.Facts, Period: EnvelopesNetWorth.SinceJanuary(v.Facts, today))).Where(x => x.Period is not null).ToList();
        var sinceGain = SumOrNull(since.Select(x => convert.Of(x.Period!.Performance!.Value.Amount, x.Facts.Currency, today)));
        var sinceBase = SumOrNull(since.Select(x => convert.Of(x.Period!.StartValue!.Value.Amount + x.Period.NetContributions!.Value.Amount, x.Facts.Currency, today)));

        var rows = valued.Select(v => (Facts: v.Facts, v.Figures, v.Value, v.Gain, v.Estimate, v.EstimatedOn))
            .Concat(figures.Where(x => x.Figures.Value is null)
                .Select(x => (x.Facts, x.Figures, Value: (decimal?)null, Gain: (decimal?)null, Estimate: (Money?)null, EstimatedOn: (LocalDate?)null)))
            .OrderBy(x => x.Figures.Value is null ? 2 : x.Value is null ? 1 : 0).ThenByDescending(x => x.Value)
            .ThenBy(x => x.Facts.Item.DisplayName, French).ThenBy(x => x.Facts.Item.Id.Value)
            .Select(x => new EnvelopeOverviewRow(x.Facts.Item.Id, x.Facts.Item.DisplayName, EnvelopeWire.Of(x.Facts.Envelope.Kind),
                x.Facts.Envelope.Institution, x.Facts.Item.OwnerViews(), x.Facts.Currency.Code,
                x.Estimate is { } estimate ? FiguresView.From(x.Figures) with { Value = AmountView.From(estimate) } : FiguresView.From(x.Figures),
                convert.View(x.Value), convert.View(x.Gain), x.Estimate is not null, x.EstimatedOn))
            .ToList();

        var byKind = valued.GroupBy(v => EnvelopeWire.Of(v.Facts.Envelope.Kind))
            .Select(g => (Kind: g.Key, Value: SumOrNull(g.Select(v => v.Value))))
            .OrderBy(k => k.Value is null).ThenByDescending(k => k.Value).ThenBy(k => k.Kind, StringComparer.Ordinal)
            .Select(k => new KindShareView(k.Kind, convert.View(k.Value), Share(k.Value, value)))
            .ToList();

        var watch = figures
            .SelectMany(x => EnvelopeWatch(x.Facts.Item.Id, x.Figures))
            .Concat(holdingWatch)
            .OrderBy(w => w.On ?? LocalDate.MaxIsoValue).ThenBy(w => w.Code, StringComparer.Ordinal).ThenBy(w => w.EnvelopeId.Value)
            .ToList();

        return new InvestmentsOverview(reference.Code, open.Count, facts.Count - open.Count,
            convert.View(value), valued.Select(v => v.EstimatedOn ?? v.Figures.AsOf).Min(), facts.SelectMany(f => f.Values).Select(v => (LocalDate?)v.AsOf).Min(),
            valued.Count(v => v.Figures.StaleDays is not null),
            figures.Count - valued.Count,
            convert.View(net), convert.View(paidIn), convert.View(gain),
            gain is { } g && paidIn is { } p && p > 0m ? EnvelopeMath.RoundPercent(g / p * 100m) : null,
            AnnualReturn(valued, convert, today),
            convert.View(sinceGain), sinceGain is { } sg && sinceBase is { } sb && sb > 0m ? EnvelopeMath.RoundPercent(sg / sb * 100m) : null,
            valued.Count - since.Count,
            byKind, People(valued, persons, value, convert), rows,
            Points(facts, grid, span, convert, today, valued.Where(v => v.Estimate is not null).ToDictionary(v => v.Facts.Item.Id, v => v.Estimate!.Value)),
            watch, convert.Missing, valued.Count(v => v.Estimate is not null));
    }

    /// <summary>
    /// D6: the XIRR of every valued envelope's cash flows to its own date <c>v</c>, its value at <c>v</c> as the final flow, each
    /// converted at today's rate; a transfer's two legs cancel. Null when not meaningful, or when a rate is missing. On the valuation,
    /// never the estimate (holding prices spec §5.4).
    /// </summary>
    internal static decimal? AnnualReturn(IReadOnlyList<Valued> valued, Converter convert, LocalDate today)
    {
        if (valued.Count == 0) return null;
        var cash = new List<(LocalDate On, decimal Amount)>();
        foreach (var v in valued)
        {
            var asOf = v.Figures.AsOf!.Value;
            foreach (var f in v.Facts.Flows.Where(f => f.OccurredOn <= asOf && f.Kind is FlowKind.Opening or FlowKind.Contribution or FlowKind.Withdrawal))
            {
                if (convert.Of(f.Kind == FlowKind.Withdrawal ? f.Amount.Amount : -f.Amount.Amount, v.Facts.Currency, today) is not { } amount) return null;
                cash.Add((f.OccurredOn, amount));
            }
            if (convert.Of(v.Figures.Value!.Value.Amount, v.Facts.Currency, today) is not { } final) return null;
            cash.Add((asOf, final));
        }
        return Xirr.AnnualPercent(cash, valued.Max(v => v.Figures.AsOf!.Value));
    }

    /// <summary>Spec §3.2: no value, an old value, or a value without what was paid in before the tracking began.</summary>
    private static IEnumerable<EnvelopeWatchView> EnvelopeWatch(ItemId id, EnvelopeFigures figures)
    {
        if (figures.Value is null)
        {
            yield return new EnvelopeWatchView(OverviewCodes.NoValue, id, null, null);
            yield break;
        }
        if (figures.StaleDays is not null) yield return new EnvelopeWatchView(OverviewCodes.StaleValue, id, null, figures.AsOf);
        if (figures.MissingOpening) yield return new EnvelopeWatchView(OverviewCodes.MissingOpening, id, null, figures.AsOf);
    }

    /// <summary>
    /// D7: each person's part of each envelope by its shares, as the dashboard splits the net worth; an envelope's last owner takes
    /// what is left, so the parts add up to it. Adults first, then dependents, each in the household's order.
    /// </summary>
    private static List<PersonEnvelopeShareView> People(IReadOnlyList<Valued> valued, IReadOnlyList<PersonInfo> persons, decimal? total, Converter convert)
    {
        var parts = new Dictionary<PersonId, (decimal? Value, decimal? Gain)>();
        foreach (var v in valued)
        {
            var owners = v.Facts.Item.Owners;
            var values = Split(v.Value, owners);
            var gains = Split(v.Gain, owners);
            for (var k = 0; k < owners.Count; k++)
            {
                var (value, gain) = parts.GetValueOrDefault(owners[k].PersonId, (0m, 0m));
                parts[owners[k].PersonId] = (value + values[k], gain + gains[k]);
            }
        }
        var rank = persons.OrderBy(p => p.Kind == PersonKind.Adult ? 0 : 1).Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return parts.OrderBy(p => rank.GetValueOrDefault(p.Key, int.MaxValue)).ThenBy(p => p.Key.Value)
            .Select(p => new PersonEnvelopeShareView(p.Key.Value, convert.View(p.Value.Value), convert.View(p.Value.Gain), Share(p.Value.Value, total)))
            .ToList();
    }

    /// <summary>An amount by shares to the cent, the last owner taking the rest.</summary>
    private static List<decimal?> Split(decimal? amount, IReadOnlyList<Owner> owners)
    {
        if (amount is not { } total) return owners.Select(_ => (decimal?)null).ToList();
        var parts = new List<decimal?>();
        var given = 0m;
        for (var k = 0; k < owners.Count; k++)
        {
            var part = k == owners.Count - 1 ? total - given : Math.Round(total * owners[k].ShareBp / 10_000m, 2, MidpointRounding.AwayFromZero);
            given += part;
            parts.Add(part);
        }
        return parts;
    }

    /// <summary>
    /// As the net worth curve (2D2b): at each date, each envelope's last reading on or before it, with its net contributions up to
    /// that reading; a closed envelope counts strictly before its closing. « Tout » starts at the first date with a reading.
    /// Holding prices spec §5.4: at today's date only, an envelope's value is its estimate when <see cref="TodayValue"/> picks it.
    /// </summary>
    private static List<OverviewPointView> Points(
        IReadOnlyList<EnvelopePerformance.Facts> facts, IReadOnlyList<LocalDate> grid, OverviewSpan span, Converter convert,
        LocalDate today, IReadOnlyDictionary<ItemId, Money> estimates)
    {
        var series = facts.Select(f => (Facts: f, Readings: EnvelopeMath.Series(f.Values, f.Flows, f.Currency))).ToList();
        var points = new List<OverviewPointView>();
        foreach (var date in grid)
        {
            var at = series
                .Where(s => s.Facts.Envelope.ClosedOn is not { } closed || date < closed)
                .Select(s => (s.Facts, Reading: s.Readings.LastOrDefault(r => r.AsOf <= date)))
                .Where(x => x.Reading is not null)
                .Select(x => (Kind: EnvelopeWire.Of(x.Facts.Envelope.Kind),
                    Value: convert.Of(date == today && estimates.TryGetValue(x.Facts.Item.Id, out var e) ? e.Amount : x.Reading!.Value.Amount, x.Facts.Currency, date),
                    Net: convert.Of(x.Reading!.NetContributions.Amount, x.Facts.Currency, date)))
                .ToList();
            if (at.Count == 0)
            {
                points.Add(new OverviewPointView(date, null, null, []));
                continue;
            }
            var byKind = at.GroupBy(x => x.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new KindValueView(g.Key, convert.View(SumOrNull(g.Select(x => x.Value))))).ToList();
            points.Add(new OverviewPointView(date, convert.View(SumOrNull(at.Select(x => x.Value))), convert.View(SumOrNull(at.Select(x => x.Net))), byKind));
        }
        return span == OverviewSpan.All ? points.SkipWhile(p => p.ByKind.Count == 0).ToList() : points;
    }

    internal static bool IsTransfer(Flow f) => f.Category == "transfer";

    private static decimal? Share(decimal? part, decimal? total)
        => part is { } p && total is { } t && t > 0m ? EnvelopeMath.RoundPercent(p / t * 100m) : null;

    /// <summary>Null with nothing to sum (no figure is not a figure of 0), or when one amount is unknown.</summary>
    internal static decimal? SumOrNull(IEnumerable<decimal?> amounts)
    {
        decimal? total = null;
        foreach (var a in amounts)
        {
            if (a is not { } value) return null;
            total = (total ?? 0m) + value;
        }
        return total;
    }

    private static string Text(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);
}
