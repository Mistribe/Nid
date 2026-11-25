using Nid.Core.Contracts;

namespace Nid.Core.Domain.Fx;

/// <summary>
/// Design §4.4: a collected rate that moved more than the threshold since the previous stored one is refused, for its quote only —
/// unless the day before refused a rate within the threshold of it (<paramref name="refusedTheDayBefore"/>, by quote code): two days at
/// the new level confirm a lasting move, which becomes the reference (#633).
/// </summary>
public static class FxPlausibility
{
    public static FxReading Check(
        IReadOnlyList<FxRate> candidates, IReadOnlyDictionary<Currency, decimal> previous, IReadOnlyDictionary<string, decimal> refusedTheDayBefore,
        decimal maxChange)
    {
        var accepted = new List<FxRate>(candidates.Count);
        var rejected = new List<FxRejection>();
        foreach (var rate in candidates)
        {
            if (previous.TryGetValue(rate.Quote, out var before) && Math.Abs(rate.Rate / before - 1m) > maxChange
                && !(refusedTheDayBefore.TryGetValue(rate.Quote.Code, out var refused) && Math.Abs(rate.Rate / refused - 1m) <= maxChange))
                rejected.Add(new FxRejection(rate.OnDate, rate.Quote.Code, rate.Rate, before, FxCodes.ImplausibleChange));
            else
                accepted.Add(rate);
        }

        return new FxReading(accepted, rejected);
    }
}
