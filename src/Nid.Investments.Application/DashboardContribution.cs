using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Investments.Application;

/// <summary>
/// 2D1 spec §4: the reader's visible open envelopes — the sum of their latest values, and, for those with a value both
/// on or before 1 January and today, the gain since excluding contributions (as the period performance).
/// </summary>
public sealed class EnvelopesNetWorth(IVisibleItems visible, IEnvelopeStore envelopes, EnvelopePerformance performance) : INetWorthContribution
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
        int count = 0, withoutValue = 0, stale = 0;
        LocalDate? oldest = null;

        foreach (var envelope in found)
        {
            // As the envelopes' list: an envelope whose facts cannot be read is left out rather than failing the page.
            if (!(await performance.FactsAsync(envelope.ItemId, ct)).TryGetValue(out var f))
                continue;
            count++;
            var figures = EnvelopeMath.At(f!.Values, f.Flows, f.Currency, f.Envelope.Cadence, f.Envelope.StaleAfterDays, today);
            if (figures.Value is not { } value)
            {
                withoutValue++;
                continue;
            }
            Add(values, value.Currency, value.Amount);
            if (figures.AsOf is { } asOf && (oldest is null || asOf < oldest)) oldest = asOf;
            if (figures.StaleDays is not null) stale++;

            var period = EnvelopeMath.Period(f.Values, f.Flows, f.Currency, january1, today);
            if (period is { StartValue: { } start, NetContributions: { } net, Performance: { } gain })
            {
                Add(starts, start.Currency, start.Amount);
                Add(nets, net.Currency, net.Amount);
                Add(gains, gain.Currency, gain.Amount);
            }
        }

        return Result.Success(new NetWorthPart(count, withoutValue, stale, oldest, values, starts, nets, gains));
    }

    private static void Add(Dictionary<Currency, decimal> sums, Currency currency, decimal amount)
        => sums[currency] = sums.GetValueOrDefault(currency) + amount;
}
