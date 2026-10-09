using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// 2D1 spec §4: the reader's visible open envelopes — the sum of their latest values, and, for those with a value both
/// on or before 1 January and today, the gain since excluding contributions (as the period performance).
/// 2D2a: one <see cref="Holding"/> per envelope, named and owned as the registry says.
/// </summary>
public sealed class EnvelopesNetWorth(IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance, IItemRegistry items) : INetWorthContribution
{
    public async Task<Result<NetWorthPart>> NetWorthAsync(PersonId reader, VisibilityScope scope, LocalDate today, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, ct);
        var found = await envelopes.FindManyAsync(ids.ToList(), ct);
        var january1 = new LocalDate(today.Year, 1, 1);
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
            var item = await items.FindAsync(envelope.ItemId, ct);
            var name = item?.DisplayName ?? string.Empty;
            IReadOnlyList<OwnerShare> owners = item?.Owners.Select(o => new OwnerShare(o.PersonId, o.ShareBp)).ToList() ?? [];
            // Facts that cannot be read: counted as without value (the tile says "N enveloppes sans valeur"),
            // never skipped, so the total is never silently partial.
            if (!(await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out var f))
            {
                withoutValue++;
                holdings.Add(new Holding(envelope.ItemId, "investments", name, null, null, false, owners));
                continue;
            }
            var figures = EnvelopeMath.At(f!.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            if (figures.Value is not { } value)
            {
                withoutValue++;
                holdings.Add(new Holding(envelope.ItemId, "investments", name, null, null, false, owners));
                continue;
            }
            Add(values, value.Currency, value.Amount);
            if (figures.AsOf is { } asOf && (oldest is null || asOf < oldest)) oldest = asOf;
            if (figures.StaleDays is not null) stale++;
            holdings.Add(new Holding(envelope.ItemId, "investments", name, value, figures.AsOf, figures.StaleDays is not null, owners));

            var period = EnvelopeMath.Period(f.Values, f.Flows, f.Currency, january1, today);
            // 2D2b decision 3: a start value older than the envelope's own staleness threshold would fold several
            // years into « the year's gain »; a null threshold (no cadence) sets no limit.
            var limit = EnvelopeMath.StaleThreshold(f.Envelope.Cadence, f.Envelope.StaleAfterDays);
            var recent = period.StartAsOf is { } startAsOf
                && (limit is not { } days || NodaTime.Period.DaysBetween(startAsOf, january1) <= days);
            if (recent && period is { StartValue: { } start, NetContributions: { } net, Performance: { } gain })
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
    /// 2D2b spec §4: at each date, each envelope's last reading on or before it, with the net contributions up to that
    /// reading (as <see cref="EnvelopeMath.Series"/>), so the gap between the two lines stays the gain. A closed
    /// envelope counts strictly before its closing; one whose facts cannot be read counts as having no reading.
    /// </summary>
    public async Task<Result<IReadOnlyList<NetWorthPoint>>> NetWorthHistoryAsync(PersonId reader, VisibilityScope scope, IReadOnlyList<LocalDate> dates, CancellationToken ct)
    {
        var ids = await visible.ForAsync(reader, scope, includeArchived: true, ct);
        var series = new List<(LocalDate? ClosedOn, IReadOnlyList<SeriesPointFigures> Readings)>();
        foreach (var envelope in await envelopes.FindManyAsync(ids.ToList(), ct))
            if ((await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out var f))
                series.Add((f!.Envelope.ClosedOn, EnvelopeMath.Series(f.Values, f.Flows, f.Currency)));

        var points = new List<NetWorthPoint>();
        foreach (var date in dates)
        {
            var values = new Dictionary<Currency, decimal>();
            var nets = new Dictionary<Currency, decimal>();
            var count = 0;
            foreach (var (closedOn, readings) in series)
            {
                if (closedOn is { } closed && date >= closed) continue;
                var reading = readings.LastOrDefault(r => r.AsOf <= date); // Series is ordered by date
                if (reading is null) continue;
                count++;
                Add(values, reading.Value.Currency, reading.Value.Amount);
                Add(nets, reading.NetContributions.Currency, reading.NetContributions.Amount);
            }
            points.Add(new NetWorthPoint(date, values, nets, count));
        }
        return Result.Success<IReadOnlyList<NetWorthPoint>>(points);
    }

    private static void Add(Dictionary<Currency, decimal> sums, Currency currency, decimal amount)
        => sums[currency] = sums.GetValueOrDefault(currency) + amount;
}
