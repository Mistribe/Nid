using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Application.Prices;

/// <summary>
/// Holding prices spec §5.3: an envelope's value today in its currency, or no value and the <see cref="MissingReason"/>.
/// <see cref="Lines"/>: each holding counted and its value, in the envelope's currency (see <see cref="EnvelopeEstimateResult"/>).
/// </summary>
public sealed record EnvelopeEstimate(
    ItemId Envelope, Money? Value, LocalDate? AsOf, LocalDate? BasedOn, bool Converted, IReadOnlyList<EstimateLine> Stale, string? MissingReason,
    IReadOnlyList<EstimatedLine> Lines);

/// <summary>
/// Spec §5.4: the one source of the envelopes' estimates (holdings view, net worth, Placements). Loads what
/// <see cref="Domain.EnvelopeEstimate.Of"/> needs in a fixed number of queries. It needs a person actor who may read every envelope
/// passed: the valuations read is checked as a block, and when it refuses (no person actor, one envelope the person cannot read) every
/// envelope is <c>unreadable</c>, as is one without a currency. Every envelope passed is a key of the result.
/// </summary>
public sealed class EnvelopeEstimator(
    IHouseholdSettings settings, IHouseholdCalendar calendar, IItemRegistry items, IHoldingStore holdings, IValuations valuations,
    ISecurityPriceStore prices, IFxRates fx)
{
    public async Task<IReadOnlyDictionary<ItemId, EnvelopeEstimate>> EstimateAsync(IReadOnlyCollection<ItemId> envelopes, CancellationToken ct)
    {
        if (!(await settings.ReadAsync(ct)).PricesEnabled)
            return envelopes.Distinct().ToDictionary(e => e, e => Missing(e, EstimateReasons.Disabled));

        var today = await calendar.TodayAsync(ct);
        var currencies = new Dictionary<ItemId, Currency>();
        foreach (var (id, item) in await items.FindManyAsync(envelopes, ct))
            if (Currency.TryCreate(item.Currency ?? "", out var currency))
                currencies[id] = currency;
        var export = await holdings.ExportAsync(currencies.Keys.ToList(), ct);
        // The snapshot each estimate starts from: the latest on or before today.
        var bases = export.Snapshots.Where(s => s.AsOf <= today).GroupBy(s => s.Envelope).ToDictionary(g => g.Key, g => g.Max(s => s.AsOf));
        var from = bases.Count == 0 ? today : bases.Values.Min();

        // Every envelope with a currency, so that a caller without the right is refused whether or not it has a snapshot.
        var series = currencies.Count == 0 ? new Dictionary<ItemId, IReadOnlyList<Valuation>>()
            : (await valuations.SeriesManyAsync(currencies.Keys.ToList(), DateRange.Create(from, null).TryGetValue(out var range) ? range : throw new InvalidOperationException(), ct))
                .TryGetValue(out var read) ? read : null;
        if (series is null)
            return envelopes.Distinct().ToDictionary(e => e, e => Missing(e, EstimateReasons.Unreadable));

        var priced = export.Holdings.Where(h => h.Isin is not null && HoldingWire.HasUnits(h.Kind) && bases.ContainsKey(h.Envelope)).ToList();
        var isins = priced.Select(h => h.Isin!).Distinct().ToList();
        var latest = isins.Count == 0 ? new Dictionary<string, LatestPrices>() : await prices.LatestAsync(isins, ct);
        var listings = isins.Count == 0 ? new Dictionary<string, SecurityListing>() : await prices.ListingsAsync(isins, ct);
        var history = (isins.Count == 0 ? Array.Empty<SecurityPrice>() : await prices.AcceptedOnOrBeforeAsync(isins, from, today, ct))
            .GroupBy(p => p.Isin).ToDictionary(g => g.Key, g => g.OrderBy(p => p.OnDate).ToList());

        var quotes = latest.Values.Select(l => l.Accepted).OfType<SecurityPrice>().Concat(history.Values.SelectMany(h => h))
            .Select(p => p.Currency).Concat(bases.Keys.Select(e => currencies[e])).Distinct().ToList();
        var ratesFrom = latest.Values.Select(l => l.Accepted?.OnDate ?? from).Append(from).Min();
        var rates = quotes.Count <= 1 ? RateTable.Empty(today, 0) : await fx.LoadAsync(quotes, ratesFrom, today, ct);
        decimal? Convert(SecurityPrice p, Currency to) => rates.TryConvert(p.Price, p.Currency, to, p.OnDate, projected: false, out var x, out _) ? x : null;

        var result = new Dictionary<ItemId, EnvelopeEstimate>();
        foreach (var (envelope, currency) in currencies)
        {
            var mine = export.Holdings.Where(h => h.Envelope == envelope).ToList();
            var snapshots = export.Snapshots.Where(s => s.Envelope == envelope).ToList();
            var movements = export.Movements.Where(m => m.Envelope == envelope).ToList();
            var values = series.GetValueOrDefault(envelope) ?? [];
            var asOf = bases.GetValueOrDefault(envelope);
            var holdingPrices = new Dictionary<Guid, HoldingPrice>();
            foreach (var h in priced.Where(h => h.Envelope == envelope))
            {
                var last = latest.GetValueOrDefault(h.Isin!);
                var accepted = last?.Accepted;
                var price = accepted is null ? null : Convert(accepted, currency);
                // A refused price blocks the holding only while no accepted one is usable (newer than the snapshot, fresh);
                // a missing rate matters only for a usable price, the domain says why the others are not.
                var usable = accepted is not null && accepted.OnDate > asOf && accepted.OnDate >= today.PlusDays(-Domain.EnvelopeEstimate.FreshDays);
                var reason = usable ? (price is null ? EstimateReasons.RateMissing : null)
                    : last?.Rejected is not null ? EstimateReasons.RejectedPrice
                    : listings.GetValueOrDefault(h.Isin!)?.NotFoundAt is not null ? EstimateReasons.NotFound
                    : null;
                var dated = history.GetValueOrDefault(h.Isin!) ?? [];
                holdingPrices[h.Id] = new HoldingPrice(price, accepted?.OnDate, reason,
                    d => dated.LastOrDefault(p => p.OnDate <= d) is { } p ? Convert(p, currency) : null, accepted is not null && accepted.Currency != currency);
            }

            var estimate = Domain.EnvelopeEstimate.Of(snapshots, values.FirstOrDefault(v => v.AsOf == asOf)?.Value.Amount,
                values.LastOrDefault(v => v.AsOf <= today)?.AsOf, mine, movements, holdingPrices, today);
            result[envelope] = new EnvelopeEstimate(envelope, estimate.Value is { } v ? Amounts.Of(v, currency) : null, estimate.AsOf, estimate.BasedOn,
                estimate.Converted, estimate.Stale, estimate.MissingReason, estimate.Lines);
        }
        foreach (var envelope in envelopes)
            result.TryAdd(envelope, Missing(envelope, EstimateReasons.Unreadable));
        return result;
    }

    private static EnvelopeEstimate Missing(ItemId envelope, string reason) => new(envelope, null, null, null, false, [], reason, []);
}
