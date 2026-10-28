using Nid.Core.Contracts;
using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// 2D1 spec §4: the reader's visible open envelopes — the sum of their latest values, and, for those with a value both
/// on or before 1 January and today, the gain since excluding contributions (as the period performance).
/// 2D2a: one <see cref="Holding"/> per envelope, named and owned as the registry says.
/// Holding prices spec §5.4: today's value is the envelope's estimate when <see cref="TodayValue"/> picks it, estimated as the reader
/// (the dashboard's actor) over the envelopes already read; the change since 1 January stays on the valuations.
/// </summary>
public sealed class EnvelopesNetWorth(
    IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items, IHoldingStore holdingStore,
    EnvelopeEstimator estimator, IHouseholdCalendar calendar) : INetWorthContribution
{
    public async Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        // Batched reads spec §3: the items and the facts read once for all. A block that cannot be read counts each envelope
        // as one whose own facts cannot be read.
        var accesses = await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct);
        var facts = await performance.FactsManyAsync(found, accesses, ct) ?? new Dictionary<ItemId, EnvelopePerformance.Facts>();
        // Issue #104: the snapshots and holdings of every envelope in one export, for the allocation.
        var export = await holdingStore.ExportAsync(found.Select(e => e.ItemId).ToList(), ct);
        var holdingsOf = export.Holdings.ToDictionary(h => h.Id);
        var estimates = await estimator.EstimateAsync(found.Select(e => e.ItemId).ToList(), ct);
        var values = new Dictionary<Currency, decimal>();
        var starts = new Dictionary<Currency, decimal>();
        var nets = new Dictionary<Currency, decimal>();
        var gains = new Dictionary<Currency, decimal>();
        var holdings = new List<Holding>();
        int count = 0, withoutValue = 0, stale = 0, withoutStart = 0;
        LocalDate? oldest = null;

        foreach (var envelope in found)
        {
            count++;
            var item = accesses.GetValueOrDefault(envelope.ItemId);
            var name = item?.DisplayName ?? string.Empty;
            IReadOnlyList<OwnerShare> owners = item?.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList() ?? [];
            // Facts that cannot be read: counted as without value (the tile says "N enveloppes sans valeur"),
            // never skipped, so the total is never silently partial.
            if (!facts.TryGetValue(envelope.ItemId, out var f))
            {
                withoutValue++;
                holdings.Add(new Holding(envelope.ItemId, "investments", name, null, null, false, owners));
                continue;
            }
            var figures = EnvelopeMath.At(f!.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            if (figures.Value is null)
            {
                withoutValue++;
                holdings.Add(new Holding(envelope.ItemId, "investments", name, null, null, false, owners));
                continue;
            }
            var (value, asOf, estimated, lines) = TodayValue.Pick(figures, estimates.GetValueOrDefault(envelope.ItemId));
            Add(values, value.Currency, value.Amount);
            if (oldest is null || asOf < oldest) oldest = asOf;
            // D17: stale follows the valuation, estimated or not.
            if (figures.StaleDays is not null) stale++;
            holdings.Add(new Holding(envelope.ItemId, "investments", name, value, asOf, figures.StaleDays is not null, owners,
                EnvelopeAllocation.Of(f.Envelope.Kind, value.Amount, EnvelopeAllocation.Latest(export.Snapshots, envelope.ItemId, today), holdingsOf, lines),
                estimated, figures.AsOf));

            if (SinceJanuary(f, today) is { StartValue: { } start, NetContributions: { } net, Performance: { } gain })
            {
                Add(starts, start.Currency, start.Amount);
                Add(nets, net.Currency, net.Amount);
                Add(gains, gain.Currency, gain.Amount);
            }
            else
                withoutStart++;
        }

        return Result.Success(new NetWorthPart(count, withoutValue, stale, oldest, values, starts, nets, gains, holdings, withoutStart));
    }

    /// <summary>
    /// The gain since 1 January excluding contributions, as the period performance; null without a start value, or with one older
    /// than the envelope's own staleness threshold (2D2b decision 3: it would fold several years into « the year's gain »; a null
    /// threshold, no cadence, sets no limit). Shared with the investments overview (dashboard spec D3).
    /// </summary>
    internal static PeriodPerformance? SinceJanuary(EnvelopePerformance.Facts f, LocalDate today)
    {
        var january1 = new LocalDate(today.Year, 1, 1);
        var period = EnvelopeMath.Period(f.Values, f.Flows, f.Currency, january1, today);
        var limit = EnvelopeMath.StaleThreshold(f.Envelope.Cadence, f.Envelope.StaleAfterDays);
        var recent = period.StartAsOf is { } startAsOf
            && (limit is not { } days || NodaTime.Period.DaysBetween(startAsOf, january1) <= days);
        return recent && period.Performance is not null ? period : null;
    }

    /// <summary>
    /// 2D2b spec §4: at each date, each envelope's last reading on or before it, with the net contributions up to that
    /// reading (as <see cref="EnvelopeMath.Series"/>), so the gap between the two lines stays the gain. A closed
    /// envelope counts strictly before its closing; one whose facts cannot be read counts as having no reading.
    /// Holding prices spec §5.4: at today's date only, the reading's value is the estimate when <see cref="TodayValue"/> picks it.
    /// </summary>
    public async Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        var facts = await performance.FactsManyAsync(found, await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct), ct)
            ?? new Dictionary<ItemId, EnvelopePerformance.Facts>();
        var today = await calendar.TodayAsync(ct);
        IReadOnlyDictionary<ItemId, Prices.EnvelopeEstimate> estimates = dates.Contains(today)
            ? await estimator.EstimateAsync(found.Where(e => e.ClosedOn is not { } closed || today < closed).Select(e => e.ItemId).ToList(), ct)
            : new Dictionary<ItemId, Prices.EnvelopeEstimate>();
        var series = new List<(LocalDate? ClosedOn, IReadOnlyList<SeriesPointFigures> Readings, Money? Estimate)>();
        foreach (var envelope in found)
            if (facts.TryGetValue(envelope.ItemId, out var f))
            {
                Money? estimate = estimates.GetValueOrDefault(envelope.ItemId) is { Value: not null } e
                    && EnvelopeMath.At(f.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today) is { Value: not null } figures
                    && TodayValue.Pick(figures, e) is { Estimated: true } picked ? picked.Value : null;
                series.Add((f.Envelope.ClosedOn, EnvelopeMath.Series(f.Values, f.Flows, f.Currency), estimate));
            }

        var points = new List<NetWorthPoint>();
        foreach (var date in dates)
        {
            var values = new Dictionary<Currency, decimal>();
            var nets = new Dictionary<Currency, decimal>();
            var count = 0;
            foreach (var (closedOn, readings, estimate) in series)
            {
                if (closedOn is { } closed && date >= closed) continue;
                var reading = readings.LastOrDefault(r => r.AsOf <= date); // Series is ordered by date
                if (reading is null) continue;
                count++;
                var value = date == today && estimate is { } estimated ? estimated : reading.Value;
                Add(values, value.Currency, value.Amount);
                Add(nets, reading.NetContributions.Currency, reading.NetContributions.Amount);
            }
            points.Add(new NetWorthPoint(date, values, nets, count));
        }
        return Result.Success<IReadOnlyList<NetWorthPoint>>(points);
    }

    /// <summary>
    /// Projection spec §2.1: the contributions minus the withdrawals of every visible envelope, closed ones included, dated in the window,
    /// in the envelope's currency; one whose facts cannot be read counts for nothing.
    /// </summary>
    public async Task<Result<IReadOnlyDictionary<Currency, decimal>>> NetContributionsAsync(
        PersonId reader, VisibilityScope scope, LocalDate from, LocalDate to, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        var facts = await performance.FactsManyAsync(found, await items.FindManyAsync(found.Select(e => e.ItemId).ToList(), ct), ct)
            ?? new Dictionary<ItemId, EnvelopePerformance.Facts>();
        var sums = new Dictionary<Currency, decimal>();
        foreach (var envelope in found)
            if (facts.TryGetValue(envelope.ItemId, out var f))
                Add(sums, f.Currency, EnvelopeMath.SavedBetween(f.Flows, from, to));
        return Result.Success<IReadOnlyDictionary<Currency, decimal>>(sums);
    }

    private static void Add(Dictionary<Currency, decimal> sums, Currency currency, decimal amount)
        => sums[currency] = sums.GetValueOrDefault(currency) + amount;
}

/// <summary>
/// Issue #34 §2.7: an open holding of a visible open envelope maturing in the window (60 days, a letter must reach the insurer to choose where
/// the proceeds go). <c>Label</c> is the holding's name.
/// </summary>
public sealed class InvestmentsUpcoming(IVisibleItems visible, IItemRegistry items, IHoldingStore holdings) : IUpcomingEvents
{
    public async Task<Result<IReadOnlyList<UpcomingEvent>>> UpcomingAsync(PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
    {
        var maturing = await holdings.MaturingAsync((await visible.ForAsync(reader, scope, ct)).ToList(), today, until, ct);
        var accesses = await items.FindManyAsync(maturing.Select(m => m.Envelope).Distinct().ToList(), ct);
        return Result.Success<IReadOnlyList<UpcomingEvent>>(maturing
            .Where(m => accesses.ContainsKey(m.Envelope))
            .Select(m => new UpcomingEvent(m.MaturesOn, UpcomingKinds.HoldingMaturity, m.Envelope, "investments", accesses[m.Envelope].DisplayName, m.Name, null))
            .ToList());
    }
}
